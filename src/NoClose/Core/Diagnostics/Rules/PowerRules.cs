using NoClose.Core.Guards;

namespace NoClose.Core.Diagnostics.Rules;

/// <summary>R-OVR: NoClose appears in the powercfg request override lists.</summary>
public sealed class OverrideRule : IDiagnosticRule
{
    public string Id => "R-OVR";
    private const string Root = @"SYSTEM\CurrentControlSet\Control\Power\PowerRequestOverride";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var hits = new List<string>();
        foreach (var kind in new[] { "Process", "Service", "Driver" })
        {
            var key = Root + "\\" + kind;
            foreach (var n in r.ValueNames(RegHive.HKLM, key).Concat(r.SubKeyNames(RegHive.HKLM, key)))
                if (Matches(n)) hits.Add($"{kind}: {n}");
        }
        // Some systems keep the process list directly under the root as values.
        foreach (var n in r.ValueNames(RegHive.HKLM, Root))
            if (Matches(n)) hits.Add($"override: {n}");
        if (hits.Count == 0) yield break;

        yield return new DiagnosticFinding(Id, Severity.High,
            "Windows ignores NoClose's power requests",
            "Windows is configured to ignore NoClose's power requests (powercfg /requestsoverride). Entries: " + string.Join("; ", hits.Distinct()),
            null,
            "An administrator can review the list with 'powercfg /requestsoverride' and remove the NoClose entry. NoClose never edits it.",
            null, false);
    }

    private static bool Matches(string name)
    {
        var n = Path.GetFileName(name.Trim());
        return n.Equals("NoClose.exe", StringComparison.OrdinalIgnoreCase) || n.Equals("NoClose", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>R-ALLOW: the active power plan (or a policy) tells Windows to ignore stay-awake requests.</summary>
public sealed class AllowRequestsRule : IDiagnosticRule
{
    public string Id => "R-ALLOW";

    private sealed record Setting(Guid Sub, Guid Guid, string Label, string AcPolicy, string DcPolicy, Severity Sev);

    private static readonly Setting[] Settings =
    {
        new(PowerGuids.SubSleep, PowerGuids.AllowSystemRequired, "system required requests (keep the PC awake)",
            "Allow applications to prevent automatic sleep (plugged in)", "Allow applications to prevent automatic sleep (on battery)", Severity.High),
        new(PowerGuids.SubVideo, PowerGuids.AllowDisplayRequired, "display required requests (keep the display on)",
            "Turn on the ability for applications to prevent sleep transitions (plugged in)", "Turn on the ability for applications to prevent sleep transitions (on battery)", Severity.High),
        new(PowerGuids.SubSleep, PowerGuids.AllowAwayMode, "away mode requests",
            "Allow away mode policy (plugged in)", "Allow away mode policy (on battery)", Severity.Low),
    };

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        foreach (var s in Settings)
        {
            foreach (bool ac in new[] { true, false })
            {
                var power = ac ? "plugged in" : "on battery";
                var polKey = PowerGuids.PolicyKeyFor(s.Guid);
                var pol = R.Hklm(r, polKey, ac ? "ACSettingIndex" : "DCSettingIndex");
                if (pol != null)
                {
                    if (pol == 0)
                        yield return R.OrgFind(Id, s.Sev, $"Policy tells Windows to ignore {s.Label} ({power})",
                            "Power management policy disables this request type, so apps cannot keep the PC awake.",
                            (ac ? s.AcPolicy : s.DcPolicy) + " (Computer Configuration > Administrative Templates > System > Power Management > Sleep Settings)");
                    continue;
                }
                if (r.ActiveScheme is not { } scheme) continue;
                var v = r.ReadPowerValue(scheme, s.Sub, s.Guid, ac);
                if (v == 0)
                    yield return R.Find(Id, s.Sev, $"Power plan ignores {s.Label} ({power})",
                        "The active power plan tells Windows to ignore app requests to stay awake / keep the display on.",
                        "Enable the optional Power plan guard (administrator), or change the setting yourself with 'powercfg /setacvalueindex'.",
                        GuardId.PowerPlan);
            }
        }
    }
}

/// <summary>R-SLEEP: lists idle timeouts.</summary>
public sealed class SleepTimeoutRule : IDiagnosticRule
{
    public string Id => "R-SLEEP";

    private static readonly (Guid Sub, Guid Setting, string Label)[] Items =
    {
        (PowerGuids.SubSleep, PowerGuids.StandbyIdle, "Sleep after"),
        (PowerGuids.SubSleep, PowerGuids.HibernateIdle, "Hibernate after"),
        (PowerGuids.SubSleep, PowerGuids.UnattendSleep, "Unattended sleep timeout"),
        (PowerGuids.SubVideo, PowerGuids.VideoIdle, "Turn off display after"),
    };

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        if (r.ActiveScheme is not { } scheme) yield break;
        var lines = new List<string>();
        bool anyTimeout = false, anyPolicy = false;
        var policyNames = new List<string>();
        foreach (var (sub, setting, label) in Items)
        {
            var acPol = R.Hklm(r, PowerGuids.PolicyKeyFor(setting), "ACSettingIndex");
            var dcPol = R.Hklm(r, PowerGuids.PolicyKeyFor(setting), "DCSettingIndex");
            var ac = acPol is { } a ? (uint?)a : r.ReadPowerValue(scheme, sub, setting, true);
            var dc = dcPol is { } d ? (uint?)d : r.ReadPowerValue(scheme, sub, setting, false);
            if (acPol != null || dcPol != null) { anyPolicy = true; policyNames.Add(label); }
            if (ac is > 0 || dc is > 0) anyTimeout = true;
            lines.Add($"{label}: plugged in {R.Seconds(ac)}, battery {R.Seconds(dc)}{(acPol != null || dcPol != null ? " (set by policy)" : "")}");
        }
        var detail = string.Join(Environment.NewLine, lines)
                     + Environment.NewLine + "While NoClose is active its system/display requests prevent these idle timers from ending the session.";
        if (anyPolicy)
            yield return new DiagnosticFinding(Id, Severity.Low, "Sleep/display timeouts are set by your organisation", detail,
                "Power Management > Sleep Settings / Video and Display Settings: " + string.Join(", ", policyNames),
                Org.Advice, GuardId.ExecutionState, true);
        else
            yield return R.Find(Id, Severity.Info, "Idle timeouts", detail,
                anyTimeout ? "Covered by NoClose's keep-awake requests while it is active; the optional Power plan guard can also remove them." : "No idle timeouts are configured.",
                GuardId.ExecutionState);
    }
}

