namespace NoClose.Core.Guards;

public enum OverallState { Idle, Active, Degraded, NotKeepingAwake }

/// <summary>Pure: what the window and tray show for "running" plus the engine state.</summary>
public static class StatusSummary
{
    /// <summary>
    /// A running activation with an idle engine (power requests switched off, guard disabled, engine thread dead) is NOT keeping the
    /// session awake and must not be shown as green "Active".
    /// </summary>
    public static OverallState Compute(bool policyRunning, bool engineIdle, bool engineDegraded)
    {
        if (!policyRunning) return OverallState.Idle;
        if (engineDegraded) return OverallState.Degraded;
        if (engineIdle) return OverallState.NotKeepingAwake;
        return OverallState.Active;
    }

    public static string Label(OverallState s) => s switch
    {
        OverallState.Idle => "Idle",
        OverallState.Active => "Active",
        OverallState.Degraded => "Degraded",
        _ => "Not keeping awake",
    };
}
