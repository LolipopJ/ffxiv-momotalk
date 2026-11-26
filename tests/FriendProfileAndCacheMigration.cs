#!/usr/bin/env dotnet
// dotnet run --file tests/FriendProfileAndCacheMigration.cs
#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.8
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
#:include ../src/FFXIVMomoTalk/Chat/ChatModels.cs
#:include ../src/FFXIVMomoTalk/Chat/CharacterSqliteStore.cs
#:include ../src/FFXIVMomoTalk/Migrations/Cache/V1ToV2.cs
#:include ../src/FFXIVMomoTalk/Chat/CacheStore.cs
#:include ../src/FFXIVMomoTalk/Chat/FriendProfileStore.cs

using System.Globalization;
using FFXIVMomoTalk.Chat;
using Microsoft.Data.Sqlite;

var directory = Directory.CreateTempSubdirectory("momotalk-friend-profile-");
try
{
    await VerifyFreshCacheStoreAsync(directory.FullName);
    await VerifyV1ToV2CacheMigrationAsync(directory.FullName);
    await VerifyUnknownVersionPreservedAsync(directory.FullName);
    await VerifyFailedMigrationRollbackAndRetryAsync(directory.FullName);
    await VerifyFriendProfileStoreAndParsingAsync(directory.FullName);

    Console.WriteLine("CacheStore v1->v2 migration, rollback/retry, version guard, and FriendProfileStore checks passed.");
}
finally
{
    directory.Delete(recursive: true);
}

static async Task VerifyFreshCacheStoreAsync(string root)
{
    const ulong owner = 0x1001UL;
    var storeRoot = Path.Combine(root, "fresh");
    await using var cache = new CacheStore(storeRoot);
    var friend1 = new CachedFriend(new PlayerIdentity(101, "Alpha", 21, "Phoenix"), 1_700_000_000L);
    var friend2 = new CachedFriend(new PlayerIdentity(102, "Beta", 22, "Shiva"), 0L);

    await cache.SyncFriendsAsync(owner, [friend1, friend2]);
    var read = await cache.ReadCachedFriendsAsync(owner);
    if (read is null || read.Count != 2)
        throw new InvalidOperationException("Fresh CacheStore did not return synced friends.");

    var byId = read.ToDictionary(item => item.Player.ContentId);
    if (byId[101].AddedAt != 1_700_000_000L || byId[102].AddedAt != 0L || byId[101].Player.Name != "Alpha")
        throw new InvalidOperationException("Fresh CacheStore did not preserve friend AddedAt values.");
}

static async Task VerifyV1ToV2CacheMigrationAsync(string root)
{
    const ulong owner = 0x2002UL;
    var storeRoot = Path.Combine(root, "migrate");
    var charDir = Path.Combine(storeRoot, $"{owner:X16}");
    Directory.CreateDirectory(charDir);
    var dbPath = Path.Combine(charDir, "cache.sqlite3");

    using (var seed = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        seed.Open();
        using var cmd = seed.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE cached_friends (
                content_id INTEGER PRIMARY KEY,
                display_name TEXT NOT NULL,
                world_id INTEGER NOT NULL,
                world_name TEXT NOT NULL
            );
            CREATE TABLE cached_channels (
                kind INTEGER NOT NULL,
                slot INTEGER NOT NULL,
                stable_key TEXT NOT NULL,
                display_name TEXT NOT NULL,
                has_real_name INTEGER NOT NULL,
                PRIMARY KEY (kind, slot)
            );
            CREATE TABLE hidden_conversations (
                kind INTEGER NOT NULL,
                stable_key TEXT NOT NULL,
                PRIMARY KEY (kind, stable_key)
            );
            CREATE TABLE store_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO cached_friends (content_id, display_name, world_id, world_name)
            VALUES (5001, 'Legacy Friend', 33, 'Twintania');
            INSERT INTO cached_channels (kind, slot, stable_key, display_name, has_real_name)
            VALUES (1, 0, 'FC01', 'Legacy FC', 1);
            INSERT INTO hidden_conversations (kind, stable_key)
            VALUES (0, '0000000000001389');
            INSERT INTO store_meta (key, value) VALUES ('friends_cached_at', '1700000000000');
            INSERT INTO store_meta (key, value) VALUES ('fc_cached_at', '1700000000000');
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
    }

    await using var cache = new CacheStore(storeRoot);
    var friends = await cache.ReadCachedFriendsAsync(owner);
    if (friends is not [{ Player: { ContentId: 5001, Name: "Legacy Friend", WorldId: 33, World: "Twintania" }, AddedAt: 0L }])
        throw new InvalidOperationException("V1 cache friends were not preserved after v1->v2 migration.");

    var channels = await cache.ReadCachedChannelsAsync(owner);
    if (!channels.HasFreeCompany || channels.FreeCompany?.Name != "Legacy FC")
        throw new InvalidOperationException("V1 cached channels were not preserved after migration.");

    var hidden = await cache.ReadHiddenConversationsAsync(owner);
    if (!hidden.Contains(ConversationKey.Tell(5001)))
        throw new InvalidOperationException("V1 hidden conversations were not preserved after migration.");

    using (var check = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        check.Open();
        using var ver = check.CreateCommand();
        ver.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(ver.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version != 2)
            throw new InvalidOperationException($"Expected migrated cache.sqlite3 user_version 2, got {version}.");
    }

    await cache.SyncFriendsAsync(owner, [new CachedFriend(new PlayerIdentity(5001, "Legacy Friend", 33, "Twintania"), 1_710_000_000L)]);
    var updated = await cache.ReadCachedFriendsAsync(owner);
    if (updated is not [{ AddedAt: 1_710_000_000L }])
        throw new InvalidOperationException("Migrated CacheStore failed to persist updated AddedAt.");
}

