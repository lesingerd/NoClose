using System.Text.Json.Serialization;
using NoClose.Core.Guards;

namespace NoClose.Core.Journal;

public enum JournalKind { RegistryValue, PowerActiveScheme, SpiScreenSaveActive }

public enum JournalEntryStatus { Pending, Applied, Restored, Failed }

public enum JournalValueState
{
    [JsonStringEnumMemberName("present")] Present,
    [JsonStringEnumMemberName("absent")] Absent
}

public enum JournalValueType { DWord, QWord, String, ExpandString, MultiString, Binary, Bool }

/// <summary>A recorded value (registry value or boolean). "Absent" means the value did not exist.</summary>
public sealed class JournalValue
{
    public JournalValueState State { get; set; }
    public JournalValueType? Type { get; set; }
    /// <summary>DWord/QWord decimal string, String as is, Binary base64, Bool "true"/"false".</summary>
    public string? Data { get; set; }
    /// <summary>MultiString values.</summary>
    public List<string>? DataList { get; set; }

    public static JournalValue Absent() => new() { State = JournalValueState.Absent };
    public static JournalValue DWord(uint v) => new() { State = JournalValueState.Present, Type = JournalValueType.DWord, Data = v.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    public static JournalValue Bool(bool v) => new() { State = JournalValueState.Present, Type = JournalValueType.Bool, Data = v ? "true" : "false" };
    public static JournalValue Text(string v) => new() { State = JournalValueState.Present, Type = JournalValueType.String, Data = v };

    public bool IsAbsent => State == JournalValueState.Absent;

    public bool ValueEquals(JournalValue? other)
    {
        if (other == null) return false;
        if (State != other.State) return false;
        if (IsAbsent) return true;
        if (Type != other.Type || Data != other.Data) return false;
        if (DataList == null && other.DataList == null) return true;
        return DataList != null && other.DataList != null && DataList.SequenceEqual(other.DataList);
    }

    public uint? AsDWord() =>
        Type == JournalValueType.DWord && uint.TryParse(Data, out var v) ? v : null;

    public bool? AsBool() =>
        Type == JournalValueType.Bool && bool.TryParse(Data, out var v) ? v : null;
}

/// <summary>What was changed. Which fields are used depends on <see cref="JournalEntry.Kind"/>.</summary>
public sealed class JournalTarget
{
    // RegistryValue
    public string? Hive { get; set; }
    public string? View { get; set; }
    public string? Key { get; set; }
    public string? Name { get; set; }
    // PowerActiveScheme
    public string? OriginalScheme { get; set; }
    public string? TempScheme { get; set; }
    public string? TempName { get; set; }
    // SpiScreenSaveActive
    public int? SessionId { get; set; }
}

public sealed class JournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public GuardId Guard { get; set; }
    public JournalKind Kind { get; set; }
    public DateTime RecordedUtc { get; set; }
    public JournalTarget Target { get; set; } = new();
    public JournalValue? Original { get; set; }
    public JournalValue? Applied { get; set; }
    public JournalEntryStatus Status { get; set; } = JournalEntryStatus.Pending;

    /// <summary>True if restoring this entry needs administrator rights.</summary>
    public bool RequiresAdmin => Kind switch
    {
        JournalKind.PowerActiveScheme => true,
        JournalKind.RegistryValue => string.Equals(Target.Hive, "HKLM", StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}

public sealed class RestoreJournal
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Incremented on every save; lets a load prefer the newer of the main file and a complete leftover .tmp file.</summary>
    public long Sequence { get; set; }
    public int SessionId { get; set; }
    public int ProcessId { get; set; }
    public DateTime ProcessStartUtc { get; set; }
    public string Machine { get; set; } = "";
    public string UserSid { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<JournalEntry> Entries { get; set; } = new();

    public bool AllRestored => Entries.All(e => e.Status == JournalEntryStatus.Restored);
}
