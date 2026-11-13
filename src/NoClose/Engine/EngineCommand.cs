namespace NoClose.Engine;

internal abstract record EngineCommand;

internal sealed record EngineStart(string Reason, bool AwayMode) : EngineCommand;

internal sealed record EngineStop(string Why) : EngineCommand;

/// <summary>Resume, unlock, display-on, session connect, settings change, guard reapply.</summary>
internal sealed record EngineReassert(string Trigger) : EngineCommand;

internal sealed record EngineQuery(TaskCompletionSource<EngineStatus> Reply) : EngineCommand;

internal sealed record EngineShutdown(ManualResetEventSlim Done) : EngineCommand;