static async Task VerifyUnknownVersionPreservedAsync(string root)
{
    const ulong owner = 0x3003UL;
    var storeRoot = Path.Combine(root, "unknown");
    var charDir = Path.Combine(storeRoot, $"{owner:X16}");
    Directory.CreateDirectory(charDir);
    var dbPath = Path.Combine(charDir, "cache.sqlite3");

    using (var seed = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        seed.Open();
        using var cmd = seed.CreateCommand();
        cmd.CommandText = "CREATE TABLE sentinel (id INTEGER PRIMARY KEY); INSERT INTO sentinel (id) VALUES (42); PRAGMA user_version = 99;";
        cmd.ExecuteNonQuery();
    }

    await using var cache = new CacheStore(storeRoot);
    try
    {
        await cache.ReadCachedFriendsAsync(owner);
        throw new InvalidOperationException("Expected InvalidDataException for unrecognized user_version.");
    }
    catch (InvalidDataException)
    {
        if (cache.IncompatibleOwner != owner)
            throw new InvalidOperationException("IncompatibleOwner was not set for unrecognized version.");
    }

    using var verify = new SqliteConnection($"Data Source={dbPath};Pooling=False");
    verify.Open();
    using var ver = verify.CreateCommand();
    ver.CommandText = "PRAGMA user_version;";
    if (Convert.ToInt32(ver.ExecuteScalar(), CultureInfo.InvariantCulture) != 99)
        throw new InvalidOperationException("Unrecognized database version was modified.");
}

