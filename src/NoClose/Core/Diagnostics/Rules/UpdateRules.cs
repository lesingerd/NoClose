using NoClose.Core.Guards;

namespace NoClose.Core.Diagnostics.Rules;

/// <summary>R-WU-REBOOT: a restart is pending.</summary>
public sealed class PendingRebootRule : IDiagnosticRule
{
    public string Id => "R-WU-REBOOT";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var reasons = new List<string>();
        if (r.WuaRebootRequired == true) reasons.Add("Windows Update Agent reports RebootRequired");
        if (r.KeyExists(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reasons.Add("Windows Update RebootRequired key present");
        if (r.KeyExists(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            reasons.Add("Component Based Servicing RebootPending key present");
        bool strong = reasons.Count > 0;

        var vol = R.AsLong(r.ReadRegistry(RegHive.HKLM, @"SOFTWARE\Microsoft\Updates", "UpdateExeVolatile"));
        bool volatileFlag = vol is { } v && v != 0;
        if (volatileFlag) reasons.Add("UpdateExeVolatile is set");

        bool renames = r.ReadRegistry(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations") != null;
        if (renames) reasons.Add("PendingFileRenameOperations present (informational)");

        if (reasons.Count == 0) yield break;
        var sev = strong ? Severity.High : volatileFlag ? Severity.Medium : Severity.Info;
        yield return R.Find(Id, sev, "A restart is pending",
            "A restart is pending. Windows may restart outside active hours or at an update deadline. " +
            "Restart at a convenient time before starting a long job." + Environment.NewLine + string.Join("; ", reasons) + ".",
            "Restart the PC at a convenient time before starting the job.",
            strong ? GuardId.ActiveHours : null);
    }
}

/// <summary>R-WU-HOURS: configured Windows Update active hours.</summary>
public sealed class ActiveHoursRule : IDiagnosticRule
{
    public string Id => "R-WU-HOURS";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        const string key = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        var start = R.Hklm(r, key, "ActiveHoursStart");
        var end = R.Hklm(r, key, "ActiveHoursEnd");
        var smart = R.Hklm(r, key, "SmartActiveHoursState");
        if (start == null || end == null) yield break;

        bool inside = ActiveHoursPlanner.Contains(start.Value, end.Value, r.NowLocal.Hour);
        bool pending = R.IsPendingReboot(r, out _);
        var sev = !inside && pending ? Severity.Medium : Severity.Low;
        var mode = smart == 1 ? "automatic (smart)" : "manual";
        yield return R.Find(Id, sev, $"Active hours {start:00}:00 - {end:00}:00 ({mode})",
            $"Windows avoids automatic restarts during active hours. It is now {(inside ? "inside" : "outside")} the window." +
            (!inside && pending ? " A restart is pending, so Windows may restart now." : ""),
            "The optional Active hours guard (administrator) can widen the window to 18 hours while a job runs.",
            GuardId.ActiveHours);
    }
}

/// <summary>R-WU-POLICY: Windows Update policies (deadlines, WSUS, active hours).</summary>
public sealed class UpdatePolicyRule : IDiagnosticRule
{
    public string Id => "R-WU-POLICY";

    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string CspKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\Update";

    private static readonly string[] Names =
    {
        "SetActiveHours", "ActiveHoursStart", "ActiveHoursEnd", "ActiveHoursMaxRange",
        "ConfigureDeadlineForQualityUpdates", "ConfigureDeadlineForFeatureUpdates", "ConfigureDeadlineGracePeriod",
        "ConfigureDeadlineGracePeriodForFeatureUpdates", "ConfigureDeadlineNoAutoReboot", "WUServer",
    };

    private static readonly string[] AuNames = { "UseWUServer", "AUOptions", "NoAutoRebootWithLoggedOnUsers", "ScheduledInstallTime" };

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var found = new List<string>();
        foreach (var n in Names)
            if (r.ReadRegistry(RegHive.HKLM, PolicyKey, n) != null) found.Add(n);
        foreach (var n in AuNames)
            if (r.ReadRegistry(RegHive.HKLM, PolicyKey + @"\AU", n) != null) found.Add(@"AU\" + n);
        var csp = r.ValueNames(RegHive.HKLM, CspKey)
            .Where(n => !n.Contains('_'))
            .Select(n => "CSP " + n)
            .ToList();
        found.AddRange(csp);
        if (found.Count == 0) yield break;

        bool deadlines = found.Any(n => n.Contains("ConfigureDeadline", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("Deadline", StringComparison.OrdinalIgnoreCase));
        var detail = "Update policies present: " + string.Join(", ", found) + ".";
        if (deadlines)
            detail += Environment.NewLine + "After the deadline Windows restarts regardless of active hours or NoClose.";
        yield return R.OrgFind(Id, deadlines ? Severity.Medium : Severity.Info,
            deadlines ? "Update deadlines are enforced by your organisation" : "Windows Update is configured by your organisation",
            detail,
            "Specify deadlines for automatic updates and restarts / Turn off auto-restart for updates during active hours / No auto-restart with logged on users (Windows Update policies; CSP Update/*)");
    }
}

/// <summary>R-MGMT: managed device (MDM, domain, WSUS).</summary>
public sealed class ManagedDeviceRule : IDiagnosticRule
{
    public string Id => "R-MGMT";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var parts = new List<string>();
        foreach (var sub in r.SubKeyNames(RegHive.HKLM, @"SOFTWARE\Microsoft\Enrollments"))
            if (r.ReadRegistry(RegHive.HKLM, @"SOFTWARE\Microsoft\Enrollments\" + sub, "ProviderID") is string p && p.Length > 0)
            {
                parts.Add("MDM enrollment (" + p + ")");
                break;
            }
        if (R.Hklm(r, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "UseWUServer") == 1
            || r.ReadRegistry(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer") != null)
            parts.Add("WSUS update server");
        if (!string.IsNullOrEmpty(r.UserDomainName) && !r.UserDomainName.Equals(r.MachineName, StringComparison.OrdinalIgnoreCase))
            parts.Add("domain account (" + r.UserDomainName + ")");
        if (parts.Count == 0) yield break;
        yield return R.OrgFind(Id, Severity.Info, "This device is managed",
            "This device is managed (MDM / domain / WSUS): " + string.Join(", ", parts) + ". Settings set by your organisation take precedence.",
            "Device management (MDM / Group Policy / WSUS)");
    }
}

/// <summary>R-ARSO: automatic restart sign-on.</summary>
public sealed class AutoRestartSignOnRule : IDiagnosticRule
{
    public string Id => "R-ARSO";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var v = R.Hklm(r, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "DisableAutomaticRestartSignOn");
        if (v != 1) yield break;
        yield return R.OrgFind(Id, Severity.Info, "Automatic restart sign-on is disabled",
            "After an update restart the session will not be resumed automatically.",
            "Sign-in and lock last interactive user automatically after a restart (disabled)");
    }
}
