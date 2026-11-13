using System.Globalization;
using NoClose.Core.Activation;
using NoClose.Core.Guards;

namespace NoClose.Core.Cli;

/// <summary>Pure helpers that turn an activation (or the whole app state) into command line arguments that CliParser reads back.</summary>
public static class ActivationArgs
{
    /// <summary>Arguments reproducing an activation. A Duration passes <paramref name="remaining"/> (when known), not its original length.</summary>
    public static IEnumerable<string> FromSpec(ActivationSpec spec, string reason, TimeSpan? remaining = null)
    {
        yield return "--start";
        switch (spec.Kind)
        {
            case ActivationKind.Duration when spec.Duration is { } d:
                {
                    var span = remaining ?? d;
                    yield return "--duration";
                    yield return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
                    break;
                }
            case ActivationKind.Until when spec.UntilLocal is { } u:
                yield return "--until";
                yield return u.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
                break;
            case ActivationKind.WhileProcess:
                yield return "--while-process";
                yield return spec.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? spec.ProcessName ?? "";
                break;
        }
        yield return "--reason";
        yield return reason;
    }

    /// <summary>
    /// Command line for the elevated copy, composed from the CURRENT state and never from the original launch arguments (which may contain
    /// mode switches that would clash with the ones added here, or a stale full-length duration). Only --minimized survives from the launch.
    /// </summary>
    public static IReadOnlyList<string> ForElevatedCopy(bool minimized, bool wasActive, ActivationSpec spec, string reason,
        TimeSpan? remaining, IReadOnlyList<GuardId>? appliedGuards, string userSid)
    {
        var args = new List<string>();
        if (minimized) args.Add("--minimized");
        if (wasActive)
        {
            args.AddRange(FromSpec(spec, reason, remaining));
            if (appliedGuards is { Count: > 0 })
            {
                args.Add("--guards");
                args.Add(string.Join(',', appliedGuards.Select(g => g.ToString())));
            }
        }
        args.Add("--handoff");
        args.Add(userSid);
        return args;
    }
}

public enum ResumeOutcome { NoSavedState, Resume, Skip }

public sealed record ResumeDecision(ResumeOutcome Outcome, ActivationSpec? Spec, string? Reason, string Message)
{
    /// <summary>
    /// Decides what a restart after servicing does with the saved activation. Saved arguments that cannot be understood, or that have
    /// expired during the restart, must NOT turn into an indefinite activation; a Duration resumes with its remaining time only.
    /// </summary>
    public static ResumeDecision Decide(string[]? savedArgs, DateTime? savedDeadlineUtc, DateTime utcNow, DateTime nowLocal, TimeZoneInfo? tz)
    {
        if (savedArgs is not { Length: > 0 })
            return new ResumeDecision(ResumeOutcome.NoSavedState, null, null, "No saved activation.");

        var parsed = CliParser.Parse(savedArgs, nowLocal, tz);
        if (!parsed.Ok || parsed.Command is not { Verb: CliVerb.Start, Activation: { } spec } cmd)
            return new ResumeDecision(ResumeOutcome.Skip, null, null,
                "The saved activation could not be resumed (it is unreadable or its end time has already passed).");

        if (savedDeadlineUtc is { } dl && utcNow >= dl)
            return new ResumeDecision(ResumeOutcome.Skip, null, null, "The saved activation ended while the PC was restarting.");

        if (spec.Kind == ActivationKind.Duration)
        {
            if (savedDeadlineUtc is not { } deadline)
                return new ResumeDecision(ResumeOutcome.Skip, null, null, "The remaining time of the saved activation is unknown; not resumed.");
            var left = deadline - utcNow;
            if (left <= TimeSpan.Zero)
                return new ResumeDecision(ResumeOutcome.Skip, null, null, "The saved activation ended while the PC was restarting.");
            spec = ActivationSpec.ForDuration(left);
        }
        return new ResumeDecision(ResumeOutcome.Resume, spec, cmd.Reason, "Resuming the saved activation.");
    }
}