static async Task VerifyFailedMigrationRollbackAndRetryAsync(string root)
{
    const ulong owner = 0x4004UL;
    var storeRoot = Path.Combine(root, "retry");
    var charDir = Path.Combine(storeRoot, $"{owner:X16}");
    Directory.CreateDirectory(charDir);
    var dbPath = Path.Combine(charDir, "retry.sqlite3");

    using (var seed = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        seed.Open();
        using var cmd = seed.CreateCommand();
        cmd.CommandText = "CREATE TABLE base_data (val TEXT NOT NULL); INSERT INTO base_data (val) VALUES ('keep'); PRAGMA user_version = 1;";
        cmd.ExecuteNonQuery();
    }

    await using var store = FlakyMigrationStore.Create(storeRoot);
    try
    {
        await store.PingAsync(owner);
        throw new InvalidOperationException("Expected first migration attempt to fail.");
    }
    catch (InvalidOperationException)
    {
        if (store.IncompatibleOwner != 0)
            throw new InvalidOperationException("Failed migration should allow retry without marking IncompatibleOwner.");
    }

    using (var checkMid = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        checkMid.Open();
        using var ver = checkMid.CreateCommand();
        ver.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(ver.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("Rolled-back migration did not preserve user_version 1.");

        using var tbl = checkMid.CreateCommand();
        tbl.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'extra_v2';";
        if (Convert.ToInt32(tbl.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Rolled-back migration left partial DDL changes behind.");
    }

    var kept = await store.PingAsync(owner);
    if (kept != "keep")
        throw new InvalidOperationException("Retry after migration failure did not preserve existing data.");
}

static async Task VerifyFriendProfileStoreAndParsingAsync(string root)
{
    if (FriendProfileStore.NormalizeTag("  Raid\tStatic   Team  ") != "Raid Static Team")
        throw new InvalidOperationException("Tag whitespace normalization failed.");

    if (FriendProfileStore.NormalizeTag(new string('A', 40)).Length != FriendProfileStore.MaxTagLength)
        throw new InvalidOperationException("Tag length clamping failed.");

    if (!FriendProfileStore.TryParseHexColor("#4a8ac6", allowShortForm: false, out var hex6) || hex6 != 0x4A8AC6
        || !FriendProfileStore.TryParseHexColor("f90", allowShortForm: true, out var hex3) || hex3 != 0xFF9900
        || FriendProfileStore.TryParseHexColor("f90", allowShortForm: false, out _)
        || FriendProfileStore.TryParseHexColor("#GGGGGG", allowShortForm: true, out _))
    {
        throw new InvalidOperationException("Hex color parsing checks failed.");
    }

    const ulong owner = 0x5005UL;
    const ulong friendId = 9001UL;
    var storeRoot = Path.Combine(root, "profiles");
    await using var store = new FriendProfileStore(storeRoot);

    var added1 = await store.AddTagAsync(owner, friendId, "  Static  Member ");
    var added2 = await store.AddTagAsync(owner, friendId, "static member");
    var added3 = await store.AddTagAsync(owner, friendId, "Mentor");
    if (added1.Count != 1 || added2.Count != 1 || added3 is not ["Static Member", "Mentor"])
        throw new InvalidOperationException("Tag deduplication or ordering failed.");

    await store.SaveColorAsync(owner, friendId, 0xD9536F);
    var snapshot1 = await store.ReadAllAsync(owner);
    if (snapshot1.NameColors[friendId] != 0xD9536F
        || snapshot1.Tags[friendId] is not ["Static Member", "Mentor"])
    {
        throw new InvalidOperationException("FriendProfileStore ReadAllAsync did not return saved tags and color.");
    }

    var afterRemove = await store.RemoveTagAsync(owner, friendId, "STATIC MEMBER");
    if (afterRemove is not ["Mentor"])
        throw new InvalidOperationException("Case-insensitive tag removal failed.");

    await store.SaveColorAsync(owner, friendId, null);
    var snapshot2 = await store.ReadAllAsync(owner);
    if (snapshot2.NameColors.ContainsKey(friendId))
        throw new InvalidOperationException("Clearing friend name color failed.");
}

internal sealed class FlakyMigrationStore : CharacterSqliteStore
{
    private FlakyMigrationStore(string charactersDirectory, IReadOnlyList<SqliteMigration> migrations)
        : base(charactersDirectory, "retry.sqlite3", "retry test", 2, "CREATE TABLE base_data (val TEXT NOT NULL);", migrations: migrations)
    {
    }

    public static FlakyMigrationStore Create(string charactersDirectory)
    {
        var holder = new AttemptHolder();
        return new FlakyMigrationStore(
            charactersDirectory,
            [
                new SqliteMigration(1, 2, "create extra_v2", (conn, tx) =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "CREATE TABLE extra_v2 (id INTEGER PRIMARY KEY);";
                    cmd.ExecuteNonQuery();
                    if (Interlocked.Increment(ref holder.Attempts) == 1)
                        throw new IOException("Simulated failure mid-migration.");
                }),
            ]);
    }

    public Task<string> PingAsync(ulong owner) =>
        Enqueue(owner, connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT val FROM base_data LIMIT 1;";
            return (string)cmd.ExecuteScalar()!;
        });

    private sealed class AttemptHolder
    {
        public int Attempts;
    }
}
