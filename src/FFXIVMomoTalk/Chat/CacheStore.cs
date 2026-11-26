using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal sealed class CacheStore(string charactersDirectory)
    : CharacterSqliteStore(charactersDirectory, "cache.sqlite3", "cache", SchemaVersion, Schema, migrations: [Migrations.Cache.V1ToV2.Migration])
{
    private const int SchemaVersion = 2;
    private const string FriendsCachedMetaKey = "friends_cached_at";
    private const string FreeCompanyCachedMetaKey = "fc_cached_at";
    private const string LinkshellsCachedMetaKey = "ls_cached_at";
    private const string CrossWorldLinkshellsCachedMetaKey = "cwls_cached_at";

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS cached_friends (
            content_id INTEGER PRIMARY KEY,
            display_name TEXT NOT NULL,
            world_id INTEGER NOT NULL,
            world_name TEXT NOT NULL,
            added_at INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS cached_channels (
            kind INTEGER NOT NULL,
            slot INTEGER NOT NULL,
            stable_key TEXT NOT NULL,
            display_name TEXT NOT NULL,
            has_real_name INTEGER NOT NULL,
            PRIMARY KEY (kind, slot)
        );

        CREATE TABLE IF NOT EXISTS hidden_conversations (
            kind INTEGER NOT NULL,
            stable_key TEXT NOT NULL,
            PRIMARY KEY (kind, stable_key)
        );

        CREATE TABLE IF NOT EXISTS store_meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    private long hiddenRevision;

    public long HiddenRevision => Interlocked.Read(ref hiddenRevision);

    public Task<IReadOnlyList<CachedFriend>?> ReadCachedFriendsAsync(ulong owner) =>
        Enqueue<IReadOnlyList<CachedFriend>?>(owner, connection =>
        {
            using var meta = connection.CreateCommand();
            meta.CommandText = "SELECT 1 FROM store_meta WHERE key = $key";
            meta.Parameters.AddWithValue("$key", FriendsCachedMetaKey);
            if (meta.ExecuteScalar() is null)
                return null;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content_id, display_name, world_id, world_name, added_at FROM cached_friends";
            using var reader = command.ExecuteReader();
            var friends = new List<CachedFriend>();
            while (reader.Read())
            {
                var player = new PlayerIdentity(
                    ReadContentId(reader, 0),
                    reader.GetString(1),
                    (uint)reader.GetInt64(2),
                    reader.GetString(3));
                friends.Add(new CachedFriend(player, reader.GetInt64(4)));
            }

            return friends;
        });

    public Task SyncFriendsAsync(ulong owner, IReadOnlyList<CachedFriend> friends) => Enqueue(owner, connection =>
    {
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM cached_friends";
            clear.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO cached_friends (content_id, display_name, world_id, world_name, added_at)
            VALUES ($cid, $name, $world, $worldName, $addedAt)
            """;
        var insertCid = insert.Parameters.Add("$cid", SqliteType.Integer);
        var insertName = insert.Parameters.Add("$name", SqliteType.Text);
        var insertWorld = insert.Parameters.Add("$world", SqliteType.Integer);
        var insertWorldName = insert.Parameters.Add("$worldName", SqliteType.Text);
        var insertAddedAt = insert.Parameters.Add("$addedAt", SqliteType.Integer);

        foreach (var entry in friends)
        {
            insertCid.Value = unchecked((long)entry.Player.ContentId);
            insertName.Value = entry.Player.Name;
            insertWorld.Value = (long)entry.Player.WorldId;
            insertWorldName.Value = entry.Player.World;
            insertAddedAt.Value = entry.AddedAt;
            insert.ExecuteNonQuery();
        }

        UpsertMeta(connection, transaction, FriendsCachedMetaKey);
        transaction.Commit();
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

            foreach (var channel in channels)
            {
                insertKind.Value = (int)kind;
                insertSlot.Value = channel.Slot ?? 0;
                insertKey.Value = channel.Key.StableKey;
                insertName.Value = channel.Name;
                insertHasRealName.Value = channel.HasRealName ? 1 : 0;
                insert.ExecuteNonQuery();
            }

            UpsertMeta(connection, transaction, metaKey);
            transaction.Commit();
            return true;
        });
    }

    public Task<HashSet<ConversationKey>> ReadHiddenConversationsAsync(ulong owner) =>
        Enqueue(owner, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind, stable_key FROM hidden_conversations";
            using var reader = command.ExecuteReader();
            var hidden = new HashSet<ConversationKey>();
            while (reader.Read())
                hidden.Add(new ConversationKey((ConversationKind)reader.GetInt32(0), reader.GetString(1)));

            return hidden;
        });

    public Task HideConversationAsync(ulong owner, ConversationKey key) => Enqueue(owner, connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO hidden_conversations (kind, stable_key)
            VALUES ($kind, $stableKey)
            """;
        BindConversationKey(command, key);
        if (command.ExecuteNonQuery() != 0)
            Interlocked.Increment(ref hiddenRevision);

        return true;
    });

    public Task UnhideConversationAsync(ulong owner, ConversationKey key) => Enqueue(owner, connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM hidden_conversations WHERE kind = $kind AND stable_key = $stableKey";
        BindConversationKey(command, key);
        if (command.ExecuteNonQuery() != 0)
            Interlocked.Increment(ref hiddenRevision);

        return true;
    });

    private static void UpsertMeta(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var meta = connection.CreateCommand();
        meta.Transaction = transaction;
        meta.CommandText = """
            INSERT INTO store_meta (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """;
        meta.Parameters.AddWithValue("$key", key);
        meta.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        meta.ExecuteNonQuery();
    }

    private static string GetChannelCacheMetaKey(ConversationKind kind) => kind switch
    {
        ConversationKind.FreeCompany => FreeCompanyCachedMetaKey,
        ConversationKind.Linkshell => LinkshellsCachedMetaKey,
        ConversationKind.CrossWorldLinkshell => CrossWorldLinkshellsCachedMetaKey,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported cached channel kind."),
    };
}
