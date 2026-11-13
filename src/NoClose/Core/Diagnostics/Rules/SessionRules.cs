using NoClose.Core.Guards;

namespace NoClose.Core.Diagnostics.Rules;

/// <summary>R-RDP: running inside a Remote Desktop session.</summary>
public sealed class RemoteSessionRule : IDiagnosticRule
{
    public string Id => "R-RDP";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var s = r.Session;
        if (!s.IsRemote) yield break;
        yield return R.Find(Id, Severity.Medium, "You are in a Remote Desktop session",
            $"Session {s.SessionId} is a remote session ({s.ProtocolName}, {s.ConnectStateName}). " +
            "Remote Desktop time limits are based on input and are not affected by NoClose. " +
            "A disconnected session does not count as signed in for Windows Update restart avoidance.",
            "Keep the remote session connected, or run the job on the console session if possible.", null);
    }
}

/// <summary>R-RDP-LIMITS: Remote Desktop session time limits (machine policy > user policy > listener).</summary>
public sealed class RemoteLimitsRule : IDiagnosticRule
{
    public string Id => "R-RDP-LIMITS";

    private const string MachinePolicy = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string UserPolicy = @"Software\Policies\Microsoft\Windows NT\Terminal Services";
    private const string Listener = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";

    private sealed record Limit(string Name, string Label, string InheritName, string PolicyName);

    private static readonly Limit[] Limits =
    {
        new("MaxIdleTime", "idle limit", "fInheritMaxIdleTime", "Set time limit for active but idle Remote Desktop Services sessions"),
        new("MaxDisconnectionTime", "disconnected session limit", "fInheritMaxDisconnectionTime", "Set time limit for disconnected sessions"),
        new("MaxConnectionTime", "active session limit", "fInheritMaxSessionTime", "Set time limit for active Remote Desktop Services sessions"),
    };

    /// <summary>Effective value and where it came from. Listener values are ignored when the listener inherits.</summary>
    internal static (long? Value, string Source) Effective(IMachineReader r, string name, string inheritName)
    {
        var m = R.AsLong(r.ReadRegistry(RegHive.HKLM, MachinePolicy, name));
        if (m != null) return (m, "machine policy");
        var u = R.AsLong(r.ReadRegistry(RegHive.HKCU, UserPolicy, name));
        if (u != null) return (u, "user policy");
        var l = R.AsLong(r.ReadRegistry(RegHive.HKLM, Listener, name));
        if (l != null && R.Hklm(r, Listener, inheritName) != 1) return (l, "RDP-Tcp listener");
        return (null, "");
    }

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var lines = new List<string>();
        var policyNames = new List<string>();
        bool anyLimit = false, anyPolicy = false;
        foreach (var lim in Limits)
        {
            var (v, src) = Effective(r, lim.Name, lim.InheritName);
            if (v is not > 0) continue;
            anyLimit = true;
            if (src != "RDP-Tcp listener") anyPolicy = true;
            policyNames.Add(lim.PolicyName);
            lines.Add($"{lim.Label}: {R.Minutes((long)Math.Ceiling(v.Value / 60000.0))} ({src})");
        }
        if (!anyLimit) yield break;

        var (reset, resetSrc) = Effective(r, "fResetBroken", "fInheritResetBroken");
        bool resets = reset == 1;
        if (resets) { lines.Add($"End session when time limits are reached: yes ({resetSrc})"); policyNames.Add("End session when time limits are reached"); }

        var sev = r.Session.IsRemote ? (resets ? Severity.High : Severity.Medium) : Severity.Info;
        var detail = string.Join(Environment.NewLine, lines) + Environment.NewLine +
                     "These limits are enforced by the Remote Desktop Session Host and are not reset by NoClose's keep-awake requests.";
        yield return new DiagnosticFinding(Id, sev,
            r.Session.IsRemote ? "Remote Desktop time limits apply to this session" : "Remote Desktop time limits are configured",
            detail,
            string.Join("; ", policyNames) + " (Remote Desktop Services > Remote Desktop Session Host > Session Time Limits)",
            Org.Advice, null, anyPolicy);
    }
}
