namespace NoClose.Core.Activation;

public enum ActivationKind { Indefinite, Duration, Until, WhileProcess }

public sealed record ActivationSpec(ActivationKind Kind, TimeSpan? Duration = null,
    DateTime? UntilLocal = null, string? ProcessName = null, int? ProcessId = null)
{
    public static readonly ActivationSpec Indefinite = new(ActivationKind.Indefinite);

    public static ActivationSpec ForDuration(TimeSpan d) => new(ActivationKind.Duration, Duration: d);
    public static ActivationSpec ForUntil(DateTime localTarget) => new(ActivationKind.Until, UntilLocal: localTarget);
    public static ActivationSpec ForProcess(string nameOrPid) => ParseProcess(nameOrPid);

    /// <summary>All digits means a PID, otherwise an image name with an optional ".exe" suffix.</summary>
    public static ActivationSpec ParseProcess(string value)
    {
        var v = value.Trim();
        if (v.Length > 0 && v.All(char.IsAsciiDigit) && int.TryParse(v, out var pid) && pid > 0)
            return new ActivationSpec(ActivationKind.WhileProcess, ProcessId: pid);
        if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v = v[..^4];
        return new ActivationSpec(ActivationKind.WhileProcess, ProcessName: v);
    }

    /// <summary>Human readable description ("indefinitely", "until 18:00", "while build.exe runs").</summary>
    public string Describe()
    {
        return Kind switch
        {
            ActivationKind.Duration when Duration is { } d => "for " + FormatSpan(d),
            ActivationKind.Until when UntilLocal is { } u => "until " + u.ToString(u.Date == DateTime.Today ? "HH:mm" : "yyyy-MM-dd HH:mm"),
            ActivationKind.WhileProcess when ProcessId is { } pid => $"while process {pid} runs",
            ActivationKind.WhileProcess => $"while {ProcessName}.exe runs",
            _ => "indefinitely"
        };
    }

    public static string FormatSpan(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
