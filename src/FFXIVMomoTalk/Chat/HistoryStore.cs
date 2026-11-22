using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal sealed class HistoryStore : IAsyncDisposable
{
    private const int PageSize = 40;

    private const string Schema = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS conversations (
            id INTEGER PRIMARY KEY,
            peer_key TEXT NOT NULL,
            world_id INTEGER NOT NULL,
            display_name TEXT NOT NULL,
            world_name TEXT NOT NULL,
            last_message_id INTEGER,
            last_sent_at INTEGER,
            UNIQUE (peer_key, world_id)
        );

        CREATE TABLE IF NOT EXISTS messages (
            id INTEGER PRIMARY KEY,
            conversation_id INTEGER NOT NULL REFERENCES conversations (id),
            sent_at INTEGER NOT NULL,
            outgoing INTEGER NOT NULL,
            body TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS message_rejections (
            message_id INTEGER PRIMARY KEY REFERENCES messages (id),
            reason TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS message_page ON messages (conversation_id, sent_at, id);
        """;

    private const string SelectMessages = """
        SELECT m.id, m.sent_at, m.outgoing, m.body, r.reason
        FROM messages m JOIN conversations c ON c.id = m.conversation_id
        LEFT JOIN message_rejections r ON r.message_id = m.id
        WHERE c.peer_key = $peer AND c.world_id = $world
        """;

    private readonly string directory;
    private readonly Channel<Action> requests = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task worker;
    private SqliteConnection? database;
    private ulong openedCharacter;
    private long revision;
    private long rejectionRevision;

    public HistoryStore(string directory)
    {
        this.directory = directory;
        worker = Task.Run(ProcessRequestsAsync);
    }

    public long Revision => Interlocked.Read(ref revision);

    public long RejectionRevision => Interlocked.Read(ref rejectionRevision);

    public Task<long> RecordAsync(CapturedTell tell) => Enqueue(tell.Session.ContentId, connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var conversation = connection.CreateCommand();
        conversation.Transaction = transaction;
        conversation.CommandText = """
            INSERT INTO conversations (peer_key, world_id, display_name, world_name)
            VALUES ($peer, $world, $name, $worldName)
            ON CONFLICT (peer_key, world_id) DO UPDATE SET
                display_name = excluded.display_name, world_name = excluded.world_name
            RETURNING id
            """;
        BindPeer(conversation, tell.Peer.Key);
        conversation.Parameters.AddWithValue("$name", tell.Peer.Name);
        conversation.Parameters.AddWithValue("$worldName", tell.Peer.World);
        var conversationId = (long)conversation.ExecuteScalar()!;

        using var message = connection.CreateCommand();
        message.Transaction = transaction;
        message.CommandText = """
            INSERT INTO messages (conversation_id, sent_at, outgoing, body)
            VALUES ($conversation, $time, $outgoing, $body)
            RETURNING id
            """;
        message.Parameters.AddWithValue("$conversation", conversationId);
        message.Parameters.AddWithValue("$time", tell.SentAt);
        message.Parameters.AddWithValue("$outgoing", tell.Outgoing);
        message.Parameters.AddWithValue("$body", tell.Body);
        var messageId = (long)message.ExecuteScalar()!;

        using var latest = connection.CreateCommand();
        latest.Transaction = transaction;
        latest.CommandText = """
            UPDATE conversations SET last_sent_at = $time, last_message_id = $message
            WHERE id = $conversation AND
                (last_message_id IS NULL OR (last_sent_at, last_message_id) < ($time, $message))
            """;
        latest.Parameters.AddWithValue("$conversation", conversationId);
        latest.Parameters.AddWithValue("$time", tell.SentAt);
        latest.Parameters.AddWithValue("$message", messageId);
        latest.ExecuteNonQuery();
        transaction.Commit();
        Interlocked.Increment(ref revision);
        return messageId;
    });

    public Task RecordRejectionAsync(ulong owner, Task<long> echo, string reason) => Enqueue(owner, connection =>
    {
        // The echo was enqueued before its capture event, so its write has already finished here.
        if (!echo.IsCompletedSuccessfully)
            return false;

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO message_rejections (message_id, reason) VALUES ($message, $reason)
            ON CONFLICT (message_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$message", echo.Result);
        command.Parameters.AddWithValue("$reason", reason);
        if (command.ExecuteNonQuery() != 0)
        {
            Interlocked.Increment(ref rejectionRevision);
            Interlocked.Increment(ref revision);
        }

        return true;
    });

    public Task<IReadOnlyList<ConversationSummary>> ReadConversationsAsync(ulong owner) =>
        Enqueue<IReadOnlyList<ConversationSummary>>(owner, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.id, m.sent_at, m.outgoing, m.body, r.reason, c.display_name, c.world_id, c.world_name
                FROM conversations c JOIN messages m ON m.id = c.last_message_id
                LEFT JOIN message_rejections r ON r.message_id = m.id
                """;
            using var reader = command.ExecuteReader();
            var conversations = new List<ConversationSummary>();
            while (reader.Read())
            {
                var peer = new PlayerIdentity(reader.GetString(5), (uint)reader.GetInt64(6), reader.GetString(7));
                conversations.Add(new ConversationSummary(peer, ReadMessage(reader)));
            }

            return conversations;
        });

    public Task<MessagePage> ReadPageAsync(ulong owner, PlayerKey peer, MessageCursor? before = null) =>
        Enqueue(owner, connection =>
        {
            using var command = connection.CreateCommand();
            BindPeer(command, peer);
            command.CommandText = SelectMessages;
            if (before is { } cursor)
            {
                command.CommandText += "\nAND (m.sent_at, m.id) < ($beforeTime, $beforeId)";
                command.Parameters.AddWithValue("$beforeTime", cursor.SentAt);
                command.Parameters.AddWithValue("$beforeId", cursor.Id);
            }

            command.CommandText += "\nORDER BY m.sent_at DESC, m.id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", PageSize + 1);
            var messages = ReadMessages(command);
            var hasOlder = messages.Count > PageSize;
            if (hasOlder)
                messages.RemoveAt(PageSize);

            messages.Reverse();
            return new MessagePage(messages, hasOlder);
        });

    public Task<IReadOnlyList<TellMessage>> ReadUpdatesAsync(ulong owner, PlayerKey peer, long afterId, bool includeRejections) =>
        Enqueue<IReadOnlyList<TellMessage>>(owner, connection =>
        {
            using var command = connection.CreateCommand();
            var filter = includeRejections ? "AND (m.id > $after OR r.message_id IS NOT NULL)" : "AND m.id > $after";
            command.CommandText = SelectMessages + $"\n{filter}\nORDER BY m.sent_at, m.id";
            BindPeer(command, peer);
            command.Parameters.AddWithValue("$after", afterId);
            return ReadMessages(command);
        });

    public async ValueTask DisposeAsync()
    {
        requests.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }

    private Task<T> Enqueue<T>(ulong owner, Func<SqliteConnection, T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!requests.Writer.TryWrite(() =>
        {
            try
            {
                completion.SetResult(operation(OpenDatabase(owner)));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }))
        {
            completion.SetException(new ObjectDisposedException(nameof(HistoryStore)));
        }

        return completion.Task;
    }

    private async Task ProcessRequestsAsync()
    {
        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync().ConfigureAwait(false))
                request();
        }
        finally
        {
            database?.Dispose();
        }
    }

    private SqliteConnection OpenDatabase(ulong owner)
    {
        if (database is not null && openedCharacter == owner)
            return database;

        database?.Dispose();
        database = null;
        Directory.CreateDirectory(directory);
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, $"{owner:X16}.sqlite3"),
            Pooling = false,
        };
        var next = new SqliteConnection(options.ToString());
        try
        {
            next.Open();
            using var command = next.CreateCommand();
            command.CommandText = Schema;
            command.ExecuteNonQuery();
            openedCharacter = owner;
            database = next;
            return next;
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    private static void BindPeer(SqliteCommand command, PlayerKey peer)
    {
        command.Parameters.AddWithValue("$peer", peer.Name);
        command.Parameters.AddWithValue("$world", (long)peer.WorldId);
    }

    private static List<TellMessage> ReadMessages(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var messages = new List<TellMessage>();
        while (reader.Read())
            messages.Add(ReadMessage(reader));

        return messages;
    }

    private static TellMessage ReadMessage(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetInt64(1), reader.GetBoolean(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
}
