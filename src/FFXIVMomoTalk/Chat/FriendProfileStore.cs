using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal sealed class FriendProfileStore(string charactersDirectory)
    : CharacterSqliteStore(charactersDirectory, "friend_profiles.sqlite3", "friend profiles", SchemaVersion, Schema, Indexes)
{
    public const int MaxTagLength = 32;
    private const int SchemaVersion = 1;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS friend_tags (
            content_id INTEGER NOT NULL,
            tag TEXT NOT NULL,
            sort_order INTEGER NOT NULL,
            PRIMARY KEY (content_id, tag)
        );

        CREATE TABLE IF NOT EXISTS friend_colors (
            content_id INTEGER PRIMARY KEY,
            color_rgb INTEGER NOT NULL
        );
        """;

    private const string Indexes = """
        CREATE INDEX IF NOT EXISTS friend_tags_order ON friend_tags (content_id, sort_order, tag);
        """;

    private long revision;

    public long Revision => Interlocked.Read(ref revision);

    public static string NormalizeTag(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        Span<char> buffer = stackalloc char[MaxTagLength];
        var length = 0;
        var pendingSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (length > 0)
                    pendingSpace = true;

                continue;
            }

            if (pendingSpace)
            {
                if (length >= MaxTagLength)
                    break;

                buffer[length++] = ' ';
                pendingSpace = false;
            }

            if (length >= MaxTagLength)
                break;

            buffer[length++] = ch;
        }

        return length == 0 ? string.Empty : new string(buffer[..length]).TrimEnd();
    }

    public static bool TryParseHexColor(string input, bool allowShortForm, out uint rgb)
    {
        rgb = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var span = input.AsSpan().Trim();
        if (span.StartsWith('#'))
            span = span[1..];

        if (span.Length == 6)
            return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);

        if (allowShortForm && span.Length == 3
            && uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shortRgb))
        {
            var r = (shortRgb >> 8) & 0xF;
            var g = (shortRgb >> 4) & 0xF;
            var b = shortRgb & 0xF;
            rgb = (r << 20) | (r << 16) | (g << 12) | (g << 8) | (b << 4) | b;
            return true;
        }

        return false;
    }

    public static string FormatHexColor(uint rgb) => $"#{rgb & 0xFFFFFF:X6}";

    public Task<FriendCustomizations> ReadAllAsync(ulong owner) =>
        Enqueue(owner, connection =>
        {
            var tags = new Dictionary<ulong, IReadOnlyList<string>>();
            var builder = new Dictionary<ulong, List<string>>();
            using (var tagCmd = connection.CreateCommand())
            {
                tagCmd.CommandText = """
                    SELECT content_id, tag
                    FROM friend_tags
                    ORDER BY content_id, sort_order, tag
                    """;
                using var reader = tagCmd.ExecuteReader();
                while (reader.Read())
                {
                    var contentId = ReadContentId(reader, 0);
                    if (!builder.TryGetValue(contentId, out var list))
                    {
                        list = [];
                        builder.Add(contentId, list);
                    }

                    list.Add(reader.GetString(1));
                }
            }

            foreach (var (contentId, list) in builder)
                tags[contentId] = list;

            var colors = new Dictionary<ulong, uint>();
            using (var colorCmd = connection.CreateCommand())
            {
                colorCmd.CommandText = "SELECT content_id, color_rgb FROM friend_colors";
                using var reader = colorCmd.ExecuteReader();
                while (reader.Read())
                    colors[ReadContentId(reader, 0)] = (uint)(reader.GetInt64(1) & 0xFFFFFF);
            }

            return new FriendCustomizations(tags, colors);
        });

    public Task<IReadOnlyList<string>> AddTagAsync(ulong owner, ulong friendContentId, string rawTag)
    {
        var normalized = NormalizeTag(rawTag);
        return Enqueue<IReadOnlyList<string>>(owner, connection =>
        {
            using var transaction = connection.BeginTransaction();
            var existing = ReadFriendTags(connection, transaction, friendContentId, out var maxOrder);
            if (normalized.Length == 0 || existing.Any(tag => StringComparer.OrdinalIgnoreCase.Equals(tag, normalized)))
            {
                transaction.Commit();
                return existing;
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO friend_tags (content_id, tag, sort_order)
                VALUES ($cid, $tag, $sortOrder)
                """;
            BindContentId(insert, "$cid", friendContentId);
            insert.Parameters.AddWithValue("$tag", normalized);
            insert.Parameters.AddWithValue("$sortOrder", maxOrder + 1);
            insert.ExecuteNonQuery();
            transaction.Commit();
            Interlocked.Increment(ref revision);
            existing.Add(normalized);
            return existing;
        });
    }

    public Task<IReadOnlyList<string>> RemoveTagAsync(ulong owner, ulong friendContentId, string tag)
    {
        var target = tag.Trim();
        return Enqueue<IReadOnlyList<string>>(owner, connection =>
        {
            using var transaction = connection.BeginTransaction();
            var existing = ReadFriendTags(connection, transaction, friendContentId, out _);
            var matched = existing.Where(item => StringComparer.OrdinalIgnoreCase.Equals(item, target)).ToList();
            if (matched.Count == 0)
            {
                transaction.Commit();
                return existing;
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM friend_tags WHERE content_id = $cid AND tag = $tag";
            BindContentId(delete, "$cid", friendContentId);
            var tagParam = delete.Parameters.Add("$tag", SqliteType.Text);
            foreach (var item in matched)
            {
                tagParam.Value = item;
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
            Interlocked.Increment(ref revision);
            existing.RemoveAll(item => StringComparer.OrdinalIgnoreCase.Equals(item, target));
            return existing;
        });
    }

    public Task SaveColorAsync(ulong owner, ulong friendContentId, uint? colorRgb) => Enqueue(owner, connection =>
    {
        using var command = connection.CreateCommand();
        if (colorRgb is not { } rgb)
        {
            command.CommandText = "DELETE FROM friend_colors WHERE content_id = $cid";
            BindContentId(command, "$cid", friendContentId);
        }
        else
        {
            command.CommandText = """
                INSERT INTO friend_colors (content_id, color_rgb) VALUES ($cid, $rgb)
                ON CONFLICT (content_id) DO UPDATE SET color_rgb = excluded.color_rgb
                """;
            BindContentId(command, "$cid", friendContentId);
            command.Parameters.AddWithValue("$rgb", (long)(rgb & 0xFFFFFF));
        }

        if (command.ExecuteNonQuery() != 0)
            Interlocked.Increment(ref revision);

        return true;
    });

    private static List<string> ReadFriendTags(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong friendContentId,
        out int maxSortOrder)
    {
        var tags = new List<string>();
        maxSortOrder = 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT tag, sort_order
            FROM friend_tags
            WHERE content_id = $cid
            ORDER BY sort_order, tag
            """;
        BindContentId(command, "$cid", friendContentId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
            maxSortOrder = Math.Max(maxSortOrder, reader.GetInt32(1));
        }

        return tags;
    }
}
