using NoClose.Core.Activation;
using NoClose.Core.Guards;
using NoClose.Core.Text;

namespace NoClose.Core.Settings;

public sealed class GuardSettings
{
    public bool Enabled { get; set; }
    public bool Acknowledged { get; set; }
    public Dictionary<string, bool>? Items { get; set; }
}

public sealed class DefaultActivationSettings
{
    public ActivationKind Kind { get; set; } = ActivationKind.Indefinite;
    public double? DurationMinutes { get; set; }
    public string? ProcessName { get; set; }

    public ActivationSpec ToSpec()
    {
        return Kind switch
        {
            ActivationKind.Duration when DurationMinutes is > 0 => ActivationSpec.ForDuration(TimeSpan.FromMinutes(DurationMinutes.Value)),
            ActivationKind.WhileProcess when !string.IsNullOrWhiteSpace(ProcessName) => ActivationSpec.ParseProcess(ProcessName),
            _ => ActivationSpec.Indefinite
        };
    }
}

public sealed class LogSettings
{
    public int RetainDays { get; set; } = 14;
    public int MaxFileMB { get; set; } = 10;
    public int TickLogMinutes { get; set; } = 15;
}

public sealed class WindowBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string Reason { get; set; } = ReasonText.Default;
    public DefaultActivationSettings DefaultActivation { get; set; } = new();
    public bool StartActiveOnLaunch { get; set; }
    public bool AwayMode { get; set; }
    public int ReassertSeconds { get; set; } = 45;
    public int GuardReapplyMinutes { get; set; } = 5;
    public bool RegisterForRestart { get; set; } = true;
    public bool StartAtLogon { get; set; }
    public bool StartMinimized { get; set; } = true;
    public Dictionary<string, GuardSettings> Guards { get; set; } = new();
    public LogSettings Log { get; set; } = new();
    public WindowBounds? LastWindowBounds { get; set; }
    /// <summary>True while an activation is running; lets a restart after servicing resume the job.</summary>
    public bool LastRunActive { get; set; }
    /// <summary>Command line arguments that reproduce the last activation (used to resume after an application restart).</summary>
    public string[]? LastActivationArgs { get; set; }
    /// <summary>Absolute end of the last activation (Duration/Until), so a restart after servicing resumes with the remaining time only.</summary>
    public DateTime? LastActivationDeadlineUtc { get; set; }

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        s.Normalize();
        return s;
    }

    public GuardSettings GetGuard(GuardId id)
    {
        if (!Guards.TryGetValue(id.ToString(), out var g))
        {
            g = DefaultGuard(id);
            Guards[id.ToString()] = g;
        }
        return g;
    }

    public static GuardSettings DefaultGuard(GuardId id) => id switch
    {
        GuardId.ExecutionState => new GuardSettings { Enabled = true, Acknowledged = true },
        GuardId.PowerPlan => new GuardSettings
        {
            Enabled = false,
            Acknowledged = false,
            Items = PowerPlanItems.All.ToDictionary(i => i.Key, i => i.DefaultOn)
        },
        _ => new GuardSettings { Enabled = false, Acknowledged = false }
    };

    /// <summary>True when the guard is enabled and, if it changes settings, the user has confirmed it once.</summary>
    public bool IsGuardActive(GuardId id, bool changesSettings)
    {
        var g = GetGuard(id);
        return g.Enabled && (!changesSettings || g.Acknowledged);
    }

    /// <summary>Fill missing parts with defaults and clamp values to their valid ranges.</summary>
    public void Normalize()
    {
        if (SchemaVersion < 1) SchemaVersion = 1;
        Reason = ReasonText.Sanitize(Reason);
        DefaultActivation ??= new DefaultActivationSettings();
        Log ??= new LogSettings();
        Guards ??= new Dictionary<string, GuardSettings>();
        ReassertSeconds = Math.Clamp(ReassertSeconds, 15, 300);
        GuardReapplyMinutes = Math.Clamp(GuardReapplyMinutes, 1, 120);
        Log.RetainDays = Math.Clamp(Log.RetainDays, 1, 365);
        Log.MaxFileMB = Math.Clamp(Log.MaxFileMB, 1, 200);
        Log.TickLogMinutes = Math.Clamp(Log.TickLogMinutes, 1, 240);
        foreach (GuardId id in Enum.GetValues<GuardId>())
        {
            if (!Guards.TryGetValue(id.ToString(), out var g) || g == null)
            {
                Guards[id.ToString()] = DefaultGuard(id);
                continue;
            }
            if (id == GuardId.PowerPlan)
            {
                g.Items ??= new Dictionary<string, bool>();
                foreach (var item in PowerPlanItems.All)
                    if (!g.Items.ContainsKey(item.Key)) g.Items[item.Key] = item.DefaultOn;
            }
        }
        if (LastWindowBounds is { Width: < 100 } or { Height: < 100 }) LastWindowBounds = null;
    }
}
