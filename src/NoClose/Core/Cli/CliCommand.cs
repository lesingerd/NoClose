using NoClose.Core.Activation;
using NoClose.Core.Guards;

namespace NoClose.Core.Cli;

public enum CliVerb { None, Start, Stop, Diagnose, Restore, Help }

public sealed record CliCommand(CliVerb Verb, ActivationSpec? Activation, string? Reason,
    bool Minimized, string? OutPath, IReadOnlyList<GuardId>? GuardOverride, bool Handoff, bool RestoredAfterRestart,
    string? HandoffSid = null)
{
    public static CliCommand Empty { get; } = new(CliVerb.None, null, null, false, null, null, false, false);

    /// <summary>Verb name used on the single-instance pipe.</summary>
    public string PipeVerb => Verb switch
    {
        CliVerb.Start => "start",
        CliVerb.Stop => "stop",
        CliVerb.Restore => "restore",
        CliVerb.Diagnose => "status",
        _ => "show"
    };
}

public sealed record CliParseResult(CliCommand? Command, string? Error)
{
    public bool Ok => Command != null;
}
