using FFXIVMomoTalk.Migrations.Messages;
using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal sealed partial class MessageStore(string charactersDirectory, Action<string>? onInfo = null)
    : CharacterSqliteStore(charactersDirectory, "messages.sqlite3", "message history", SchemaVersion, Schema, Indexes, [V1ToV2.Migration], onInfo)
{
    internal const long MergeIntervalMs = 500;
    private const int PageSize = 40;
    private const int SchemaVersion = 2;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS conversations (
            id INTEGER PRIMARY KEY,
            kind INTEGER NOT NULL,
            stable_key TEXT NOT NULL,
            display_name TEXT NOT NULL,
            world_id INTEGER NOT NULL,
            world_name TEXT NOT NULL,
            last_message_id INTEGER,
            last_sent_at INTEGER,
            UNIQUE (kind, stable_key)
        );

        CREATE TABLE IF NOT EXISTS messages (
            id INTEGER PRIMARY KEY,
            conversation_id INTEGER NOT NULL REFERENCES conversations (id),
            sent_at INTEGER NOT NULL,
            outgoing INTEGER NOT NULL,
            sender_content_id INTEGER NOT NULL,
            sender_name TEXT NOT NULL,
            sender_world_id INTEGER NOT NULL,
            sender_world_name TEXT NOT NULL,
            body TEXT NOT NULL,
            kind INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS message_rejections (
            message_id INTEGER PRIMARY KEY REFERENCES messages (id),
            reason TEXT NOT NULL
        );

        """;

    private const string Indexes = """
        CREATE INDEX IF NOT EXISTS message_page ON messages (conversation_id, sent_at, id);
        CREATE INDEX IF NOT EXISTS message_updates ON messages (conversation_id, id);
        """;

    private const string SelectMessages = """
        SELECT m.id, m.sent_at, m.outgoing,
               m.sender_content_id, m.sender_name, m.sender_world_id, m.sender_world_name,
               m.body, r.reason, m.kind
        FROM messages m JOIN conversations c ON c.id = m.conversation_id
        LEFT JOIN message_rejections r ON r.message_id = m.id
        WHERE c.kind = $kind AND c.stable_key = $stableKey
        """;

    private static readonly PlayerIdentity SystemSender = new(0, string.Empty, 0, string.Empty);
    private long revision;
    private long rejectionRevision;

    public long Revision => Interlocked.Read(ref revision);

    public long RejectionRevision => Interlocked.Read(ref rejectionRevision);

    public Task<long> RecordAsync(CapturedMessage captured, bool updateDisplayName = true) =>
        RecordCoreAsync(captured, updateDisplayName, rejection: null, onlyIfConversationExists: false, deduplicateBody: false);

    public Task<long> RecordExistingOnlyAsync(CapturedMessage captured, bool updateDisplayName = false) =>
        RecordCoreAsync(captured, updateDisplayName, rejection: null, onlyIfConversationExists: true, deduplicateBody: false);

    public Task<long> RecordUniqueSystemAsync(CapturedMessage captured, bool updateDisplayName = true) =>
        RecordCoreAsync(captured, updateDisplayName, rejection: null, onlyIfConversationExists: false, deduplicateBody: true);

    public Task<long> RecordFailedTellAsync(CapturedMessage attempt, string reason) =>
        RecordCoreAsync(attempt, updateDisplayName: false, reason, onlyIfConversationExists: false, deduplicateBody: false);

    private Task<long> RecordCoreAsync(
        CapturedMessage captured,
        bool updateDisplayName,
        string? rejection,
        bool onlyIfConversationExists,
        bool deduplicateBody) =>
        Enqueue(captured.Session.ContentId, connection =>
        {
            using var transaction = connection.BeginTransaction();
            long conversationId;
            long? lastMessageId;
            long? lastSentAt;
            if (onlyIfConversationExists)
            {
                if (!TryFindConversation(connection, transaction, captured.Conversation.Key, out conversationId, out lastMessageId, out lastSentAt))
                {
                    transaction.Commit();
                    return 0L;
                }

                if (updateDisplayName)
                    UpdateConversationDescriptor(connection, transaction, conversationId, captured.Conversation);
            }
            else
            {
                (conversationId, lastMessageId, lastSentAt) =
                    UpsertConversation(connection, transaction, captured, updateDisplayName);
            }

            if (deduplicateBody && HasSilentSystemBody(connection, transaction, conversationId, captured.Body))
            {
                transaction.Commit();
                return 0L;
            }

            if (captured.Kind == MessageKind.Chat
                && !captured.Outgoing
                && lastMessageId is { } mergeTargetId
                && lastSentAt is { } previousSentAt
                && ShouldMergeInterval(previousSentAt, captured.SentAt)
                && CanMergeIncomingMessage(connection, transaction, conversationId, mergeTargetId, captured.Sender))
            {
                using var append = connection.CreateCommand();
                append.Transaction = transaction;
                append.CommandText = "UPDATE messages SET body = body || $append WHERE id = $id";
                append.Parameters.AddWithValue("$append", "\n" + captured.Body);
                append.Parameters.AddWithValue("$id", mergeTargetId);
                append.ExecuteNonQuery();

                if (captured.SentAt > previousSentAt)
                {
                    using var updateLatest = connection.CreateCommand();
                    updateLatest.Transaction = transaction;
                    updateLatest.CommandText = """
                        UPDATE conversations SET last_sent_at = $time
                        WHERE id = $conversation AND last_message_id = $id
                        """;
                    updateLatest.Parameters.AddWithValue("$conversation", conversationId);
                    updateLatest.Parameters.AddWithValue("$time", captured.SentAt);
                    updateLatest.Parameters.AddWithValue("$id", mergeTargetId);
                    updateLatest.ExecuteNonQuery();
                }

                transaction.Commit();
                Interlocked.Increment(ref revision);
                return mergeTargetId;
            }

            var messageId = InsertMessageRow(connection, transaction, conversationId, captured);
            if (rejection is not null)
                InsertRejection(connection, messageId, rejection, transaction);

            transaction.Commit();
            Interlocked.Increment(ref revision);
            return messageId;
        });

    public Task RecordRejectionAsync(ulong owner, Task<long> echo, string reason) => Enqueue(owner, connection =>
    {
        if (!echo.IsCompletedSuccessfully)
            return false;

        if (InsertRejection(connection, echo.Result, reason))
        {
            Interlocked.Increment(ref rejectionRevision);
            Interlocked.Increment(ref revision);
        }

        return true;
    });

    private static bool InsertRejection(SqliteConnection connection, long messageId, string reason, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_rejections (message_id, reason) VALUES ($message, $reason)
            ON CONFLICT (message_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$reason", reason);
        return command.ExecuteNonQuery() != 0;
    }

    public Task<IReadOnlyList<ConversationSummary>> ReadConversationsAsync(ulong owner) =>
        Enqueue<IReadOnlyList<ConversationSummary>>(owner, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.id, m.sent_at, m.outgoing,
                       m.sender_content_id, m.sender_name, m.sender_world_id, m.sender_world_name,
                       m.body, r.reason, m.kind,
                       c.kind, c.stable_key, c.display_name, c.world_id, c.world_name, c.last_sent_at
                FROM conversations c JOIN messages m ON m.id = c.last_message_id
                LEFT JOIN message_rejections r ON r.message_id = m.id
                """;
            using var reader = command.ExecuteReader();
            var conversations = new List<ConversationSummary>();
            while (reader.Read())
            {
                var key = new ConversationKey((ConversationKind)reader.GetInt32(10), reader.GetString(11));
                var descriptor = new ConversationDescriptor(
                    key,
                    reader.GetString(12),
                    (uint)reader.GetInt64(13),
                    reader.GetString(14));
                conversations.Add(new ConversationSummary(descriptor, ReadMessage(reader), reader.GetInt64(15)));
            }

            return conversations;
        });

    public Task<MessagePage> ReadPageAsync(ulong owner, ConversationKey key, MessageCursor? before = null) =>
        Enqueue(owner, connection =>
        {
            using var command = connection.CreateCommand();
            BindConversationKey(command, key);
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

    public Task<IReadOnlyList<ChatMessage>> ReadAiContextAsync(ulong owner, ConversationKey key, int maxCount) =>
        Enqueue<IReadOnlyList<ChatMessage>>(owner, connection =>
        {
            if (maxCount <= 0)
                return [];

            using var command = connection.CreateCommand();
            BindConversationKey(command, key);
            command.CommandText = SelectMessages + "\nAND r.reason IS NULL AND m.kind <> $silentKind ORDER BY m.sent_at DESC, m.id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$silentKind", (int)MessageKind.SilentSystem);
            command.Parameters.AddWithValue("$limit", maxCount);
            var messages = ReadMessages(command);
            messages.Reverse();
            return messages;
        });

    public Task<IReadOnlyList<ChatMessage>> ReadUpdatesAsync(
        ulong owner, ConversationKey key, long afterId, long latestDisplayedId, bool includeRejections) =>
        Enqueue<IReadOnlyList<ChatMessage>>(owner, connection =>
        {
            using var command = connection.CreateCommand();
            // A merge can update the conversation's latest message without allocating a new ID.
            command.CommandText = SelectMessages + "\nAND m.id > $after"
                + "\nUNION ALL\n" + SelectMessages
                + "\nAND m.id <= $after AND m.id IN ($displayed, c.last_message_id)";
            if (includeRejections)
            {
                command.CommandText += "\nUNION ALL\n" + SelectMessages
                    + "\nAND m.id <= $after AND r.message_id IS NOT NULL"
                    + "\nAND m.id NOT IN ($displayed, c.last_message_id)";
            }

            BindConversationKey(command, key);
            command.Parameters.AddWithValue("$after", afterId);
            command.Parameters.AddWithValue("$displayed", latestDisplayedId);
            return ReadMessages(command);
        });

    internal static bool ShouldMergeInterval(long previousSentAt, long currentSentAt)
    {
        var interval = currentSentAt - previousSentAt;
        return interval >= 0 && interval < MergeIntervalMs;
    }

    private static bool IsSameSender(
        ulong previousContentId,
        string previousName,
        uint previousWorldId,
        string previousWorldName,
        PlayerIdentity currentSender)
    {
        if (previousContentId != 0 && currentSender.ContentId != 0)
            return previousContentId == currentSender.ContentId;

        if (previousWorldId != currentSender.WorldId)
            return false;

        if (previousWorldId == 0
            && !StringComparer.OrdinalIgnoreCase.Equals(previousWorldName.Trim(), currentSender.World.Trim()))
        {
            return false;
        }

        return StringComparer.OrdinalIgnoreCase.Equals(previousName.Trim(), currentSender.Name.Trim());
    }

    private static long InsertMessageRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long conversationId,
        CapturedMessage captured)
    {
        var messageId = InsertRawMessageRow(
            connection,
            transaction,
            conversationId,
            captured.SentAt,
            captured.Outgoing,
            captured.Sender,
            captured.Body,
            captured.Kind);

        if (!captured.IsSilentSystem)
        {
            using var latest = connection.CreateCommand();
            latest.Transaction = transaction;
            latest.CommandText = """
                UPDATE conversations SET last_sent_at = $time, last_message_id = $message
                WHERE id = $conversation AND
                    (last_message_id IS NULL OR (last_sent_at, last_message_id) < ($time, $message))
                """;
            latest.Parameters.AddWithValue("$conversation", conversationId);
            latest.Parameters.AddWithValue("$time", captured.SentAt);
            latest.Parameters.AddWithValue("$message", messageId);
            latest.ExecuteNonQuery();
        }

        return messageId;
    }

    private static long InsertSilentSystemRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long conversationId,
        long sentAt,
        bool outgoing,
        PlayerIdentity sender,
        string body) =>
        InsertRawMessageRow(connection, transaction, conversationId, sentAt, outgoing, sender, body, MessageKind.SilentSystem);

    private static long InsertRawMessageRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long conversationId,
        long sentAt,
        bool outgoing,
        PlayerIdentity sender,
        string body,
        MessageKind kind)
    {
        using var message = connection.CreateCommand();
        message.Transaction = transaction;
        message.CommandText = """
            INSERT INTO messages (
                conversation_id, sent_at, outgoing,
                sender_content_id, sender_name, sender_world_id, sender_world_name, body, kind)
            VALUES ($conversation, $time, $outgoing, $senderCid, $senderName, $senderWorld, $senderWorldName, $body, $kind)
            RETURNING id
            """;
        message.Parameters.AddWithValue("$conversation", conversationId);
        message.Parameters.AddWithValue("$time", sentAt);
        message.Parameters.AddWithValue("$outgoing", outgoing);
        BindContentId(message, "$senderCid", sender.ContentId);
        message.Parameters.AddWithValue("$senderName", sender.Name);
        message.Parameters.AddWithValue("$senderWorld", (long)sender.WorldId);
        message.Parameters.AddWithValue("$senderWorldName", sender.World);
        message.Parameters.AddWithValue("$body", body);
        message.Parameters.AddWithValue("$kind", (int)kind);
        return (long)message.ExecuteScalar()!;
    }

    private static bool TryFindConversation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationKey key,
        out long conversationId,
        out long? lastMessageId,
        out long? lastSentAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, last_message_id, last_sent_at
            FROM conversations
            WHERE kind = $kind AND stable_key = $stableKey
            """;
        BindConversationKey(command, key);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            conversationId = 0;
            lastMessageId = null;
            lastSentAt = null;
            return false;
        }

        conversationId = reader.GetInt64(0);
        lastMessageId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        lastSentAt = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        return true;
    }

    private static void UpdateConversationDescriptor(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long conversationId,
        ConversationDescriptor descriptor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversations
            SET display_name = $name, world_id = $world, world_name = $worldName
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$name", descriptor.DisplayName);
        command.Parameters.AddWithValue("$world", (long)descriptor.WorldId);
        command.Parameters.AddWithValue("$worldName", descriptor.WorldName);
        command.ExecuteNonQuery();
    }

    private static bool HasSilentSystemBody(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long conversationId,
        string body)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM messages
            WHERE conversation_id = $conversation AND kind = $silentKind AND body = $body
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$silentKind", (int)MessageKind.SilentSystem);
        command.Parameters.AddWithValue("$body", body);
        return command.ExecuteScalar() is not null;
    }

    private static (long ConversationId, long? LastMessageId, long? LastSentAt) UpsertConversation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapturedMessage captured,
        bool updateDisplayName)
    {
        using var conversation = connection.CreateCommand();
        conversation.Transaction = transaction;
        conversation.CommandText = updateDisplayName
            ? """
                INSERT INTO conversations (kind, stable_key, display_name, world_id, world_name)
                VALUES ($kind, $stableKey, $name, $world, $worldName)
                ON CONFLICT (kind, stable_key) DO UPDATE SET
                    display_name = excluded.display_name,
                    world_id = excluded.world_id,
                    world_name = excluded.world_name
                RETURNING id, last_message_id, last_sent_at
                """
            : """
                INSERT INTO conversations (kind, stable_key, display_name, world_id, world_name)
                VALUES ($kind, $stableKey, $name, $world, $worldName)
                ON CONFLICT (kind, stable_key) DO UPDATE SET
                    world_id = excluded.world_id,
                    world_name = excluded.world_name
                RETURNING id, last_message_id, last_sent_at
                """;
        BindConversationKey(conversation, captured.Conversation.Key);
        conversation.Parameters.AddWithValue("$name", captured.Conversation.DisplayName);
        conversation.Parameters.AddWithValue("$world", (long)captured.Conversation.WorldId);
        conversation.Parameters.AddWithValue("$worldName", captured.Conversation.WorldName);

        using var reader = conversation.ExecuteReader();
        reader.Read();
        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static bool CanMergeIncomingMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long conversationId,
        long messageId,
        PlayerIdentity sender)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.sender_content_id, m.sender_name, m.sender_world_id, m.sender_world_name
            FROM messages m
            WHERE m.id = $id AND m.outgoing = 0 AND m.kind = 0
              AND NOT EXISTS (
                  SELECT 1 FROM messages newer
                  WHERE newer.conversation_id = $conversation
                    AND (newer.sent_at, newer.id) > (m.sent_at, m.id)
              )
            """;
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$conversation", conversationId);

        using var reader = command.ExecuteReader();
        return reader.Read() && IsSameSender(
            ReadContentId(reader, 0),
            reader.GetString(1),
            (uint)reader.GetInt64(2),
            reader.GetString(3),
            sender);
    }

    private static List<ChatMessage> ReadMessages(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var messages = new List<ChatMessage>();
        while (reader.Read())
            messages.Add(ReadMessage(reader));

        return messages;
    }

    private static ChatMessage ReadMessage(SqliteDataReader reader)
    {
        var sender = new PlayerIdentity(
            ReadContentId(reader, 3),
            reader.GetString(4),
            (uint)reader.GetInt64(5),
            reader.GetString(6));
        return new ChatMessage(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            sender,
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            (MessageKind)reader.GetInt32(9));
    }
}
