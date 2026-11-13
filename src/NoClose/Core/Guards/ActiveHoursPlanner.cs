namespace NoClose.Core.Guards;

/// <summary>Pure planning of the Windows Update "active hours" window.</summary>
public static class ActiveHoursPlanner
{
    public const int DefaultMaxRange = 18;

    public sealed record Window(int StartHour, int EndHour, int LengthHours);

    /// <summary>Start = current hour, End = (start + maxRange) % 24. Range is clamped to 1..18.</summary>
    public static Window Plan(DateTime nowLocal, int maxRange = DefaultMaxRange)
    {
        int len = Math.Clamp(maxRange, 1, 18);
        int start = nowLocal.Hour;
        return new Window(start, (start + len) % 24, len);
    }

    /// <summary>True if the given hour (0..23) lies within [start, end) taking wrap-around into account.</summary>
    public static bool Contains(int startHour, int endHour, int hour)
    {
        if (startHour == endHour) return false;
        return startHour < endHour
            ? hour >= startHour && hour < endHour
            : hour >= startHour || hour < endHour;
    }

    /// <summary>Hours from <paramref name="now"/> until the window ends (0 if outside the window).</summary>
    public static double HoursLeft(int startHour, int endHour, DateTime now)
    {
        if (!Contains(startHour, endHour, now.Hour)) return 0;
        var end = new DateTime(now.Year, now.Month, now.Day, endHour, 0, 0, now.Kind);
        if (end <= now) end = end.AddDays(1);
        return (end - now).TotalHours;
    }
}
