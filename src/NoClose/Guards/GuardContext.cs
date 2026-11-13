using NoClose.Core.Diagnostics;
using NoClose.Core.Settings;
using NoClose.Core.Time;
using NoClose.Engine;
using NoClose.Journal;
using NoClose.Logging;
using NoClose.Platform;

namespace NoClose.Guards;

/// <summary>Everything a guard may use. Guards run on the UI thread.</summary>
internal sealed class GuardContext
{
    public required JournalStore Journal { get; init; }
    public required ILogSink Log { get; init; }
    public required bool IsElevated { get; init; }
    public required IMachineReader Reader { get; init; }
    public required PowerSchemeAccessor Power { get; init; }
    public required RegistryAccessor Registry { get; init; }
    public required ScreenSaverAccessor ScreenSaver { get; init; }
    public required RestoreService Restore { get; init; }
    public required KeepAwakeEngine Engine { get; init; }
    public required IClock Clock { get; init; }
    public required AppSettings Settings { get; init; }

    /// <summary>Handle of the (always created) status window; the end-session guard blocks on it.</summary>
    public IntPtr StatusWindowHandle { get; set; }
    /// <summary>Shows the status window minimized (never hidden) so that the shutdown block reason has a visible owner.</summary>
    public Action EnsureStatusWindowVisibleMinimized { get; set; } = () => { };
    /// <summary>Reason for the current activation.</summary>
    public string Reason { get; set; } = "Long-running task";
    /// <summary>Time left of the current activation, null if open ended.</summary>
    public Func<TimeSpan?> RemainingActivation { get; set; } = () => null;
}
