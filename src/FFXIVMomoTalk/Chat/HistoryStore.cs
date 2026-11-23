using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal sealed class HistoryStore : IAsyncDisposable
{
    private const int PageSize = 40;
    private const int SchemaVersion = 5;
    private const string FriendsCachedMetaKey = "friends_cached_at";
    private const string FreeCompanyCachedMetaKey = "fc_cached_at";
    private const string LinkshellsCachedMetaKey = "ls_cached_at";
    private const string CrossWorldLinkshellsCachedMetaKey = "cwls_cached_at";

    private const string Schema = """
        PRAGMA foreign_keys = ON;

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

        CREATE TABLE IF NOT EXISTS cached_friends (
            content_id INTEGER PRIMARY KEY,
            display_name TEXT NOT NULL,
            world_id INTEGER NOT NULL,
            world_name TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS cached_channels (
            kind INTEGER NOT NULL,
            slot INTEGER NOT NULL,
            stable_key TEXT NOT NULL,
            display_name TEXT NOT NULL,
            has_real_name INTEGER NOT NULL,
            PRIMARY KEY (kind, slot)
        );

        CREATE TABLE IF NOT EXISTS conversation_notes (
            kind INTEGER NOT NULL,
            stable_key TEXT NOT NULL,
            note TEXT NOT NULL,
            PRIMARY KEY (kind, stable_key)
        );

        CREATE TABLE IF NOT EXISTS hidden_conversations (
            conversation_id INTEGER PRIMARY KEY REFERENCES conversations (id)
        );

        CREATE TABLE IF NOT EXISTS static_groups (
            stable_key TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            created_at INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS static_members (
            static_key TEXT NOT NULL REFERENCES static_groups (stable_key),
            content_id INTEGER NOT NULL,
            display_name TEXT NOT NULL,
            world_id INTEGER NOT NULL,
            world_name TEXT NOT NULL,
            PRIMARY KEY (static_key, content_id)
        );

        CREATE TABLE IF NOT EXISTS store_meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
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

    private readonly string directory;
    private readonly Channel<Action> requests = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task worker;
    private SqliteConnection? database;
    private ulong openedCharacter;
    private long revision;
    private long rejectionRevision;
    private long notesRevision;
    private long staticsRevision;

    public HistoryStore(string directory)
    {
        this.directory = directory;
        worker = Task.Run(ProcessRequestsAsync);
    }

    public long Revision => Interlocked.Read(ref revision);

    public long RejectionRevision => Interlocked.Read(ref rejectionRevision);

    public long NotesRevision => Interlocked.Read(ref notesRevision);

    public long StaticsRevision => Interlocked.Read(ref staticsRevision);

    public Task<long> RecordAsync(CapturedMessage captured, bool updateDisplayName = true) =>
        Enqueue(captured.Session.ContentId, connection =>
        {
            using var transaction = connection.BeginTransaction();
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
                    RETURNING id
                    """
                : """
                    INSERT INTO conversations (kind, stable_key, display_name, world_id, world_name)
                    VALUES ($kind, $stableKey, $name, $world, $worldName)
                    ON CONFLICT (kind, stable_key) DO UPDATE SET
                        world_id = excluded.world_id,
                        world_name = excluded.world_name
                    RETURNING id
                    """;
            BindConversationKey(conversation, captured.Conversation.Key);
            conversation.Parameters.AddWithValue("$name", captured.Conversation.DisplayName);
            conversation.Parameters.AddWithValue("$world", (long)captured.Conversation.WorldId);
            conversation.Parameters.AddWithValue("$worldName", captured.Conversation.WorldName);
            var conversationId = (long)conversation.ExecuteScalar()!;

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

            using var unhide = connection.CreateCommand();
            unhide.Transaction = transaction;
            unhide.CommandText = "DELETE FROM hidden_conversations WHERE conversation_id = $conversation";
            unhide.Parameters.AddWithValue("$conversation", conversationId);
            unhide.ExecuteNonQuery();

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
                WHERE NOT EXISTS (
                    SELECT 1 FROM hidden_conversations h WHERE h.conversation_id = c.id
                )
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

    public Task HideConversationAsync(ulong owner, ConversationKey key) => Enqueue(owner, connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO hidden_conversations (conversation_id)
            SELECT id FROM conversations WHERE kind = $kind AND stable_key = $stableKey
            """;
        BindConversationKey(command, key);
        if (command.ExecuteNonQuery() != 0)
            Interlocked.Increment(ref revision);

        return true;
    });

    public Task UnhideConversationAsync(ulong owner, ConversationKey key) => Enqueue(owner, connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM hidden_conversations
            WHERE conversation_id IN (
                SELECT id FROM conversations WHERE kind = $kind AND stable_key = $stableKey
            )
            """;
        BindConversationKey(command, key);
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
        var updatedStatics = false;
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
            WHERE kind = 0 AND stable_key = $stableKey
              AND (display_name <> $name OR world_id <> $world OR world_name <> $worldName)
            """;
        var updateKey = updateConv.Parameters.Add("$stableKey", SqliteType.Text);
        var updateName = updateConv.Parameters.Add("$name", SqliteType.Text);
        var updateWorld = updateConv.Parameters.Add("$world", SqliteType.Integer);
        var updateWorldName = updateConv.Parameters.Add("$worldName", SqliteType.Text);

        using var updateStaticMember = connection.CreateCommand();
        updateStaticMember.Transaction = transaction;
        updateStaticMember.CommandText = """
            UPDATE static_members
            SET display_name = $name, world_id = $world, world_name = $worldName
            WHERE content_id = $cid
              AND (display_name <> $name OR world_id <> $world OR world_name <> $worldName)
            """;
        var memberCid = updateStaticMember.Parameters.Add("$cid", SqliteType.Integer);
        var memberName = updateStaticMember.Parameters.Add("$name", SqliteType.Text);
        var memberWorld = updateStaticMember.Parameters.Add("$world", SqliteType.Integer);
        var memberWorldName = updateStaticMember.Parameters.Add("$worldName", SqliteType.Text);

        foreach (var friend in friends)
        {
            var cid = unchecked((long)friend.ContentId);
            insertCid.Value = cid;
            insertName.Value = friend.Name;
            insertWorld.Value = (long)friend.WorldId;
            insertWorldName.Value = friend.World;
            insert.ExecuteNonQuery();

            updateKey.Value = ConversationKey.Tell(friend.ContentId).StableKey;
            updateName.Value = friend.Name;
            updateWorld.Value = (long)friend.WorldId;
            updateWorldName.Value = friend.World;
            if (updateConv.ExecuteNonQuery() != 0)
                updatedConversations = true;

            memberCid.Value = cid;
            memberName.Value = friend.Name;
            memberWorld.Value = (long)friend.WorldId;
            memberWorldName.Value = friend.World;
            if (updateStaticMember.ExecuteNonQuery() != 0)
                updatedStatics = true;
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
        if (updatedStatics)
            Interlocked.Increment(ref staticsRevision);

        return true;
    });

    public Task<CachedChannels> ReadCachedChannelsAsync(ulong owner) =>
        Enqueue(owner, connection =>
        {
            var hasFc = false;
            var hasLs = false;
            var hasCwls = false;
            using (var meta = connection.CreateCommand())
            {
                meta.CommandText = "SELECT key FROM store_meta WHERE key IN ($fc, $ls, $cwls)";
                meta.Parameters.AddWithValue("$fc", FreeCompanyCachedMetaKey);
                meta.Parameters.AddWithValue("$ls", LinkshellsCachedMetaKey);
                meta.Parameters.AddWithValue("$cwls", CrossWorldLinkshellsCachedMetaKey);
                using var metaReader = meta.ExecuteReader();
                while (metaReader.Read())
                {
                    switch (metaReader.GetString(0))
                    {
                        case FreeCompanyCachedMetaKey:
                            hasFc = true;
                            break;
                        case LinkshellsCachedMetaKey:
                            hasLs = true;
                            break;
                        case CrossWorldLinkshellsCachedMetaKey:
                            hasCwls = true;
                            break;
                    }
                }
            }

            ChannelInfo? fc = null;
            var linkshells = new Dictionary<int, ChannelInfo>();
            var cwls = new Dictionary<int, ChannelInfo>();
            if (hasFc || hasLs || hasCwls)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT kind, slot, stable_key, display_name, has_real_name FROM cached_channels";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var kind = (ConversationKind)reader.GetInt32(0);
                    var slot = reader.GetInt32(1);
                    var stableKey = reader.GetString(2);
                    var displayName = reader.GetString(3);
                    var hasRealName = reader.GetBoolean(4);
                    var key = new ConversationKey(kind, stableKey);

                    switch (kind)
                    {
                        case ConversationKind.FreeCompany when hasFc:
                            fc = new ChannelInfo(key, displayName, Slot: null, HasRealName: hasRealName);
                            break;
                        case ConversationKind.Linkshell when hasLs && slot is >= 1 and <= 8:
                            linkshells[slot] = new ChannelInfo(key, displayName, slot, hasRealName);
                            break;
                        case ConversationKind.CrossWorldLinkshell when hasCwls && slot is >= 1 and <= 8:
                            cwls[slot] = new ChannelInfo(key, displayName, slot, hasRealName);
                            break;
                    }
                }
            }

            return new CachedChannels(hasFc, fc, hasLs, linkshells, hasCwls, cwls);
        });

    public Task SyncCachedChannelsAsync(ulong owner, ConversationKind kind, IReadOnlyList<ChannelInfo> channels)
    {
        var metaKey = GetChannelCacheMetaKey(kind);
        return Enqueue(owner, connection =>
        {
            using var transaction = connection.BeginTransaction();

            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM cached_channels WHERE kind = $kind";
                clear.Parameters.AddWithValue("$kind", (int)kind);
                clear.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO cached_channels (kind, slot, stable_key, display_name, has_real_name)
                VALUES ($kind, $slot, $stableKey, $name, $hasRealName)
                """;
            var insertKind = insert.Parameters.Add("$kind", SqliteType.Integer);
            var insertSlot = insert.Parameters.Add("$slot", SqliteType.Integer);
            var insertKey = insert.Parameters.Add("$stableKey", SqliteType.Text);
            var insertName = insert.Parameters.Add("$name", SqliteType.Text);
            var insertHasRealName = insert.Parameters.Add("$hasRealName", SqliteType.Integer);

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

            var updatedConversations = false;
            foreach (var channel in channels)
            {
                insertKind.Value = (int)kind;
                insertSlot.Value = channel.Slot ?? 0;
                insertKey.Value = channel.Key.StableKey;
                insertName.Value = channel.Name;
                insertHasRealName.Value = channel.HasRealName ? 1 : 0;
                insert.ExecuteNonQuery();

                if (channel.HasRealName)
                {
                    convKind.Value = (int)kind;
                    convKey.Value = channel.Key.StableKey;
                    convName.Value = channel.Name;
                    if (updateConv.ExecuteNonQuery() != 0)
                        updatedConversations = true;
                }
            }

            using (var meta = connection.CreateCommand())
            {
                meta.Transaction = transaction;
                meta.CommandText = """
                    INSERT INTO store_meta (key, value) VALUES ($key, $value)
                    ON CONFLICT (key) DO UPDATE SET value = excluded.value
                    """;
                meta.Parameters.AddWithValue("$key", metaKey);
                meta.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
                meta.ExecuteNonQuery();
            }

            transaction.Commit();
            if (updatedConversations)
                Interlocked.Increment(ref revision);

            return true;
        });
    }

    public Task SyncStaticMemberIdentitiesAsync(ulong owner, IReadOnlyList<PlayerIdentity> members) =>
        Enqueue(owner, connection =>
        {
            if (members.Count == 0)
                return false;

            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE static_members
                SET display_name = $name, world_id = $world, world_name = $worldName
                WHERE content_id = $cid
                  AND (display_name <> $name OR world_id <> $world OR world_name <> $worldName)
                """;
            var cidParam = update.Parameters.Add("$cid", SqliteType.Integer);
            var nameParam = update.Parameters.Add("$name", SqliteType.Text);
            var worldParam = update.Parameters.Add("$world", SqliteType.Integer);
            var worldNameParam = update.Parameters.Add("$worldName", SqliteType.Text);

            var changed = false;
            foreach (var member in members)
            {
                if (member.ContentId == 0)
                    continue;

                cidParam.Value = unchecked((long)member.ContentId);
                nameParam.Value = member.Name;
                worldParam.Value = (long)member.WorldId;
                worldNameParam.Value = member.World;
                if (update.ExecuteNonQuery() != 0)
                    changed = true;
            }

            transaction.Commit();
            if (changed)
                Interlocked.Increment(ref staticsRevision);

            return changed;
        });

    public Task<IReadOnlyList<StaticGroup>> ReadStaticsAsync(ulong owner) =>
        Enqueue<IReadOnlyList<StaticGroup>>(owner, connection =>
        {
            var membersByStatic = new Dictionary<string, List<PlayerIdentity>>(StringComparer.Ordinal);
            using (var memberCmd = connection.CreateCommand())
            {
                memberCmd.CommandText = """
                    SELECT static_key, content_id, display_name, world_id, world_name
                    FROM static_members
                    ORDER BY display_name COLLATE NOCASE, content_id
                    """;
                using var reader = memberCmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    if (!membersByStatic.TryGetValue(key, out var list))
                    {
                        list = new List<PlayerIdentity>();
                        membersByStatic.Add(key, list);
                    }

                    list.Add(new PlayerIdentity(
                        ReadContentId(reader, 1),
                        reader.GetString(2),
                        (uint)reader.GetInt64(3),
                        reader.GetString(4)));
                }
            }

            var groups = new List<StaticGroup>();
            using (var groupCmd = connection.CreateCommand())
            {
                groupCmd.CommandText = "SELECT stable_key, display_name, created_at FROM static_groups ORDER BY created_at";
                using var reader = groupCmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var name = reader.GetString(1);
                    var createdAt = reader.GetInt64(2);
                    var members = membersByStatic.TryGetValue(key, out var list) ? (IReadOnlyList<PlayerIdentity>)list : [];
                    groups.Add(new StaticGroup(key, name, createdAt, members));
                }
            }

            return groups;
        });

    public Task<StaticGroup> CreateStaticAsync(ulong owner, StaticGroup group) =>
        Enqueue(owner, connection =>
        {
            using var transaction = connection.BeginTransaction();

            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT display_name, created_at FROM static_groups WHERE stable_key = $stableKey";
                check.Parameters.AddWithValue("$stableKey", group.StableKey);
                using var existing = check.ExecuteReader();
                if (existing.Read())
                {
                    var existingName = existing.GetString(0);
                    var existingCreated = existing.GetInt64(1);
                    transaction.Commit();
                    return group with { Name = existingName, CreatedAt = existingCreated };
                }
            }

            using (var insertGroup = connection.CreateCommand())
            {
                insertGroup.Transaction = transaction;
                insertGroup.CommandText = """
                    INSERT INTO static_groups (stable_key, display_name, created_at)
                    VALUES ($stableKey, $name, $createdAt)
                    """;
                insertGroup.Parameters.AddWithValue("$stableKey", group.StableKey);
                insertGroup.Parameters.AddWithValue("$name", group.Name);
                insertGroup.Parameters.AddWithValue("$createdAt", group.CreatedAt);
                insertGroup.ExecuteNonQuery();
            }

            using (var insertMember = connection.CreateCommand())
            {
                insertMember.Transaction = transaction;
                insertMember.CommandText = """
                    INSERT INTO static_members (static_key, content_id, display_name, world_id, world_name)
                    VALUES ($stableKey, $cid, $name, $world, $worldName)
                    """;
                var keyParam = insertMember.Parameters.Add("$stableKey", SqliteType.Text);
                var cidParam = insertMember.Parameters.Add("$cid", SqliteType.Integer);
                var nameParam = insertMember.Parameters.Add("$name", SqliteType.Text);
                var worldParam = insertMember.Parameters.Add("$world", SqliteType.Integer);
                var worldNameParam = insertMember.Parameters.Add("$worldName", SqliteType.Text);

                foreach (var member in group.Members)
                {
                    keyParam.Value = group.StableKey;
                    cidParam.Value = unchecked((long)member.ContentId);
                    nameParam.Value = member.Name;
                    worldParam.Value = (long)member.WorldId;
                    worldNameParam.Value = member.World;
                    insertMember.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            Interlocked.Increment(ref staticsRevision);
            Interlocked.Increment(ref revision);
            return group;
        });

    public Task RenameStaticAsync(ulong owner, string stableKey, string newName) =>
        Enqueue(owner, connection =>
        {
            var trimmed = newName.Trim();
            if (trimmed.Length == 0)
                return false;

            using var transaction = connection.BeginTransaction();
            var changed = false;

            using (var updateGroup = connection.CreateCommand())
            {
                updateGroup.Transaction = transaction;
                updateGroup.CommandText = """
                    UPDATE static_groups
                    SET display_name = $name
                    WHERE stable_key = $stableKey AND display_name <> $name
                    """;
                updateGroup.Parameters.AddWithValue("$stableKey", stableKey);
                updateGroup.Parameters.AddWithValue("$name", trimmed);
                if (updateGroup.ExecuteNonQuery() != 0)
                    changed = true;
            }

            using (var updateConv = connection.CreateCommand())
            {
                updateConv.Transaction = transaction;
                updateConv.CommandText = """
                    UPDATE conversations
                    SET display_name = $name
                    WHERE kind = $kind AND stable_key = $stableKey AND display_name <> $name
                    """;
                updateConv.Parameters.AddWithValue("$kind", (int)ConversationKind.Static);
                updateConv.Parameters.AddWithValue("$stableKey", stableKey);
                updateConv.Parameters.AddWithValue("$name", trimmed);
                if (updateConv.ExecuteNonQuery() != 0)
                    changed = true;
            }

            transaction.Commit();
            if (changed)
            {
                Interlocked.Increment(ref staticsRevision);
                Interlocked.Increment(ref revision);
            }

            return changed;
        });

    public Task<IReadOnlyDictionary<ConversationKey, string>> ReadNotesAsync(ulong owner) =>
        Enqueue<IReadOnlyDictionary<ConversationKey, string>>(owner, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind, stable_key, note FROM conversation_notes";
            using var reader = command.ExecuteReader();
            var notes = new Dictionary<ConversationKey, string>();
            while (reader.Read())
                notes[new ConversationKey((ConversationKind)reader.GetInt32(0), reader.GetString(1))] = reader.GetString(2);

            return notes;
        });

    public Task SaveNoteAsync(ulong owner, ConversationKey key, string note) => Enqueue(owner, connection =>
    {
        var trimmed = note.Trim();
        using var command = connection.CreateCommand();
        if (trimmed.Length == 0)
        {
            command.CommandText = "DELETE FROM conversation_notes WHERE kind = $kind AND stable_key = $stableKey";
            BindConversationKey(command, key);
        }
        else
        {
            command.CommandText = """
                INSERT INTO conversation_notes (kind, stable_key, note) VALUES ($kind, $stableKey, $note)
                ON CONFLICT (kind, stable_key) DO UPDATE SET note = excluded.note
                """;
            BindConversationKey(command, key);
            command.Parameters.AddWithValue("$note", trimmed);
        }

        command.ExecuteNonQuery();
        Interlocked.Increment(ref notesRevision);
        return true;
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

    public Task<IReadOnlyList<ChatMessage>> ReadUpdatesAsync(ulong owner, ConversationKey key, long afterId, bool includeRejections) =>
        Enqueue<IReadOnlyList<ChatMessage>>(owner, connection =>
        {
            using var command = connection.CreateCommand();
            var filter = includeRejections ? "AND (m.id > $after OR r.message_id IS NOT NULL)" : "AND m.id > $after";
            command.CommandText = SelectMessages + $"\n{filter}\nORDER BY m.sent_at, m.id";
            BindConversationKey(command, key);
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

    private static string GetChannelCacheMetaKey(ConversationKind kind) => kind switch
    {
        ConversationKind.FreeCompany => FreeCompanyCachedMetaKey,
        ConversationKind.Linkshell => LinkshellsCachedMetaKey,
        ConversationKind.CrossWorldLinkshell => CrossWorldLinkshellsCachedMetaKey,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported cached channel kind."),
    };

    private static void BindConversationKey(SqliteCommand command, ConversationKey key)
    {
        command.Parameters.AddWithValue("$kind", (int)key.Kind);
        command.Parameters.AddWithValue("$stableKey", key.StableKey);
    }

    private static void BindContentId(SqliteCommand command, string name, ulong contentId) =>
        command.Parameters.AddWithValue(name, unchecked((long)contentId));

    private static ulong ReadContentId(SqliteDataReader reader, int ordinal) =>
        unchecked((ulong)reader.GetInt64(ordinal));

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
