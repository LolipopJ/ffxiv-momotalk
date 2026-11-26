using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace FFXIVMomoTalk.Chat;

internal readonly record struct SqliteMigration(
    int FromVersion,
    int ToVersion,
    string Step,
    Action<SqliteConnection, SqliteTransaction> Apply);

internal abstract class CharacterSqliteStore : IAsyncDisposable
{
    private readonly string charactersDirectory;
    private readonly string fileName;
    private readonly string storeName;
    private readonly int schemaVersion;
    private readonly string schemaSql;
    private readonly string? indexSql;
    private readonly IReadOnlyList<SqliteMigration> migrations;
    private readonly Channel<Action> requests = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task worker;
    private SqliteConnection? database;
    private ulong openedCharacter;
    private ulong failedOwner;
    private ulong incompatibleOwner;

    protected CharacterSqliteStore(
        string charactersDirectory,
        string fileName,
        string storeName,
        int schemaVersion,
        string schemaSql,
        string? indexSql = null,
        IReadOnlyList<SqliteMigration>? migrations = null)
    {
        this.charactersDirectory = charactersDirectory;
        this.fileName = fileName;
        this.storeName = storeName;
        this.schemaVersion = schemaVersion;
        this.schemaSql = schemaSql;
        this.indexSql = indexSql;
        this.migrations = migrations ?? [];
        worker = Task.Run(ProcessRequestsAsync);
    }

    public string FileName => fileName;

    public ulong FailedOwner => Volatile.Read(ref failedOwner);

    public ulong IncompatibleOwner => Volatile.Read(ref incompatibleOwner);

    public async ValueTask DisposeAsync()
    {
        requests.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }

    protected Task<T> Enqueue<T>(ulong owner, Func<SqliteConnection, T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!requests.Writer.TryWrite(() =>
        {
            try
            {
                var result = operation(OpenDatabase(owner));
                if (Volatile.Read(ref failedOwner) == owner)
                    Volatile.Write(ref failedOwner, 0);
                completion.SetResult(result);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref failedOwner, owner);
                completion.SetException(exception);
            }
        }))
        {
            completion.SetException(new ObjectDisposedException(GetType().Name));
        }

        return completion.Task;
    }

    protected static void BindConversationKey(SqliteCommand command, ConversationKey key)
    {
        command.Parameters.AddWithValue("$kind", (int)key.Kind);
        command.Parameters.AddWithValue("$stableKey", key.StableKey);
    }

    protected static void BindContentId(SqliteCommand command, string name, ulong contentId) =>
        command.Parameters.AddWithValue(name, unchecked((long)contentId));

    protected static ulong ReadContentId(SqliteDataReader reader, int ordinal) =>
        unchecked((ulong)reader.GetInt64(ordinal));

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
        var characterDir = Path.Combine(charactersDirectory, $"{owner:X16}");
        Directory.CreateDirectory(characterDir);
        var path = Path.Combine(characterDir, fileName);
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
            using (var fk = next.CreateCommand())
            {
                fk.CommandText = "PRAGMA foreign_keys = ON;";
                fk.ExecuteNonQuery();
            }

            if (existed)
            {
                using var versionCmd = next.CreateCommand();
                versionCmd.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(versionCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (version < 1 || version > schemaVersion)
                {
                    Volatile.Write(ref incompatibleOwner, owner);
                    throw new InvalidDataException(
                        $"Unrecognized {storeName} database schema version {version} at '{path}' (expected {schemaVersion}). Existing file was preserved.");
                }

                while (version < schemaVersion)
                {
                    var step = FindMigration(version);
                    if (step is not { } migration)
                    {
                        Volatile.Write(ref incompatibleOwner, owner);
                        throw new InvalidDataException(
                            $"Unrecognized {storeName} database schema version {version} at '{path}' (expected {schemaVersion}). Existing file was preserved.");
                    }

                    try
                    {
                        using var transaction = next.BeginTransaction();
                        migration.Apply(next, transaction);
                        using var updateVersion = next.CreateCommand();
                        updateVersion.Transaction = transaction;
                        updateVersion.CommandText = $"PRAGMA user_version = {migration.ToVersion};";
                        updateVersion.ExecuteNonQuery();
                        transaction.Commit();
                        version = migration.ToVersion;
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            $"Failed to migrate {storeName} database at '{path}' from v{migration.FromVersion} to v{migration.ToVersion} during '{migration.Step}'. Existing file was preserved at v{version}.",
                            exception);
                    }
                }

                if (indexSql is not null)
                {
                    using var indexes = next.CreateCommand();
                    indexes.CommandText = indexSql;
                    indexes.ExecuteNonQuery();
                }
            }
            else
            {
                using var transaction = next.BeginTransaction();
                using var command = next.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"{schemaSql}\n{indexSql}\nPRAGMA user_version = {schemaVersion};";
                command.ExecuteNonQuery();
                transaction.Commit();
            }

            Volatile.Write(ref incompatibleOwner, 0);
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

    private SqliteMigration? FindMigration(int version)
    {
        foreach (var step in migrations)
        {
            if (step.FromVersion == version && step.ToVersion > version && step.ToVersion <= schemaVersion)
                return step;
        }

        return null;
    }
}
