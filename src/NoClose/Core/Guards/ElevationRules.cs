namespace NoClose.Core.Guards;

/// <summary>Pure elevation decisions (token elevation type values: 1 default, 2 full, 3 limited).</summary>
public static class ElevationRules
{
    public const int TokenElevationTypeDefault = 1, TokenElevationTypeFull = 2, TokenElevationTypeLimited = 3;

    /// <summary>
    /// "Restart elevated" is only offered to an administrator running with a filtered (limited) token. A standard user would get the
    /// over-the-shoulder credential prompt and the elevated copy would run as a DIFFERENT account (other HKCU, journal, pipe, settings).
    /// </summary>
    public static bool CanRestartElevated(bool isElevated, int tokenElevationType) =>
        !isElevated && tokenElevationType == TokenElevationTypeLimited;

    /// <summary>True when the elevated copy runs as the user who asked for the elevation (compares SIDs; empty never matches).</summary>
    public static bool SameUser(string? requestedSid, string? currentSid) =>
        !string.IsNullOrEmpty(requestedSid) && string.Equals(requestedSid, currentSid, StringComparison.OrdinalIgnoreCase);
}
