using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal sealed class MessageStore(string charactersDirectory)
    : CharacterSqliteStore(charactersDirectory, "messages.sqlite3", "message history", SchemaVersion, Schema)
{
    internal const long MergeIntervalMs = 500;
    private const int PageSize = 40;
    private const int SchemaVersion = 1;

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
            body TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS message_rejections (
            message_id INTEGER PRIMARY KEY REFERENCES messages (id),
            reason TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS message_page ON messages (conversation_id, sent_at, id);
        """;

    private const string SelectMessages = """
        SELECT m.id, m.sent_at, m.outgoing,
               m.sender_content_id, m.sender_name, m.sender_world_id, m.sender_world_name,
               m.body, r.reason
        FROM messages m JOIN conversations c ON c.id = m.conversation_id
        LEFT JOIN message_rejections r ON r.message_id = m.id
        WHERE c.kind = $kind AND c.stable_key = $stableKey
        """;

    private long revision;
    private long rejectionRevision;

    public long Revision => Interlocked.Read(ref revision);

    public long RejectionRevision => Interlocked.Read(ref rejectionRevision);

    public Task<long> RecordAsync(CapturedMessage captured, bool updateDisplayName = true) =>
        Enqueue(captured.Session.ContentId, connection =>
        {
            using var transaction = connection.BeginTransaction();
            var (conversationId, lastMessageId, lastSentAt) =
                UpsertConversation(connection, transaction, captured, updateDisplayName);

            if (lastMessageId is { } mergeTargetId
                && lastSentAt is { } previousSentAt
                && ShouldMergeInterval(previousSentAt, captured.SentAt)
                && CanMergeIntoMessage(connection, transaction, mergeTargetId, captured))
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

            using var message = connection.CreateCommand();
            message.Transaction = transaction;
            message.CommandText = """
                INSERT INTO messages (
                    conversation_id, sent_at, outgoing,
                    sender_content_id, sender_name, sender_world_id, sender_world_name, body)
                VALUES ($conversation, $time, $outgoing, $senderCid, $senderName, $senderWorld, $senderWorldName, $body)
                RETURNING id
                """;
            message.Parameters.AddWithValue("$conversation", conversationId);
            message.Parameters.AddWithValue("$time", captured.SentAt);
            message.Parameters.AddWithValue("$outgoing", captured.Outgoing);
            BindContentId(message, "$senderCid", captured.Sender.ContentId);
            message.Parameters.AddWithValue("$senderName", captured.Sender.Name);
            message.Parameters.AddWithValue("$senderWorld", (long)captured.Sender.WorldId);
            message.Parameters.AddWithValue("$senderWorldName", captured.Sender.World);
            message.Parameters.AddWithValue("$body", captured.Body);
            var messageId = (long)message.ExecuteScalar()!;

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

            transaction.Commit();
            Interlocked.Increment(ref revision);
            return messageId;
        });

    public Task RecordRejectionAsync(ulong owner, Task<long> echo, string reason) => Enqueue(owner, connection =>
    {
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
                SELECT m.id, m.sent_at, m.outgoing,
                       m.sender_content_id, m.sender_name, m.sender_world_id, m.sender_world_name,
                       m.body, r.reason,
                       c.kind, c.stable_key, c.display_name, c.world_id, c.world_name
                FROM conversations c JOIN messages m ON m.id = c.last_message_id
                LEFT JOIN message_rejections r ON r.message_id = m.id
                """;
            using var reader = command.ExecuteReader();
            var conversations = new List<ConversationSummary>();
            while (reader.Read())
            {
                var key = new ConversationKey((ConversationKind)reader.GetInt32(9), reader.GetString(10));
                var descriptor = new ConversationDescriptor(
                    key,
                    reader.GetString(11),
                    (uint)reader.GetInt64(12),
                    reader.GetString(13));
                conversations.Add(new ConversationSummary(descriptor, ReadMessage(reader)));
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

    public Task<IReadOnlyList<ChatMessage>> ReadRecentAsync(ulong owner, ConversationKey key, int maxCount) =>
        Enqueue<IReadOnlyList<ChatMessage>>(owner, connection =>
        {
            if (maxCount <= 0)
                return [];

            using var command = connection.CreateCommand();
            BindConversationKey(command, key);
            command.CommandText = SelectMessages + "\nORDER BY m.sent_at DESC, m.id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", maxCount);
            var messages = ReadMessages(command);
            messages.Reverse();
            return messages;
        });

    public Task<IReadOnlyList<ChatMessage>> ReadUpdatesAsync(ulong owner, ConversationKey key, long afterId, bool includeRejections) =>
        Enqueue<IReadOnlyList<ChatMessage>>(owner, connection =>
        {
            using var command = connection.CreateCommand();
            var filter = includeRejections ? "AND (m.id >= $after OR r.message_id IS NOT NULL)" : "AND m.id >= $after";
            command.CommandText = SelectMessages + $"\n{filter}\nORDER BY m.sent_at, m.id";
            BindConversationKey(command, key);
            command.Parameters.AddWithValue("$after", afterId);
            return ReadMessages(command);
        });

    public Task UpdateFriendDescriptorsAsync(ulong owner, IReadOnlyList<PlayerIdentity> friends)
    {
        if (friends.Count == 0)
            return Task.CompletedTask;

        return Enqueue(owner, connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var updateConv = connection.CreateCommand();
            updateConv.Transaction = transaction;
            updateConv.CommandText = """
                UPDATE conversations
                SET display_name = $name, world_id = $world, world_name = $worldName
                WHERE kind = 0 AND stable_key = $stableKey
                  AND (display_name <> $name OR world_id <> $world OR world_name <> $worldName)
                """;
            var updateKey = updateConv.Parameters.Add("$stableKey", SqliteType.Text);
            var updateName = updateConv.Parameters.Add("$name", SqliteType.Text);
            var updateWorld = updateConv.Parameters.Add("$world", SqliteType.Integer);
            var updateWorldName = updateConv.Parameters.Add("$worldName", SqliteType.Text);

            var changed = false;
            foreach (var friend in friends)
            {
                updateKey.Value = ConversationKey.Tell(friend.ContentId).StableKey;
                updateName.Value = friend.Name;
                updateWorld.Value = (long)friend.WorldId;
                updateWorldName.Value = friend.World;
                if (updateConv.ExecuteNonQuery() != 0)
                    changed = true;
            }

            transaction.Commit();
            if (changed)
                Interlocked.Increment(ref revision);

            return changed;
        });
    }

    public Task UpdateChannelNamesAsync(ulong owner, ConversationKind kind, IReadOnlyList<ChannelInfo> channels)
    {
        var hasNamedChannel = false;
        foreach (var channel in channels)
        {
            if (channel.HasRealName)
            {
                hasNamedChannel = true;
                break;
            }
        }

        if (!hasNamedChannel)
            return Task.CompletedTask;

        return Enqueue(owner, connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var updateConv = connection.CreateCommand();
            updateConv.Transaction = transaction;
            updateConv.CommandText = """
                UPDATE conversations
                SET display_name = $name
                WHERE kind = $kind AND stable_key = $stableKey AND display_name <> $name
                """;
            var convKind = updateConv.Parameters.Add("$kind", SqliteType.Integer);
            var convKey = updateConv.Parameters.Add("$stableKey", SqliteType.Text);
            var convName = updateConv.Parameters.Add("$name", SqliteType.Text);

            var changed = false;
            foreach (var channel in channels)
            {
                if (!channel.HasRealName)
                    continue;

                convKind.Value = (int)kind;
                convKey.Value = channel.Key.StableKey;
                convName.Value = channel.Name;
                if (updateConv.ExecuteNonQuery() != 0)
                    changed = true;
            }

            transaction.Commit();
            if (changed)
                Interlocked.Increment(ref revision);

            return changed;
        });
    }

    public Task RenameConversationAsync(ulong owner, ConversationKey key, string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0)
            return Task.CompletedTask;

        return Enqueue(owner, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE conversations
                SET display_name = $name
                WHERE kind = $kind AND stable_key = $stableKey AND display_name <> $name
                """;
            BindConversationKey(command, key);
            command.Parameters.AddWithValue("$name", trimmed);
            var changed = command.ExecuteNonQuery() != 0;
            if (changed)
                Interlocked.Increment(ref revision);

            return changed;
        });
    }

    internal static bool ShouldMergeInterval(long previousSentAt, long currentSentAt)
    {
        var interval = currentSentAt - previousSentAt;
        return interval >= 0 && interval < MergeIntervalMs;
    }

    internal static bool IsSameSender(
        bool previousOutgoing,
        ulong previousContentId,
        string previousName,
        uint previousWorldId,
        string previousWorldName,
        bool currentOutgoing,
        PlayerIdentity currentSender)
    {
        if (previousOutgoing != currentOutgoing)
            return false;

        if (currentOutgoing)
            return true;

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

    private static bool CanMergeIntoMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        CapturedMessage captured)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.outgoing, m.sender_content_id, m.sender_name, m.sender_world_id, m.sender_world_name, r.message_id
            FROM messages m
            LEFT JOIN message_rejections r ON r.message_id = m.id
            WHERE m.id = $id
            """;
        command.Parameters.AddWithValue("$id", messageId);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || !reader.IsDBNull(5))
            return false;

        return IsSameSender(
            reader.GetBoolean(0),
            ReadContentId(reader, 1),
            reader.GetString(2),
            (uint)reader.GetInt64(3),
            reader.GetString(4),
            captured.Outgoing,
            captured.Sender);
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
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }
}
