namespace FFXIVMomoTalk.Chat;

internal sealed class NotesStore(string charactersDirectory)
    : CharacterSqliteStore(charactersDirectory, "notes.sqlite3", "notes", SchemaVersion, Schema)
{
    private const int SchemaVersion = 1;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS conversation_notes (
            kind INTEGER NOT NULL,
            stable_key TEXT NOT NULL,
            note TEXT NOT NULL,
            PRIMARY KEY (kind, stable_key)
        );
        """;

    private long revision;

    public long Revision => Interlocked.Read(ref revision);

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
        Interlocked.Increment(ref revision);
        return true;
    });
}
