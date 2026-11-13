using System.Globalization;
using System.Text.RegularExpressions;

namespace NoClose.Core.Cli;

public static class DurationParser
{
    public static readonly TimeSpan Min = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Max = TimeSpan.FromDays(30);

    private static readonly Regex Units = new(
        @"^(?:(\d+(?:\.\d+)?)d)?(?:(\d+(?:\.\d+)?)h)?(?:(\d+(?:\.\d+)?)m)?(?:(\d+)s)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Clock = new(@"^(\d{1,3}):(\d{2})$", RegexOptions.CultureInvariant);

    /// <summary>Parses 90m, 8h, 1h30m, 2d, 45s, 01:30 (hh:mm), 1.5h. Range 1 minute to 30 days.</summary>
    public static bool TryParse(string? text, out TimeSpan value, out string error)
    {
        value = TimeSpan.Zero;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "Duration is empty."; return false; }
        var s = text.Trim();

        TimeSpan result;
        var cm = Clock.Match(s);
        if (cm.Success)
        {
            int h = int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture);
            int m = int.Parse(cm.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m > 59) { error = $"Invalid minutes in '{s}'."; return false; }
            result = new TimeSpan(h, m, 0);
        }
        else
        {
            var um = Units.Match(s);
            if (!um.Success || !um.Groups.Cast<Group>().Skip(1).Any(g => g.Success))
            {
                error = $"Cannot parse duration '{s}'. Examples: 90m, 8h, 1h30m, 2d, 01:30, 1.5h.";
                return false;
            }
            double days = Num(um.Groups[1]), hours = Num(um.Groups[2]), mins = Num(um.Groups[3]), secs = Num(um.Groups[4]);
            double totalSeconds = days * 86400 + hours * 3600 + mins * 60 + secs;
            if (totalSeconds > Max.TotalSeconds * 2) { error = "Duration is too long (maximum 30 days)."; return false; }
            result = TimeSpan.FromSeconds(Math.Round(totalSeconds));
        }

        if (result < Min) { error = "Duration must be at least 1 minute."; return false; }
        if (result > Max) { error = "Duration must be at most 30 days."; return false; }
        value = result;
        return true;
    }

    private static double Num(Group g) =>
        g.Success ? double.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
}

public static class UntilParser
{
    /// <summary>
    /// "HH:mm" (24 h local; today, or tomorrow if that time has passed) or ISO "yyyy-MM-ddTHH:mm" (must be in the future,
    /// at most 30 days away). If a <paramref name="tz"/> is supplied, wall-clock times that do not exist (DST gap) are
    /// moved forward one hour.
    /// </summary>
    public static bool TryParse(string? text, DateTime nowLocal, out DateTime targetLocal, TimeZoneInfo? tz = null)
    {
        targetLocal = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        DateTime t;
        if (DateTime.TryParseExact(s, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var hm)
            || DateTime.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out hm))
        {
            t = nowLocal.Date + hm.TimeOfDay;
            if (t <= nowLocal) t = t.AddDays(1);
        }
        else if (DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            t = iso;
            if (t <= nowLocal) return false;
        }
        else return false;

        t = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
        if (tz != null && tz.IsInvalidTime(t)) t = t.AddHours(1);
        if (t - nowLocal > DurationParser.Max) return false;
        targetLocal = t;
        return true;
    }
}
