using System.Globalization;
using NoClose.Core.Guards;

namespace NoClose.Core.Diagnostics.Rules;

internal static class Org
{
    public const string Advice =
        "This is set by the machine owner or your IT administrator. NoClose does not change it. " +
        "If it interferes with your work, ask them to adjust it through their management tools.";
}

/// <summary>Helpers shared by the rules.</summary>
internal static class R
{
    public static int? AsInt(object? v)
    {
        switch (v)
        {
            case null: return null;
            case int i: return i;
            case uint u: return (int)Math.Min(u, int.MaxValue);
            case long l: return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
            case string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n): return n;
            default: return null;
        }
    }

    public static long? AsLong(object? v) => v switch
    {
        null => null,
        int i => i,
        uint u => u,
        long l => l,
        string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null
    };

    public static int? Hklm(IMachineReader r, string key, string name) => AsInt(r.ReadRegistry(RegHive.HKLM, key, name));
    public static int? Hkcu(IMachineReader r, string key, string name) => AsInt(r.ReadRegistry(RegHive.HKCU, key, name));

    public static DiagnosticFinding Find(string rule, Severity sev, string title, string detail, GuardId? addressedBy = null) =>
        new(rule, sev, title, detail, null, "No action needed unless this is unexpected.", addressedBy, false);

    public static DiagnosticFinding Find(string rule, Severity sev, string title, string detail, string advice, GuardId? addressedBy) =>
        new(rule, sev, title, detail, null, advice, addressedBy, false);

    /// <summary>Finding about an organisation controlled setting (detect and report only).</summary>
    public static DiagnosticFinding OrgFind(string rule, Severity sev, string title, string detail, string policyName, GuardId? addressedBy = null) =>
        new(rule, sev, title, detail, policyName, Org.Advice, addressedBy, true);

    public static string Minutes(long totalMinutes)
    {
        if (totalMinutes <= 0) return "never";
        if (totalMinutes < 120) return totalMinutes + " min";
        return $"{totalMinutes / 60.0:0.#} h";
    }

    /// <summary>Power timeout value in seconds formatted for display.</summary>
    public static string Seconds(uint? secs)
    {
        if (secs == null) return "unknown";
        if (secs == 0) return "never";
        return Minutes((long)Math.Ceiling(secs.Value / 60.0));
    }

    public static string Plural(int n, string singular) => n == 1 ? $"{n} {singular}" : $"{n} {singular}s";

    public static bool IsPendingReboot(IMachineReader r, out bool strong)
    {
        strong = false;
        bool wua = r.WuaRebootRequired == true;
        bool keys = r.KeyExists(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                    || r.KeyExists(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        strong = wua || keys;
        if (strong) return true;
        var vol = AsLong(r.ReadRegistry(RegHive.HKLM, @"SOFTWARE\Microsoft\Updates", "UpdateExeVolatile"));
        return vol is { } v && v != 0;
    }
}
