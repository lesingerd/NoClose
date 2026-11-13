namespace NoClose.Core.Time;

/// <summary>Abstraction over time so that pure logic can be tested deterministically.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
    TimeZoneInfo TimeZone { get; }
}

public static class ClockExtensions
{
    /// <summary>Local wall-clock time (Kind = Unspecified) in the clock's time zone.</summary>
    public static DateTime LocalNow(this IClock clock) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, clock.TimeZone), DateTimeKind.Unspecified);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
    public TimeZoneInfo TimeZone => TimeZoneInfo.Local;
}

/// <summary>Allows an identical key only once per interval (used for balloon notifications).</summary>
public sealed class Throttle
{
    private readonly TimeSpan _interval;
    private readonly IClock _clock;
    private readonly Dictionary<string, DateTime> _last = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Throttle(TimeSpan interval, IClock clock)
    {
        _interval = interval;
        _clock = clock;
    }

    public bool TryPass(string key)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            if (_last.TryGetValue(key, out var prev) && now >= prev && now - prev < _interval)
                return false;
            _last[key] = now;
            return true;
        }
    }
}
