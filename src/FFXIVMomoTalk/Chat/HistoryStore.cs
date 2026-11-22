using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal sealed class HistoryStore : IAsyncDisposable
{
    private const int PageSize = 40;
    private const int SchemaVersion = 2;
    private const string FriendsCachedMetaKey = "friends_cached_at";

    private const string Schema = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS conversations (
            id INTEGER PRIMARY KEY,
            peer_content_id INTEGER NOT NULL UNIQUE,
            display_name TEXT NOT NULL,
            world_id INTEGER NOT NULL,
            world_name TEXT NOT NULL,
            last_message_id INTEGER,
            last_sent_at INTEGER
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

        CREATE TABLE IF NOT EXISTS cached_friends (
            content_id INTEGER PRIMARY KEY,
            display_name TEXT NOT NULL,
            world_id INTEGER NOT NULL,
            world_name TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS friend_notes (
            content_id INTEGER PRIMARY KEY,
            note TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS hidden_conversations (
            content_id INTEGER PRIMARY KEY
        );

        CREATE TABLE IF NOT EXISTS store_meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS message_page ON messages (conversation_id, sent_at, id);
        """;

    private const string SelectMessages = """
        SELECT m.id, m.sent_at, m.outgoing, m.body, r.reason
        FROM messages m JOIN conversations c ON c.id = m.conversation_id
        LEFT JOIN message_rejections r ON r.message_id = m.id
        WHERE c.peer_content_id = $peer
        """;

    private readonly string directory;
    private readonly Channel<Action> requests = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task worker;
    private SqliteConnection? database;
    private ulong openedCharacter;
    private long revision;
    private long rejectionRevision;
    private long notesRevision;

    public HistoryStore(string directory)
    {
        this.directory = directory;
        worker = Task.Run(ProcessRequestsAsync);
    }

    public long Revision => Interlocked.Read(ref revision);

    public long RejectionRevision => Interlocked.Read(ref rejectionRevision);

    public long NotesRevision => Interlocked.Read(ref notesRevision);

    public Task<long> RecordAsync(CapturedTell tell) => Enqueue(tell.Session.ContentId, connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var conversation = connection.CreateCommand();
        conversation.Transaction = transaction;
        conversation.CommandText = """
            INSERT INTO conversations (peer_content_id, display_name, world_id, world_name)
            VALUES ($peer, $name, $world, $worldName)
            ON CONFLICT (peer_content_id) DO UPDATE SET
                display_name = excluded.display_name,
                world_id = excluded.world_id,
                world_name = excluded.world_name
            RETURNING id
            """;
        BindContentId(conversation, "$peer", tell.Peer.ContentId);
        conversation.Parameters.AddWithValue("$name", tell.Peer.Name);
        conversation.Parameters.AddWithValue("$world", (long)tell.Peer.WorldId);
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

        using var unhide = connection.CreateCommand();
        unhide.Transaction = transaction;
        unhide.CommandText = "DELETE FROM hidden_conversations WHERE content_id = $peer";
        BindContentId(unhide, "$peer", tell.Peer.ContentId);
        unhide.ExecuteNonQuery();

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
                SELECT m.id, m.sent_at, m.outgoing, m.body, r.reason,
                       c.peer_content_id, c.display_name, c.world_id, c.world_name
                FROM conversations c JOIN messages m ON m.id = c.last_message_id
                LEFT JOIN message_rejections r ON r.message_id = m.id
                WHERE NOT EXISTS (
                    SELECT 1 FROM hidden_conversations h WHERE h.content_id = c.peer_content_id
                )
                """;
            using var reader = command.ExecuteReader();
            var conversations = new List<ConversationSummary>();
            while (reader.Read())
            {
                var peer = new PlayerIdentity(
                    ReadContentId(reader, 5),
                    reader.GetString(6),
                    (uint)reader.GetInt64(7),
                    reader.GetString(8));
                conversations.Add(new ConversationSummary(peer, ReadMessage(reader)));
            }

            return conversations;
        });

    public Task HideConversationAsync(ulong owner, ulong peerContentId) => Enqueue(owner, connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO hidden_conversations (content_id) VALUES ($peer)";
        BindContentId(command, "$peer", peerContentId);
        if (command.ExecuteNonQuery() != 0)
            Interlocked.Increment(ref revision);

        return true;
    });

    public Task UnhideConversationAsync(ulong owner, ulong peerContentId) => Enqueue(owner, connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM hidden_conversations WHERE content_id = $peer";
        BindContentId(command, "$peer", peerContentId);
        if (command.ExecuteNonQuery() != 0)
            Interlocked.Increment(ref revision);

        return true;
    });

    public Task<IReadOnlyList<PlayerIdentity>?> ReadCachedFriendsAsync(ulong owner) =>
        Enqueue<IReadOnlyList<PlayerIdentity>?>(owner, connection =>
        {
            using var meta = connection.CreateCommand();
            meta.CommandText = "SELECT 1 FROM store_meta WHERE key = $key";
            meta.Parameters.AddWithValue("$key", FriendsCachedMetaKey);
            if (meta.ExecuteScalar() is null)
                return null;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content_id, display_name, world_id, world_name FROM cached_friends";
            using var reader = command.ExecuteReader();
            var friends = new List<PlayerIdentity>();
            while (reader.Read())
            {
                friends.Add(new PlayerIdentity(
                    ReadContentId(reader, 0),
                    reader.GetString(1),
                    (uint)reader.GetInt64(2),
                    reader.GetString(3)));
            }

            return friends;
        });

    public Task SyncFriendsAsync(ulong owner, IReadOnlyList<PlayerIdentity> friends) => Enqueue(owner, connection =>
    {
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM cached_friends";
            clear.ExecuteNonQuery();
        }

        var updatedConversations = false;
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO cached_friends (content_id, display_name, world_id, world_name)
            VALUES ($cid, $name, $world, $worldName)
            """;
        var insertCid = insert.Parameters.Add("$cid", SqliteType.Integer);
        var insertName = insert.Parameters.Add("$name", SqliteType.Text);
        var insertWorld = insert.Parameters.Add("$world", SqliteType.Integer);
        var insertWorldName = insert.Parameters.Add("$worldName", SqliteType.Text);

        using var updateConv = connection.CreateCommand();
        updateConv.Transaction = transaction;
        updateConv.CommandText = """
            UPDATE conversations
            SET display_name = $name, world_id = $world, world_name = $worldName
            WHERE peer_content_id = $cid
              AND (display_name <> $name OR world_id <> $world OR world_name <> $worldName)
            """;
        var updateCid = updateConv.Parameters.Add("$cid", SqliteType.Integer);
        var updateName = updateConv.Parameters.Add("$name", SqliteType.Text);
        var updateWorld = updateConv.Parameters.Add("$world", SqliteType.Integer);
        var updateWorldName = updateConv.Parameters.Add("$worldName", SqliteType.Text);

        foreach (var friend in friends)
        {
            var cid = unchecked((long)friend.ContentId);
            insertCid.Value = cid;
            insertName.Value = friend.Name;
            insertWorld.Value = (long)friend.WorldId;
            insertWorldName.Value = friend.World;
            insert.ExecuteNonQuery();

            updateCid.Value = cid;
            updateName.Value = friend.Name;
            updateWorld.Value = (long)friend.WorldId;
            updateWorldName.Value = friend.World;
            if (updateConv.ExecuteNonQuery() != 0)
                updatedConversations = true;
        }

        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = transaction;
            meta.CommandText = """
                INSERT INTO store_meta (key, value) VALUES ($key, $value)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value
                """;
            meta.Parameters.AddWithValue("$key", FriendsCachedMetaKey);
            meta.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            meta.ExecuteNonQuery();
        }

        transaction.Commit();
        if (updatedConversations)
            Interlocked.Increment(ref revision);

        return true;
    });

    public Task<IReadOnlyDictionary<ulong, string>> ReadNotesAsync(ulong owner) =>
        Enqueue<IReadOnlyDictionary<ulong, string>>(owner, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content_id, note FROM friend_notes";
            using var reader = command.ExecuteReader();
            var notes = new Dictionary<ulong, string>();
            while (reader.Read())
                notes[ReadContentId(reader, 0)] = reader.GetString(1);

            return notes;
        });

    public Task SaveNoteAsync(ulong owner, ulong peerContentId, string note) => Enqueue(owner, connection =>
    {
        var trimmed = note.Trim();
        using var command = connection.CreateCommand();
        if (trimmed.Length == 0)
        {
            command.CommandText = "DELETE FROM friend_notes WHERE content_id = $cid";
            BindContentId(command, "$cid", peerContentId);
        }
        else
        {
            command.CommandText = """
                INSERT INTO friend_notes (content_id, note) VALUES ($cid, $note)
                ON CONFLICT (content_id) DO UPDATE SET note = excluded.note
                """;
            BindContentId(command, "$cid", peerContentId);
            command.Parameters.AddWithValue("$note", trimmed);
        }

        command.ExecuteNonQuery();
        Interlocked.Increment(ref notesRevision);
        return true;
    });

    public Task<MessagePage> ReadPageAsync(ulong owner, ulong peerContentId, MessageCursor? before = null) =>
        Enqueue(owner, connection =>
        {
            using var command = connection.CreateCommand();
            BindContentId(command, "$peer", peerContentId);
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

    public Task<IReadOnlyList<TellMessage>> ReadUpdatesAsync(ulong owner, ulong peerContentId, long afterId, bool includeRejections) =>
        Enqueue<IReadOnlyList<TellMessage>>(owner, connection =>
        {
            using var command = connection.CreateCommand();
            var filter = includeRejections ? "AND (m.id > $after OR r.message_id IS NOT NULL)" : "AND m.id > $after";
            command.CommandText = SelectMessages + $"\n{filter}\nORDER BY m.sent_at, m.id";
            BindContentId(command, "$peer", peerContentId);
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
        var path = Path.Combine(directory, $"{owner:X16}.sqlite3");
        var existed = File.Exists(path) && new FileInfo(path).Length > 0;
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        };
        var next = new SqliteConnection(options.ToString());
        try
        {
            next.Open();
            if (existed)
            {
                using var versionCmd = next.CreateCommand();
                versionCmd.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(versionCmd.ExecuteScalar());
                if (version != SchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Unrecognized history database schema version {version} at '{path}' (expected {SchemaVersion}). Existing file was preserved.");
                }

                using var fk = next.CreateCommand();
                fk.CommandText = "PRAGMA foreign_keys = ON;";
                fk.ExecuteNonQuery();
            }
            else
            {
                using var command = next.CreateCommand();
                command.CommandText = $"{Schema}\nPRAGMA user_version = {SchemaVersion};";
                command.ExecuteNonQuery();
            }

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

    private static void BindContentId(SqliteCommand command, string name, ulong contentId) =>
        command.Parameters.AddWithValue(name, unchecked((long)contentId));

    private static ulong ReadContentId(SqliteDataReader reader, int ordinal) =>
        unchecked((ulong)reader.GetInt64(ordinal));

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
