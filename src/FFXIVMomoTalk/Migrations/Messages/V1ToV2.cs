using FFXIVMomoTalk.Chat;
using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Migrations.Messages;

internal static class V1ToV2
{
    public static SqliteMigration Migration { get; } = new(1, 2, "add message kind column", Apply, BackupBeforeMigrate: true);

    private static void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = "ALTER TABLE messages ADD COLUMN kind INTEGER NOT NULL DEFAULT 0;";
        alter.ExecuteNonQuery();
    }
}
