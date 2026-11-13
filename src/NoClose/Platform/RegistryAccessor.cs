using System.Globalization;
using Microsoft.Win32;
using NoClose.Core.Diagnostics;
using NoClose.Core.Journal;
using NoClose.Journal;

namespace NoClose.Platform;

/// <summary>
/// The only place in NoClose that writes to the registry. Every write is vetted by <see cref="RegistryWriteGuard"/>
/// (allow-list of the three Windows Update active-hours values; policy/MDM keys are always rejected).
/// </summary>
internal class RegistryAccessor
{
    public static RegistryKey? OpenBase(RegHive hive, RegView view) =>
        RegistryKey.OpenBaseKey(
            hive == RegHive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            view == RegView.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64);

    public static RegHive ParseHive(string? s) =>
        string.Equals(s, "HKCU", StringComparison.OrdinalIgnoreCase) ? RegHive.HKCU : RegHive.HKLM;

    public static RegView ParseView(string? s) => s == "32" ? RegView.Registry32 : RegView.Registry64;

    /// <summary>Reads the current value as a journal value (Absent if it does not exist).</summary>
    public virtual JournalValue Capture(RegHive hive, string key, string name, RegView view = RegView.Registry64)
    {
        using var baseKey = OpenBase(hive, view);
        using var sub = baseKey?.OpenSubKey(key, writable: false);
        if (sub == null) return JournalValue.Absent();
        var raw = sub.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw == null) return JournalValue.Absent();
        return ToJournal(raw, sub.GetValueKind(name));
    }

    public virtual void Apply(RegHive hive, string key, string name, JournalValue value, RegView view = RegView.Registry64)
    {
        RegistryWriteGuard.EnsureAllowed(hive, key, name);
        if (value.IsAbsent) { DeleteValue(hive, key, name, view); return; }
        var (data, kind) = FromJournal(value);
        SetValue(hive, key, name, data, kind, view);
    }

    public virtual void SetValue(RegHive hive, string key, string name, object data, RegistryValueKind kind, RegView view = RegView.Registry64)
    {
        RegistryWriteGuard.EnsureAllowed(hive, key, name);
        using var baseKey = OpenBase(hive, view) ?? throw new InvalidOperationException("Cannot open registry hive.");
        using var sub = baseKey.CreateSubKey(key, writable: true) ?? throw new InvalidOperationException("Cannot open registry key.");
        sub.SetValue(name, data, kind);
    }

    public virtual void DeleteValue(RegHive hive, string key, string name, RegView view = RegView.Registry64)
    {
        RegistryWriteGuard.EnsureAllowed(hive, key, name);
        using var baseKey = OpenBase(hive, view);
        using var sub = baseKey?.OpenSubKey(key, writable: true);
        sub?.DeleteValue(name, throwOnMissingValue: false);
    }

    public static JournalValue ToJournal(object raw, RegistryValueKind kind)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord:
                return new JournalValue { State = JournalValueState.Present, Type = JournalValueType.DWord, Data = unchecked((uint)(int)raw).ToString(CultureInfo.InvariantCulture) };
            case RegistryValueKind.QWord:
                return new JournalValue { State = JournalValueState.Present, Type = JournalValueType.QWord, Data = unchecked((ulong)(long)raw).ToString(CultureInfo.InvariantCulture) };
            case RegistryValueKind.ExpandString:
                return new JournalValue { State = JournalValueState.Present, Type = JournalValueType.ExpandString, Data = (string)raw };
            case RegistryValueKind.MultiString:
                return new JournalValue { State = JournalValueState.Present, Type = JournalValueType.MultiString, DataList = ((string[])raw).ToList() };
            case RegistryValueKind.Binary:
                return new JournalValue { State = JournalValueState.Present, Type = JournalValueType.Binary, Data = Convert.ToBase64String((byte[])raw) };
            default:
                return new JournalValue { State = JournalValueState.Present, Type = JournalValueType.String, Data = Convert.ToString(raw, CultureInfo.InvariantCulture) };
        }
    }

    public static (object Data, RegistryValueKind Kind) FromJournal(JournalValue v)
    {
        switch (v.Type)
        {
            case JournalValueType.DWord:
                return (unchecked((int)uint.Parse(v.Data ?? "0", CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
            case JournalValueType.QWord:
                return (unchecked((long)ulong.Parse(v.Data ?? "0", CultureInfo.InvariantCulture)), RegistryValueKind.QWord);
            case JournalValueType.ExpandString:
                return (v.Data ?? "", RegistryValueKind.ExpandString);
            case JournalValueType.MultiString:
                return ((v.DataList ?? new List<string>()).ToArray(), RegistryValueKind.MultiString);
            case JournalValueType.Binary:
                return (Convert.FromBase64String(v.Data ?? ""), RegistryValueKind.Binary);
            case JournalValueType.Bool:
                return (v.Data ?? "false", RegistryValueKind.String);
            default:
                return (v.Data ?? "", RegistryValueKind.String);
        }
    }
}
