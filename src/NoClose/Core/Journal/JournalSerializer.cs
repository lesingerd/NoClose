using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoClose.Core.Journal;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RestoreJournal))]
internal sealed partial class JournalJsonContext : JsonSerializerContext
{
}

public sealed class JournalFormatException : Exception
{
    public JournalFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class JournalSerializer
{
    public static string Serialize(RestoreJournal journal) =>
        JsonSerializer.Serialize(journal, JournalJsonContext.Default.RestoreJournal);

    /// <summary>Unknown fields are tolerated; invalid JSON or an unsupported schemaVersion throws <see cref="JournalFormatException"/>.</summary>
    public static RestoreJournal Deserialize(string json)
    {
        RestoreJournal? j;
        try { j = JsonSerializer.Deserialize(json, JournalJsonContext.Default.RestoreJournal); }
        catch (JsonException ex) { throw new JournalFormatException("Journal is not valid JSON: " + ex.Message, ex); }
        if (j == null) throw new JournalFormatException("Journal is empty.");
        if (j.SchemaVersion != RestoreJournal.CurrentSchemaVersion)
            throw new JournalFormatException($"Unsupported journal schemaVersion {j.SchemaVersion} (expected {RestoreJournal.CurrentSchemaVersion}).");
        j.Entries ??= new List<JournalEntry>();
        foreach (var e in j.Entries) e.Target ??= new JournalTarget();
        return j;
    }
}
