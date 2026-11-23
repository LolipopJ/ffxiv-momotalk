using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal sealed class StaticStore(string charactersDirectory)
    : CharacterSqliteStore(charactersDirectory, "statics.sqlite3", "static groups", SchemaVersion, Schema)
{
    private const int SchemaVersion = 1;

    private const string Schema = """
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
        """;

    private long revision;

    public long Revision => Interlocked.Read(ref revision);

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
            Interlocked.Increment(ref revision);
            return group;
        });

    public Task RenameStaticAsync(ulong owner, string stableKey, string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0)
            return Task.CompletedTask;

        return Enqueue(owner, connection =>
        {
            using var updateGroup = connection.CreateCommand();
            updateGroup.CommandText = """
                UPDATE static_groups
                SET display_name = $name
                WHERE stable_key = $stableKey AND display_name <> $name
                """;
            updateGroup.Parameters.AddWithValue("$stableKey", stableKey);
            updateGroup.Parameters.AddWithValue("$name", trimmed);
            var changed = updateGroup.ExecuteNonQuery() != 0;
            if (changed)
                Interlocked.Increment(ref revision);

            return changed;
        });
    }

    public Task SyncMemberIdentitiesAsync(ulong owner, IReadOnlyList<PlayerIdentity> members)
    {
        if (members.Count == 0)
            return Task.CompletedTask;

        return Enqueue(owner, connection =>
        {
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
                Interlocked.Increment(ref revision);

            return changed;
        });
    }
}