/// <summary>R-MS: Modern Standby (AoAc).</summary>
public sealed class ModernStandbyRule : IDiagnosticRule
{
    public string Id => "R-MS";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        if (!r.Caps.AoAc) yield break;
        if (!r.OnAcPower)
            yield return R.Find(Id, Severity.High, "Modern Standby on battery",
                "Modern Standby on battery: Windows ends app stay-awake requests 5 min after the sleep timeout. Keep the device plugged in.",
                "Plug the device in before starting a long job.", null);
        else
            yield return R.Find(Id, Severity.Info, "Modern Standby device",
                "This device uses Modern Standby. On AC power stay-awake requests are honoured; on battery Windows ends them 5 minutes after the sleep timeout (away mode is not available).");
    }
}

/// <summary>R-LID: lid / power button actions.</summary>
public sealed class LidRule : IDiagnosticRule
{
    public string Id => "R-LID";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        if (r.ActiveScheme is not { } scheme) yield break;
        var hits = new List<string>();
        foreach (var (setting, label) in new[] { (PowerGuids.LidAction, "Closing the lid"), (PowerGuids.PowerButtonAction, "Pressing the power button") })
        {
            foreach (bool ac in new[] { true, false })
            {
                var v = r.ReadPowerValue(scheme, PowerGuids.SubButtons, setting, ac);
                if (v is > 0) hits.Add($"{label} ({(ac ? "plugged in" : "on battery")}) = {ActionName(v.Value)}");
            }
        }
        if (hits.Count == 0) yield break;
        yield return R.Find(Id, Severity.Low, "Closing the lid / pressing power can sleep the PC",
            string.Join(Environment.NewLine, hits) + Environment.NewLine + "Closing the lid / pressing power will sleep regardless of NoClose.",
            "Leave the lid open, or set these actions to 'Do nothing' in Power Options.", null);
    }

    private static string ActionName(uint v) => v switch { 1 => "Sleep", 2 => "Hibernate", 3 => "Shut down", 4 => "Turn off display", _ => "action " + v };
}

/// <summary>R-SIGNIN-WAKE: require sign-in when the PC wakes.</summary>
public sealed class SignInOnWakeRule : IDiagnosticRule
{
    public string Id => "R-SIGNIN-WAKE";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var polKey = PowerGuids.PolicyKeyFor(PowerGuids.ConsoleLock);
        var polAc = R.Hklm(r, polKey, "ACSettingIndex");
        var polDc = R.Hklm(r, polKey, "DCSettingIndex");
        var delay = R.Hkcu(r, @"Control Panel\Desktop", "DelayLockInterval");
        uint? ac = null, dc = null;
        if (r.ActiveScheme is { } s)
        {
            ac = r.ReadPowerValue(s, PowerGuids.SubNone, PowerGuids.ConsoleLock, true);
            dc = r.ReadPowerValue(s, PowerGuids.SubNone, PowerGuids.ConsoleLock, false);
        }
        bool policy = polAc != null || polDc != null;
        bool required = policy ? (polAc ?? 1) != 0 || (polDc ?? 1) != 0 : ac == 1 || dc == 1;
        if (!required && delay == null) yield break;

        var detail = "If the PC does sleep, sign-in will be required on wake. NoClose prevents idle sleep; it does not change this sign-in setting."
                     + (delay is { } dl ? $"{Environment.NewLine}Lock delay after display off (DelayLockInterval): {dl} s." : "");
        if (policy)
            yield return R.OrgFind(Id, Severity.Info, "Sign-in required on wake (policy)", detail,
                "Require a password when a computer wakes (plugged in / on battery)", GuardId.ExecutionState);
        else
            yield return R.Find(Id, Severity.Info, "Sign-in required on wake", detail,
                "Nothing to do; NoClose avoids the sleep that would trigger this.", GuardId.ExecutionState);
    }
}
