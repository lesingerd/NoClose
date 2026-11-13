namespace NoClose.Engine;

internal enum EngineState { Idle, Active, Degraded }

/// <summary>Immutable snapshot published by the engine.</summary>
internal sealed record EngineStatus(
    EngineState State,
    string Reason,
    DateTime? AssertedAtUtc,
    DateTime? LastVerifiedUtc,
    string VerifyResult,
    string? DegradedReason,
    bool AwayModeActive,
    string? Note)
{
    public static readonly EngineStatus Idle = new(EngineState.Idle, "", null, null, "", null, false, null);

    public bool IsActive => State != EngineState.Idle;
}
