using NoClose.Core.Guards;

namespace NoClose.Core.Diagnostics.Rules;

/// <summary>R-INACT: "Interactive logon: Machine inactivity limit".</summary>
public sealed class InactivityLimitRule : IDiagnosticRule
{
    public string Id => "R-INACT";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var secs = R.Hklm(r, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "InactivityTimeoutSecs");
        if (secs is not > 0) yield break;
        var minutes = (int)Math.Ceiling(secs.Value / 60.0);
        yield return R.OrgFind(Id, Severity.Medium, $"Machine inactivity limit: {minutes} min",
            $"Your organisation intends the session to lock after {R.Plural(minutes, "minute")} without keyboard/mouse input. " +
            "This limit is based on user input, which NoClose deliberately never simulates, so the session can still lock.",
            "Interactive logon: Machine inactivity limit (Computer Configuration > Windows Settings > Security Settings > Local Policies > Security Options)");
    }
}

/// <summary>R-SS-POL: screen saver policies (user configuration).</summary>
public sealed class ScreenSaverPolicyRule : IDiagnosticRule
{
    public string Id => "R-SS-POL";
    public const string Key = @"Software\Policies\Microsoft\Windows\Control Panel\Desktop";

    private static readonly (string Value, string Policy)[] Items =
    {
        ("ScreenSaveActive", "Enable screen saver"),
        ("ScreenSaverIsSecure", "Password protect the screen saver"),
        ("ScreenSaveTimeOut", "Screen saver timeout"),
        ("SCRNSAVE.EXE", "Force specific screen saver"),
    };

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var present = new List<string>();
        var names = new List<string>();
        foreach (var (value, policy) in Items)
        {
            var v = r.ReadRegistry(RegHive.HKCU, Key, value);
            if (v == null) continue;
            present.Add($"{value} = {v}");
            names.Add(policy);
        }
        if (present.Count == 0) yield break;
        yield return R.OrgFind(Id, Severity.Medium, "Screen saver is controlled by Group Policy",
            string.Join(Environment.NewLine, present),
            string.Join(" / ", names) + " (User Configuration > Administrative Templates > Control Panel > Personalization)");
    }
}

/// <summary>R-SS-USER: the user's own screen saver settings.</summary>
public sealed class ScreenSaverUserRule : IDiagnosticRule
{
    public string Id => "R-SS-USER";
    private const string Key = @"Control Panel\Desktop";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var active = R.Hkcu(r, Key, "ScreenSaveActive");
        var timeout = R.Hkcu(r, Key, "ScreenSaveTimeOut");
        var secure = R.Hkcu(r, Key, "ScreenSaverIsSecure");
        var exe = r.ReadRegistry(RegHive.HKCU, Key, "SCRNSAVE.EXE") as string;
        if (active != 1 || timeout is not > 0) yield break;
        var minutes = (int)Math.Ceiling(timeout.Value / 60.0);
        yield return R.Find(Id, Severity.Low, "Screen saver is enabled",
            $"Screen saver starts after {R.Plural(minutes, "minute")}{(secure == 1 ? " and asks for sign-in on resume" : "")}" +
            (string.IsNullOrEmpty(exe) ? "." : $" ({Path.GetFileName(exe)})."),
            "NoClose's display request normally prevents it from starting; the Screen saver guard additionally switches it off for this session only.",
            GuardId.ScreenSaver);
    }
}

/// <summary>R-MDM-LOCK: Intune/MDM DeviceLock policy.</summary>
public sealed class MdmDeviceLockRule : IDiagnosticRule
{
    public string Id => "R-MDM-LOCK";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        const string key = @"SOFTWARE\Microsoft\PolicyManager\current\device\DeviceLock";
        var max = R.Hklm(r, key, "MaxInactivityTimeDeviceLock");
        var pw = R.Hklm(r, key, "DevicePasswordEnabled");
        if (max is > 0)
            yield return R.OrgFind(Id, Severity.Medium, $"Device lock after {max} min of inactivity (MDM)",
                $"Your organisation's device management locks the device after {R.Plural(max.Value, "minute")} without input. NoClose does not simulate input, so this can still lock the session.",
                "DeviceLock/MaxInactivityTimeDeviceLock (Intune / MDM)");
        else if (pw != null)
            yield return R.OrgFind(Id, Severity.Info, "Device password policy present (MDM)",
                $"DevicePasswordEnabled = {pw}", "DeviceLock/DevicePasswordEnabled (Intune / MDM)");
    }
}

/// <summary>R-DYNLOCK: Dynamic Lock.</summary>
public sealed class DynamicLockRule : IDiagnosticRule
{
    public string Id => "R-DYNLOCK";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var user = R.Hkcu(r, @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "EnableGoodbye");
        var pol = R.Hklm(r, @"SOFTWARE\Policies\Microsoft\PassportForWork\DynamicLock", "DynamicLock");
        if (pol == 1)
            yield return R.OrgFind(Id, Severity.Low, "Dynamic Lock is enabled by policy",
                "Dynamic Lock locks the PC when your paired phone goes out of range.",
                "Configure Dynamic Lock Signals (Windows Hello for Business)");
        else if (user == 1)
            yield return R.Find(Id, Severity.Low, "Dynamic Lock is on",
                "Dynamic Lock locks the PC when your paired phone goes out of range.",
                "Turn it off in Settings > Accounts > Sign-in options if needed. NoClose does not change it.", null);
    }
}

/// <summary>R-SCARD: smart card removal behaviour.</summary>
public sealed class SmartCardRemovalRule : IDiagnosticRule
{
    public string Id => "R-SCARD";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var v = r.ReadRegistry(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "ScRemoveOption") as string;
        var action = v?.Trim() switch { "1" => "lock the workstation", "2" => "force a logoff", "3" => "disconnect the Remote Desktop session", _ => null };
        if (action == null) yield break;
        yield return R.OrgFind(Id, Severity.Low, "Removing the smart card will end the session",
            $"Removing the smart card will {action}.", "Interactive logon: Smart card removal behavior");
    }
}
