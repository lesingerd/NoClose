using NoClose.Core.Time;

namespace NoClose.Core.Activation;

public readonly record struct StopDecision(bool Stop, string Reason)
{
    public static readonly StopDecision Continue = new(false, "");
}

/// <summary>
/// Pure state machine deciding when an activation ends. Thread-safe: the engine thread, the UI timer and the
/// process watcher may all call into it.
/// </summary>
public sealed class ActivationPolicy
{
    /// <summary>Stop only after this many consecutive "process not running" observations.</summary>
    public const int ProcessGraceMisses = 2;

    private readonly object _gate = new();
    private readonly IClock _clock;
    private int _misses;

    public ActivationPolicy(IClock clock) => _clock = clock;

    public ActivationSpec Spec { get; private set; } = ActivationSpec.Indefinite;
    public DateTime StartedUtc { get; private set; }
    public DateTime? DeadlineUtc { get; private set; }
    public bool IsRunning { get; private set; }

    public void Begin(ActivationSpec spec, DateTime nowUtc)
    {
        lock (_gate)
        {
            Spec = spec;
            StartedUtc = nowUtc;
            _misses = 0;
            IsRunning = true;
            DeadlineUtc = spec.Kind switch
            {
                ActivationKind.Duration when spec.Duration is { } d => nowUtc + d,
                ActivationKind.Until when spec.UntilLocal is { } u => ToUtc(u),
                _ => null
            };
        }
    }

    public void Begin(ActivationSpec spec) => Begin(spec, _clock.UtcNow);

    public void End()
    {
        lock (_gate) { IsRunning = false; DeadlineUtc = null; _misses = 0; }
    }

    private DateTime ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var tz = _clock.TimeZone;
        if (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }

    /// <summary>
    /// Evaluate the stop conditions. <paramref name="processAlive"/> is only meaningful for WhileProcess; pass
    /// <c>true</c> for other kinds. Each call with <c>false</c> counts as one poll.
    /// </summary>
    public StopDecision ShouldStop(DateTime nowUtc, bool processAlive = true)
    {
        lock (_gate)
        {
            if (!IsRunning) return StopDecision.Continue;
            if (DeadlineUtc is { } dl && nowUtc >= dl)
                return new StopDecision(true, Spec.Kind == ActivationKind.Duration ? "duration elapsed" : "end time reached");
            if (Spec.Kind == ActivationKind.WhileProcess)
            {
                if (processAlive) { _misses = 0; return StopDecision.Continue; }
                if (++_misses >= ProcessGraceMisses)
                    return new StopDecision(true, "watched process ended");
            }
            return StopDecision.Continue;
        }
    }

    /// <summary>Only the time based conditions (never touches the process grace counter).</summary>
    public StopDecision CheckDeadline(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!IsRunning) return StopDecision.Continue;
            if (DeadlineUtc is { } dl && nowUtc >= dl)
                return new StopDecision(true, Spec.Kind == ActivationKind.Duration ? "duration elapsed" : "end time reached");
            return StopDecision.Continue;
        }
    }

    /// <summary>Time left until the deadline, or null if there is none.</summary>
    public TimeSpan? Remaining(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!IsRunning || DeadlineUtc is not { } dl) return null;
            var r = dl - nowUtc;
            return r < TimeSpan.Zero ? TimeSpan.Zero : r;
        }
    }
}
