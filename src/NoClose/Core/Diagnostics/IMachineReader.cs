namespace NoClose.Core.Diagnostics;

public enum RegHive { HKLM, HKCU }

public enum RegView { Default, Registry64, Registry32 }

/// <summary>Power capabilities relevant for stay-awake behaviour.</summary>
public sealed record PowerCaps(bool S3, bool S4, bool AoAc)
{
    public bool IsS3Capable => S3;
}

/// <summary>Information about the session NoClose runs in.</summary>
public sealed record SessionInfo(bool IsRemote, int Protocol, int ConnectState, int SessionId, int ConsoleSessionId)
{
    public bool IsConsole => !IsRemote && SessionId == ConsoleSessionId;

    public string Describe() => IsRemote
        ? $"{SessionId} (remote, {ProtocolName}, {ConnectStateName})"
        : SessionId == ConsoleSessionId ? $"{SessionId} (console)" : $"{SessionId} (local, not the console)";

    public string ProtocolName => Protocol switch { 0 => "console", 1 => "ICA", 2 => "RDP", _ => "protocol " + Protocol };

    public string ConnectStateName => ConnectState switch
    {
        0 => "active", 1 => "connected", 2 => "connect query", 3 => "shadow", 4 => "disconnected",
        5 => "idle", 6 => "listening", 7 => "reset", 8 => "down", 9 => "init", _ => "state " + ConnectState
    };
}

/// <summary>Everything the diagnostics rules may read. Implementations must be strictly read-only.</summary>
public interface IMachineReader
{
    /// <summary>Returns the value (int, long, string, byte[], string[]) or null if absent.</summary>
    object? ReadRegistry(RegHive hive, string subKey, string valueName, RegView view = RegView.Default);
    bool KeyExists(RegHive hive, string subKey);
    IReadOnlyList<string> SubKeyNames(RegHive hive, string subKey);
    IReadOnlyList<string> ValueNames(RegHive hive, string subKey);
    uint? ReadPowerValue(Guid scheme, Guid sub, Guid setting, bool ac);
    Guid? ActiveScheme { get; }
    PowerCaps Caps { get; }
    bool OnAcPower { get; }
    SessionInfo Session { get; }
    /// <summary>Windows Update Agent "RebootRequired"; null if it could not be queried.</summary>
    bool? WuaRebootRequired { get; }
    /// <summary>Output of "powercfg /requests" (administrator only); otherwise null.</summary>
    string? PowercfgRequests { get; }
    /// <summary>Read-only: does a default audio render endpoint exist? null when it could not be determined.</summary>
    bool? HasAudioRenderEndpoint => null;
    DateTime NowLocal { get; }
    string MachineName { get; }
    string UserName { get; }
    string UserDomainName { get; }
}

public enum Severity { Info, Low, Medium, High }

public sealed record DiagnosticFinding(string RuleId, Severity Severity, string Title, string Detail,
    string? PolicyName, string Advice, Guards.GuardId? AddressedBy, bool OrganisationControlled);

public interface IDiagnosticRule
{
    string Id { get; }
    IEnumerable<DiagnosticFinding> Evaluate(IMachineReader reader);
}

/// <summary>Engine state handed to R-ENGINE (not read through the machine reader).</summary>
public sealed record EngineSnapshot(bool Active, bool Degraded, string? DegradedReason);

public sealed record MachineSummary(DateTimeOffset Time, string Computer, string User, bool Elevated,
    string Session, string Power, bool ModernStandby, string ActiveScheme)
{
    public static MachineSummary From(IMachineReader r, bool elevated, DateTimeOffset now)
    {
        var scheme = r.ActiveScheme is { } g
            ? $"{Guards.PowerGuids.FriendlySchemeName(g)} {{{g:D}}}"
            : "unknown";
        return new MachineSummary(now, r.MachineName, r.UserName, elevated, r.Session.Describe(),
            r.OnAcPower ? "AC" : "Battery", r.Caps.AoAc, scheme);
    }
}
