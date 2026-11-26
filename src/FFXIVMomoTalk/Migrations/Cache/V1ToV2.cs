using FFXIVMomoTalk.Chat;
using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Migrations.Cache;

internal static class V1ToV2
{
    public static SqliteMigration Migration { get; } = new(1, 2, "add cached friend added_at column", Apply);

    private static void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = "ALTER TABLE cached_friends ADD COLUMN added_at INTEGER NOT NULL DEFAULT 0;";
        alter.ExecuteNonQuery();
    }
}
