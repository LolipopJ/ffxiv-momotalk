using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal sealed partial class MessageStore
{
    public Task UpdateFriendDescriptorsAsync(
        ulong owner,
        IReadOnlyList<PlayerIdentity> friends,
        Func<string, string, string, string, string>? formatChangeNotice = null,
        long? recordedAt = null)
    {
        if (friends.Count == 0)
            return Task.CompletedTask;

        return Enqueue(owner, connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var selectConv = connection.CreateCommand();
            selectConv.Transaction = transaction;
            selectConv.CommandText = """
                SELECT id, display_name, world_id, world_name
                FROM conversations
                WHERE kind = 0 AND stable_key = $stableKey
                """;
            var selectKey = selectConv.Parameters.Add("$stableKey", SqliteType.Text);

            using var updateConv = connection.CreateCommand();
            updateConv.Transaction = transaction;
            updateConv.CommandText = """
                UPDATE conversations
                SET display_name = $name, world_id = $world, world_name = $worldName
                WHERE id = $id
                """;
            var updateId = updateConv.Parameters.Add("$id", SqliteType.Integer);
            var updateName = updateConv.Parameters.Add("$name", SqliteType.Text);
            var updateWorld = updateConv.Parameters.Add("$world", SqliteType.Integer);
            var updateWorldName = updateConv.Parameters.Add("$worldName", SqliteType.Text);

            var timestamp = recordedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var changed = false;
            foreach (var friend in friends)
            {
                selectKey.Value = ConversationKey.Tell(friend.ContentId).StableKey;
                long convId;
                string oldName;
                uint oldWorldId;
                string oldWorldName;
                using (var reader = selectConv.ExecuteReader())
                {
                    if (!reader.Read())
                        continue;

                    convId = reader.GetInt64(0);
                    oldName = reader.GetString(1);
                    oldWorldId = (uint)reader.GetInt64(2);
                    oldWorldName = reader.GetString(3);
                }

                if (StringComparer.Ordinal.Equals(oldName, friend.Name)
                    && oldWorldId == friend.WorldId
                    && StringComparer.Ordinal.Equals(oldWorldName, friend.World))
                {
                    continue;
                }

                updateId.Value = convId;
                updateName.Value = friend.Name;
                updateWorld.Value = (long)friend.WorldId;
                updateWorldName.Value = friend.World;
                updateConv.ExecuteNonQuery();
                changed = true;

                if (formatChangeNotice is null)
                    continue;

                var nameChanged = !string.IsNullOrWhiteSpace(oldName)
                    && !StringComparer.Ordinal.Equals(oldName, friend.Name);
                var worldChanged = !string.IsNullOrWhiteSpace(oldWorldName)
                    && !string.IsNullOrWhiteSpace(friend.World)
                    && (oldWorldId != 0 && friend.WorldId != 0
                        ? oldWorldId != friend.WorldId
                        : !StringComparer.OrdinalIgnoreCase.Equals(oldWorldName, friend.World));
                if (!nameChanged && !worldChanged)
                    continue;

                var notice = formatChangeNotice(oldName, oldWorldName, friend.Name, friend.World);
                if (!string.IsNullOrWhiteSpace(notice))
                    InsertSilentSystemRow(connection, transaction, convId, timestamp, outgoing: false, friend, notice);
            }

            transaction.Commit();
            if (changed)
                Interlocked.Increment(ref revision);

            return changed;
        });
    }

    public Task UpdateChannelNamesAsync(
        ulong owner,
        ConversationKind kind,
        IReadOnlyList<ChannelInfo> channels,
        Func<string, string, string>? formatRenameNotice = null,
        IReadOnlyDictionary<ConversationKey, string>? previousNames = null,
        long? recordedAt = null)
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
            using var selectConv = connection.CreateCommand();
            selectConv.Transaction = transaction;
            selectConv.CommandText = """
                SELECT id, display_name
                FROM conversations
                WHERE kind = $kind AND stable_key = $stableKey
                """;
            var selectKind = selectConv.Parameters.Add("$kind", SqliteType.Integer);
            var selectKey = selectConv.Parameters.Add("$stableKey", SqliteType.Text);

            using var updateConv = connection.CreateCommand();
            updateConv.Transaction = transaction;
            updateConv.CommandText = "UPDATE conversations SET display_name = $name WHERE id = $id";
            var updateId = updateConv.Parameters.Add("$id", SqliteType.Integer);
            var updateName = updateConv.Parameters.Add("$name", SqliteType.Text);

            var timestamp = recordedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var changed = false;
            foreach (var channel in channels)
            {
                if (!channel.HasRealName)
                    continue;

                selectKind.Value = (int)kind;
                selectKey.Value = channel.Key.StableKey;
                long? existingId = null;
                string? existingName = null;
                using (var reader = selectConv.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        existingId = reader.GetInt64(0);
                        existingName = reader.GetString(1);
                    }
                }

                if (existingId is { } convId)
                {
                    if (StringComparer.Ordinal.Equals(existingName, channel.Name))
                        continue;

                    updateId.Value = convId;
                    updateName.Value = channel.Name;
                    updateConv.ExecuteNonQuery();
                    changed = true;

                    if (formatRenameNotice is not null
                        && !string.IsNullOrWhiteSpace(existingName)
                        && !IsPlaceholderChannelName(kind, existingName))
                    {
                        var notice = formatRenameNotice(existingName, channel.Name);
                        if (!string.IsNullOrWhiteSpace(notice))
                            InsertSilentSystemRow(connection, transaction, convId, timestamp, outgoing: false, SystemSender, notice);
                    }
                }
                else if (formatRenameNotice is not null
                    && previousNames is not null
                    && previousNames.TryGetValue(channel.Key, out var previousName)
                    && !string.IsNullOrWhiteSpace(previousName)
                    && !IsPlaceholderChannelName(kind, previousName)
                    && !StringComparer.Ordinal.Equals(previousName, channel.Name))
                {
                    var notice = formatRenameNotice(previousName, channel.Name);
                    if (string.IsNullOrWhiteSpace(notice))
                        continue;

                    var insertedId = InsertConversationShell(connection, transaction, channel.ToDescriptor());
                    InsertSilentSystemRow(connection, transaction, insertedId, timestamp, outgoing: false, SystemSender, notice);
                    changed = true;
                }
            }

            transaction.Commit();
            if (changed)
                Interlocked.Increment(ref revision);

            return changed;
        });
    }

    public Task RenameConversationAsync(
        ulong owner,
        ConversationKey key,
        string newName,
        string? systemNotice = null,
        PlayerIdentity? noticeSender = null,
        long? recordedAt = null)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0)
            return Task.CompletedTask;

        return Enqueue(owner, connection =>
        {
            if (string.IsNullOrWhiteSpace(systemNotice))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE conversations
                    SET display_name = $name
                    WHERE kind = $kind AND stable_key = $stableKey AND display_name <> $name
                    """;
                BindConversationKey(command, key);
                command.Parameters.AddWithValue("$name", trimmed);
                var updated = command.ExecuteNonQuery() != 0;
                if (updated)
                    Interlocked.Increment(ref revision);

                return updated;
            }

            using var transaction = connection.BeginTransaction();
            var descriptor = new ConversationDescriptor(key, trimmed);
            long convId;
            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO conversations (kind, stable_key, display_name, world_id, world_name)
                    VALUES ($kind, $stableKey, $name, 0, '')
                    ON CONFLICT (kind, stable_key) DO UPDATE SET display_name = excluded.display_name
                    RETURNING id
                    """;
                BindConversationKey(upsert, descriptor.Key);
                upsert.Parameters.AddWithValue("$name", descriptor.DisplayName);
                convId = (long)upsert.ExecuteScalar()!;
            }

            var timestamp = recordedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            InsertSilentSystemRow(connection, transaction, convId, timestamp,
                outgoing: noticeSender is not null, noticeSender ?? SystemSender, systemNotice);
            transaction.Commit();
            Interlocked.Increment(ref revision);
            return true;
        });
    }

    internal static bool IsPlaceholderChannelName(ConversationKind kind, string name)
    {
        var trimmed = name.Trim();
        return kind switch
        {
            ConversationKind.FreeCompany => StringComparer.Ordinal.Equals(trimmed, "Free Company"),
            ConversationKind.Linkshell => trimmed.Length == 11
                && trimmed.StartsWith("Linkshell ", StringComparison.Ordinal)
                && trimmed[10] is >= '1' and <= '8',
            ConversationKind.CrossWorldLinkshell => trimmed.Length == 6
                && trimmed.StartsWith("CWLS ", StringComparison.Ordinal)
                && trimmed[5] is >= '1' and <= '8',
            _ => false,
        };
    }

    private static long InsertConversationShell(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationDescriptor descriptor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversations (kind, stable_key, display_name, world_id, world_name)
            VALUES ($kind, $stableKey, $name, $world, $worldName)
            RETURNING id
            """;
        BindConversationKey(command, descriptor.Key);
        command.Parameters.AddWithValue("$name", descriptor.DisplayName);
        command.Parameters.AddWithValue("$world", (long)descriptor.WorldId);
        command.Parameters.AddWithValue("$worldName", descriptor.WorldName);
        return (long)command.ExecuteScalar()!;
    }
}
