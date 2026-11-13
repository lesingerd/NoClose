using System.Diagnostics;
using Microsoft.Win32;
using NoClose.Core.Activation;
using NoClose.Core.Cli;
using NoClose.Core.Diagnostics;
using NoClose.Core.Guards;
using NoClose.Core.Settings;
using NoClose.Core.Text;
using NoClose.Core.Time;
using NoClose.Engine;
using NoClose.Guards;
using NoClose.Journal;
using NoClose.Logging;
using NoClose.Platform;
using NoClose.Platform.Interop;
using NoClose.Ui;

namespace NoClose;

/// <summary>Owns and wires every long-lived component (see DESIGN.md section 14).</summary>
internal sealed class AppHost
{
    public static AppHost? Current { get; private set; }

    private readonly SettingsStore _settingsStore = new();
    private readonly object _stateGate = new();
    private SynchronizationContext? _ui;
    private int _emergencyDone;
    private int _shutdownDone;
    private int _diagRunning;
    private int _diagSecondsLeft = 60;
    private readonly TaskCompletionSource _startupDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _lastDiagTriggerUtc = DateTime.MinValue;
    private bool _deactivating;
    private ProcessWatcher? _watcher;
    private PipeServer? _pipe;
    private System.Windows.Forms.Timer? _uiTimer;
    private System.Windows.Forms.Timer? _settingChangeTimer;
    private volatile IReadOnlyList<DiagnosticFinding> _findings = Array.Empty<DiagnosticFinding>();
    private HashSet<string> _knownHighFindings = new();
    private readonly Dictionary<GuardId, GuardStatus> _lastGuardStatus = new();
    private EngineState _lastEngineState = EngineState.Idle;
    private string _reason = ReasonText.Default;
    private CliCommand _startCommand = CliCommand.Empty;

    public AppHost()
    {
        Current = this;
        IsElevated = Elevation.IsElevated;
        CanRestartElevated = Elevation.CanRestartElevated;
        Settings = _settingsStore.Load(out var settingsWarning);
        Log = new TimelineLog(settings: Settings.Log);
        var previous = Log.LastLineOfPreviousRun();
        Clock = SystemClock.Instance;
        Policy = new ActivationPolicy(Clock);

        Power = new PowerSchemeAccessor();
        Registry = new RegistryAccessor();
        ScreenSaver = new ScreenSaverAccessor();
        Reader = new WindowsMachineReader(Power, IsElevated);
        Journal = JournalStore.ForCurrentProcess(IsElevated, Log);
        Restore = new RestoreService(Registry, Power, ScreenSaver, Log);
        Startup = new StartupTask(Log);

        Engine = new KeepAwakeEngine(Log, Reader, () => Settings.ReassertSeconds, () => Settings.Log.TickLogMinutes,
            EnvironmentCheck, OnEngineTick);
        Engine.StatusChanged += OnEngineStatus;

        var ctx = new GuardContext
        {
            Journal = Journal, Log = Log, IsElevated = IsElevated, Reader = Reader, Power = Power, Registry = Registry,
            ScreenSaver = ScreenSaver, Restore = Restore, Engine = Engine, Clock = Clock, Settings = Settings,
            RemainingActivation = () => Policy.Remaining(DateTime.UtcNow),
        };
        GuardContext = ctx;
        Guards = new GuardManager(ctx);
        Guards.Changed += OnGuardsChanged;
        SilentMedia.SessionChanged += () => PostUi(() =>
        {
            SilentMedia.Sync();
            Guards.NotifyChanged();
        });

        LogStartBanner(previous, settingsWarning);
    }

    // ---- public state ---------------------------------------------------------------------------------------

    public bool IsElevated { get; }
    /// <summary>True for an administrator with a filtered token: the only case where "Restart elevated" makes sense.</summary>
    public bool CanRestartElevated { get; }
    public OverallState Overall
    {
        get
        {
            var s = Engine.Status.State;
            return StatusSummary.Compute(Policy.IsRunning, s == EngineState.Idle, s == EngineState.Degraded);
        }
    }
    public AppSettings Settings { get; }
    public TimelineLog Log { get; }
    public IClock Clock { get; }
    public ActivationPolicy Policy { get; }
    public GuardManager Guards { get; }
    public GuardContext GuardContext { get; }
    public KeepAwakeEngine Engine { get; }
    public JournalStore Journal { get; }
    public RestoreService Restore { get; }
    public StartupTask Startup { get; }
    public PowerSchemeAccessor Power { get; }
    public RegistryAccessor Registry { get; }
    public ScreenSaverAccessor ScreenSaver { get; }
    public WindowsMachineReader Reader { get; }
    public TrayController? Tray { get; private set; }
    public StatusForm? StatusWindow { get; private set; }
    public ApplicationContext ApplicationContext => Tray ?? throw new InvalidOperationException("Start() has not been called.");
    public bool Exiting { get; private set; }
    public string? Banner { get; private set; }
    public IReadOnlyList<DiagnosticFinding> Findings => _findings;
    public EndSessionGuard EndSession => Guards.Get<EndSessionGuard>();
    public SilentMediaGuard SilentMedia => Guards.Get<SilentMediaGuard>();
    public bool IsActive => Policy.IsRunning;
    public string Reason => _reason;
    public EngineStatus EngineStatus => Engine.Status;

    public event Action? ViewChanged;

    public bool HasHighFinding => _findings.Any(f => f.Severity == Severity.High);

    // ---- startup / shutdown ---------------------------------------------------------------------------------

    private void LogStartBanner(string? previous, string? settingsWarning)
    {
        var v = typeof(AppHost).Assembly.GetName().Version;
        var s = Reader.Session;
        Log.Info(LogCategory.App,
            $"NoClose {v} started: elevated={IsElevated}, session={s.Describe()}, OS={Environment.OSVersion.Version}, AoAc={Reader.Caps.AoAc}, power={(Reader.OnAcPower ? "AC" : "DC")}");
        if (previous != null) Log.Info(LogCategory.App, "Previous run ended: " + previous);
        if (settingsWarning != null) Log.Warn(LogCategory.App, settingsWarning);
    }

    public void Start(CliCommand cmd)
    {
        _startCommand = cmd;
        _reason = ReasonText.Sanitize(cmd.Reason ?? Settings.Reason);

        // 1. command pipe first: a second invocation during the (possibly slow) stale journal restore queues its command until the
        //    startup has finished instead of failing with exit code 3.
        _pipe = new PipeServer(HandlePipeAsync, m => Log.Warn(LogCategory.Cli, m));
        _pipe.Start();

        // 2. stale journal check and restore, before anything else
        RecoverStaleJournals();

        // 3. UI
        StatusWindow = new StatusForm(this);
        _ = StatusWindow.Handle; // force handle creation: it hosts session/power notifications and the shutdown block reason
        _ui = SynchronizationContext.Current;
        GuardContext.StatusWindowHandle = StatusWindow.Handle;
        GuardContext.EnsureStatusWindowVisibleMinimized = () => StatusWindow?.EnsureVisibleMinimized();
        Tray = new TrayController(this);
        SystemEvents.SessionEnding += OnSystemEventsSessionEnding;

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => OnUiSecond();
        _uiTimer.Start();

        if (cmd.Minimized || Settings.StartMinimized) StatusWindow.ShowMinimizedFirst();
        else StatusWindow.Show();

        // 4. engine thread (idle)
        Engine.Begin();

        // 5. diagnostics in the background
        _ = RefreshDiagnosticsAsync(first: true);

        // 6. automatic activation
        TryStartInitialActivation(cmd);

        // 7. queued pipe commands may run now
        _startupDone.TrySetResult();
        RaiseViewChanged();

        // 8. elevation on demand: an admin-only guard the user opted in to needs an elevated instance
        if (!IsElevated && CanRestartElevated && !cmd.Handoff && WantsAdminGuard(cmd))
        {
            Log.Info(LogCategory.App, "An administrator-only guard is enabled; asking for elevation (UAC)");
            PostUi(RestartElevated);
        }
    }

    private bool WantsAdminGuard(CliCommand cmd)
    {
        foreach (var g in Guards.Guards.Where(g => g.IsAdminRequired))
        {
            var s = Settings.GetGuard(g.Id);
            bool wanted = cmd.GuardOverride != null ? cmd.GuardOverride.Contains(g.Id) : s.Enabled;
            if (wanted && (!g.ChangesSettings || s.Acknowledged)) return true;
        }
        return false;
    }

    private void TryStartInitialActivation(CliCommand cmd)
    {
        if (cmd.Verb == CliVerb.Start)
        {
            if (!Activate(cmd.Activation ?? ActivationSpec.Indefinite, cmd.Reason, cmd.GuardOverride, out var msg))
                Log.Warn(LogCategory.Cli, "Start from command line failed: " + msg);
            return;
        }
        if (cmd.RestoredAfterRestart && Settings.LastRunActive)
        {
            Log.Info(LogCategory.App, "Restarted by Windows after servicing; resuming the previous activation");
            var decision = ResumeDecision.Decide(Settings.LastActivationArgs, Settings.LastActivationDeadlineUtc, DateTime.UtcNow, Clock.LocalNow(), Clock.TimeZone);
            switch (decision.Outcome)
            {
                case ResumeOutcome.Resume:
                    Activate(decision.Spec!, decision.Reason, null, out _);
                    break;
                case ResumeOutcome.NoSavedState:
                    Activate(Settings.DefaultActivation.ToSpec(), null, null, out _);
                    break;
                default:
                    // Saved arguments that are unreadable or expired must never become an indefinite activation.
                    Log.Warn(LogCategory.App, decision.Message);
                    Settings.LastRunActive = false;
                    Settings.LastActivationArgs = null;
                    Settings.LastActivationDeadlineUtc = null;
                    SaveSettings();
                    Tray?.Notify.Show("NoClose did not resume", decision.Message, ToolTipIcon.Warning, throttle: false);
                    break;
            }
            return;
        }
        if (Settings.StartActiveOnLaunch)
            Activate(Settings.DefaultActivation.ToSpec(), null, null, out _);
    }

    private void RecoverStaleJournals()
    {
        var paths = new[] { JournalStore.DefaultPath(true), JournalStore.DefaultPath(false) };
        // Not just File.Exists: it reports false for a journal we are not allowed to see (written by an elevated run), which would
        // silently skip the whole recovery and never offer "Restart elevated".
        if (paths.Any(p => File.Exists(p) || File.Exists(p + ".tmp") || JournalStore.ExistsButDenied(p))) RecoverJournalsAt(paths);

        if (IsElevated)
        {
            var notes = Restore.CleanupOrphanSchemes(paths, Journal);
            if (notes.Count > 0) Banner = (Banner == null ? "" : Banner + Environment.NewLine) + string.Join(Environment.NewLine, notes);
        }
    }

    private void RecoverJournalsAt(string[] paths)
    {
        try
        {
            var summary = Restore.RestoreFromDisk(paths);
            if (summary.Restored > 0 || summary.Failed > 0 || summary.NeedsElevation)
            {
                Log.Warn(LogCategory.App, "Stale journal processed: " + summary.ToText().Replace(Environment.NewLine, " | "));
                _pendingStaleBalloon = summary;
            }
            if (summary.NeedsElevation && !IsElevated)
                Banner = CanRestartElevated
                    ? "Settings from a previous elevated session need restoring — Restart elevated."
                    : "Settings from a previous elevated session need restoring — sign in with an administrator account and run NoClose --restore.";
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.App, "Stale journal recovery failed: " + ex);
        }
        finally
        {
            // Anything that stayed behind (failures / needs elevation) in our own journal path is adopted so the live store never
            // overwrites it; this also runs when the restore above threw.
            try
            {
                var own = JournalStore.TryLoad(Journal.Path, out _);
                if (own != null && own.Entries.Any(e => e.Status != Core.Journal.JournalEntryStatus.Restored))
                {
                    var p = Process.GetCurrentProcess();
                    own.ProcessId = p.Id;
                    own.ProcessStartUtc = p.StartTime.ToUniversalTime();
                    Journal.Adopt(own);
                }
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.App, "Adopting the remaining journal entries failed: " + ex.Message);
            }
        }
    }

    private RestoreSummary? _pendingStaleBalloon;

    /// <summary>Normal exit path (after the message loop ended). Idempotent.</summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownDone, 1) != 0) return;
        Exiting = true;
        try
        {
            _uiTimer?.Stop();
            _settingChangeTimer?.Stop();
            _pipe?.Dispose();
            Deactivate("NoClose exited", silent: true);
            Guards.RestoreAll();
            Engine.Shutdown(TimeSpan.FromSeconds(2));
            Journal.DeleteIfEmpty();
            SystemEvents.SessionEnding -= OnSystemEventsSessionEnding;
            SaveWindowBounds();
            SaveSettings();
            if (Tray != null) Tray.DisposeIcon();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.App, "Shutdown error: " + ex);
        }
        finally
        {
            Log.Info(LogCategory.App, "NoClose exited");
            Log.Dispose();
        }
    }

    /// <summary>
    /// Synchronous, journal driven, best effort, idempotent, each action isolated; total budget 4 s. Used for session end and
    /// unhandled exceptions. (The elevation hand-off uses the normal Deactivate / guard restore path, not this one.)
    /// </summary>
    public void EmergencyRestore()
    {
        if (Interlocked.Exchange(ref _emergencyDone, 1) != 0) return;
        var sw = Stopwatch.StartNew();
        try { Log.Info(LogCategory.App, "Emergency restore started"); } catch { }
        try { Engine.Shutdown(TimeSpan.FromMilliseconds(1000)); } catch { }
        try { EndSession.Restore(GuardContext); } catch { }
        try
        {
            var left = TimeSpan.FromSeconds(4) - sw.Elapsed;
            var summary = Restore.RestoreStore(Journal, null, left > TimeSpan.FromMilliseconds(500) ? left : TimeSpan.FromMilliseconds(500));
            Log.Info(LogCategory.App, "Emergency restore finished: " + summary.ToText().Replace(Environment.NewLine, " | "));
        }
        catch (Exception ex)
        {
            try { Log.Error(LogCategory.App, "Emergency restore failed: " + ex.Message); } catch { }
        }
        try { Log.FlushNow(); } catch { }
    }

    public void Exit()
    {
        Exiting = true;
        PostUi(() => Tray?.ExitThread());
    }

    // ---- UI thread marshalling --------------------------------------------------------------------------------

    public void PostUi(Action action)
    {
        var ui = _ui;
        if (ui == null) { action(); return; }
        ui.Post(_ =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error(LogCategory.App, "UI action failed: " + ex); }
        }, null);
    }

    public void RaiseViewChanged() => ViewChanged?.Invoke();

    // ---- activation -----------------------------------------------------------------------------------------

    public bool Activate(ActivationSpec spec, string? reason, IReadOnlyList<GuardId>? guardOverride, out string message)
    {
        if (spec.Kind == ActivationKind.WhileProcess && !ProcessWatcher.IsRunning(spec))
        {
            message = $"The process {(spec.ProcessId?.ToString() ?? spec.ProcessName + ".exe")} is not running; NoClose did not start.";
            Log.Warn(LogCategory.App, message);
            Tray?.Notify.Show("NoClose did not start", message, ToolTipIcon.Warning, throttle: false);
            return false;
        }
        if (Policy.IsRunning) Deactivate("restarted with new settings", silent: true);

        _reason = ReasonText.Sanitize(reason ?? _reason);
        if (Settings.Reason != _reason) { Settings.Reason = _reason; }
        GuardContext.Reason = _reason;

        Log.Info(LogCategory.App, $"Activation starting: {spec.Describe()}, reason=\"{_reason}\"");
        Guards.SetOverride(guardOverride);
        Guards.ApplyAll();
        Policy.Begin(spec);

        if (spec.Kind == ActivationKind.WhileProcess)
        {
            // Callbacks carry the watcher that produced them: observations queued by a replaced watcher must not count as misses
            // of the new activation.
            ProcessWatcher? watcher = null;
            watcher = new ProcessWatcher(spec, alive => PostUi(() =>
            {
                if (!ReferenceEquals(_watcher, watcher)) return;
                OnProcessPoll(alive);
            }));
            _watcher = watcher;
            watcher.Start();
        }

        Settings.LastRunActive = true;
        Settings.LastActivationArgs = ActivationArgs.FromSpec(spec, _reason).ToArray();
        Settings.LastActivationDeadlineUtc = Policy.DeadlineUtc;
        SaveSettings();
        message = $"NoClose is active {spec.Describe()}.";
        Log.Info(LogCategory.App, message);
        Tray?.Notify.Show("NoClose started", message, ToolTipIcon.Info, throttle: false);
        RaiseViewChanged();
        return true;
    }

    public void Deactivate(string why, bool silent = false)
    {
        if (_deactivating) return;
        if (!Policy.IsRunning && !Guards.Running) return;
        _deactivating = true;
        try
        {
            Policy.End();
            _watcher?.Dispose();
            _watcher = null;
            Guards.RestoreAll();
            Journal.DeleteIfEmpty();
            Settings.LastRunActive = false;
            Settings.LastActivationArgs = null;
            Settings.LastActivationDeadlineUtc = null;
            SaveSettings();
            Log.Info(LogCategory.App, "Activation ended: " + why);
            if (!silent) Tray?.Notify.Show("NoClose stopped", "NoClose stopped: " + why, ToolTipIcon.Info, throttle: false);
        }
        finally
        {
            _deactivating = false;
            RaiseViewChanged();
        }
    }

    private void OnProcessPoll(bool alive)
    {
        if (!Policy.IsRunning) return;
        var d = Policy.ShouldStop(DateTime.UtcNow, alive);
        if (d.Stop) Deactivate(d.Reason);
    }

    private void CheckDeadline()
    {
        if (!Policy.IsRunning) return;
        var d = Policy.CheckDeadline(DateTime.UtcNow);
        if (d.Stop) Deactivate(d.Reason);
    }

    /// <summary>Engine thread tick: schedule the deadline check and the guard reapply on the UI thread.</summary>
    private void OnEngineTick(DateTime nowUtc) => PostUi(() =>
    {
        CheckDeadline();
        Guards.ReapplyDue();
    });

    private void OnUiSecond()
    {
        CheckDeadline();
        Tray?.UpdateTooltip();
        StatusWindow?.UpdateCountdown();
        StatusWindow?.RetryRegistrations();
        // Periodic diagnostics: a pending restart or a new High finding must show up mid-job (15 min while active, hourly while idle).
        if (--_diagSecondsLeft <= 0)
        {
            _diagSecondsLeft = (int)(Policy.IsRunning ? DiagIntervalActive : DiagIntervalIdle).TotalSeconds;
            _ = RefreshDiagnosticsAsync();
        }
    }

    private static readonly TimeSpan DiagIntervalActive = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DiagIntervalIdle = TimeSpan.FromHours(1);

    /// <summary>Extra refresh after resume / unlock / settings change; throttled so event bursts cause one run.</summary>
    private void TriggerDiagnostics(string why)
    {
        if (DateTime.UtcNow - _lastDiagTriggerUtc < TimeSpan.FromSeconds(30)) return;
        _lastDiagTriggerUtc = DateTime.UtcNow;
        Log.Debug(LogCategory.Diag, "Diagnostics refresh: " + why);
        _ = RefreshDiagnosticsAsync();
    }

    // ---- engine / guard events ----------------------------------------------------------------------------------

    private string? EnvironmentCheck()
    {
        var texts = new List<string>();
        // Only the settings of the CURRENT power source matter: on AC a DC-only "ignore requests" value does not degrade the engine.
        bool onAc = Reader.OnAcPower;
        foreach (var rule in DiagnosticsEngine.CreateRules().Where(r => r.Id is "R-OVR" or "R-ALLOW"))
            foreach (var f in PowerSourceFilter.For(rule.Evaluate(Reader).Where(f => f.Severity == Severity.High), onAc))
                texts.Add(f.Title);
        return texts.Count == 0 ? null : string.Join("; ", texts.Distinct());
    }

    private void OnEngineStatus(EngineStatus s)
    {
        PostUi(() =>
        {
            if (s.State == EngineState.Degraded && _lastEngineState != EngineState.Degraded)
                Tray?.Notify.Show("NoClose may not keep the session awake", s.DegradedReason ?? "The keep-awake engine is degraded.", ToolTipIcon.Warning);
            _lastEngineState = s.State;
            Tray?.Refresh();
            RaiseViewChanged();
        });
    }

    private void OnGuardsChanged()
    {
        foreach (var g in Guards.Guards)
        {
            _lastGuardStatus.TryGetValue(g.Id, out var prev);
            if (g.Status != prev)
            {
                if (g.Status is GuardStatus.Reverted or GuardStatus.Error)
                    Tray?.Notify.Show("NoClose: " + g.DisplayName, g.Message, ToolTipIcon.Warning);
                _lastGuardStatus[g.Id] = g.Status;
            }
        }
        RaiseViewChanged();
    }

    // ---- diagnostics ------------------------------------------------------------------------------------------

    public EngineSnapshot CurrentEngineSnapshot()
    {
        var s = Engine.Status;
        return new EngineSnapshot(s.IsActive, s.State == EngineState.Degraded, s.DegradedReason);
    }

    public async Task<(IReadOnlyList<DiagnosticFinding> Findings, string Text)> RunDiagnosticsAsync()
    {
        var snapshot = CurrentEngineSnapshot();
        var media = SilentMedia.Snapshot(Guards.Running && SilentMedia.Wanted);
        return await Task.Run(() =>
        {
            var findings = DiagnosticsEngine.Run(Reader, snapshot, media);
            var text = DiagnosticsTextFormatter.Format(findings, MachineSummary.From(Reader, IsElevated, DateTimeOffset.Now));
            return ((IReadOnlyList<DiagnosticFinding>)findings, text);
        });
    }

    public async Task RefreshDiagnosticsAsync(bool first = false)
    {
        if (Interlocked.Exchange(ref _diagRunning, 1) != 0) return; // a run is already in flight
        try
        {
            var (findings, _) = await RunDiagnosticsAsync().ConfigureAwait(false);
            PostUi(() =>
            {
                var high = findings.Where(f => f.Severity == Severity.High).Select(f => f.RuleId + ": " + f.Title).ToHashSet();
                bool changed = !high.SetEquals(_knownHighFindings);
                if (first || changed)
                    foreach (var f in findings.Where(f => f.Severity == Severity.High))
                        Log.Warn(LogCategory.Diag, $"{f.RuleId} [{f.Severity}] {f.Title} - {f.Detail.Replace(Environment.NewLine, " ")}");
                foreach (var h in high.Except(_knownHighFindings))
                    Tray?.Notify.Show("NoClose diagnostics", h, ToolTipIcon.Warning);
                _knownHighFindings = high;
                _findings = findings;
                if (_pendingStaleBalloon is { } stale)
                {
                    _pendingStaleBalloon = null;
                    Tray?.Notify.Show("NoClose restored settings", stale.ToText().Split(Environment.NewLine)[0], ToolTipIcon.Info, throttle: false);
                }
                Tray?.Refresh();
                RaiseViewChanged();
            });
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Diag, "Diagnostics failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _diagRunning, 0);
        }
    }

    // ---- window messages (called by StatusForm) -------------------------------------------------------------

    public void OnSessionChange(int code, int sessionId)
    {
        Log.Info(LogCategory.Session, $"{NativeConstants.WtsEventName(code)} (session {sessionId})");
        switch (code)
        {
            case 8: // unlock
                Engine.Reassert("session unlock");
                TriggerDiagnostics("session unlock");
                if (Guards.Running)
                {
                    Guards.Get<ScreenSaverGuard>().RetryIfPending(GuardContext);
                    Guards.ReapplyNow();
                }
                break;
            case 1: // console connect
            case 3: // remote connect
                Engine.Reassert(NativeConstants.WtsEventName(code));
                break;
        }
    }

    public void OnPowerBroadcast(int pbt, IntPtr lParam)
    {
        switch (pbt)
        {
            case NativeConstants.PBT_APMSUSPEND: Log.Info(LogCategory.Power, "PBT_APMSUSPEND"); break;
            case NativeConstants.PBT_APMRESUMESUSPEND:
                Log.Info(LogCategory.Power, "PBT_APMRESUMESUSPEND");
                Engine.Reassert("resume (suspend)");
                TriggerDiagnostics("resume");
                if (Guards.Running) Guards.ReapplyNow();
                break;
            case NativeConstants.PBT_APMRESUMEAUTOMATIC:
                Log.Info(LogCategory.Power, "PBT_APMRESUMEAUTOMATIC");
                Engine.Reassert("resume (automatic)");
                TriggerDiagnostics("resume");
                if (Guards.Running) Guards.ReapplyNow();
                break;
            case NativeConstants.PBT_APMPOWERSTATUSCHANGE:
                Log.Info(LogCategory.Power, "PBT_APMPOWERSTATUSCHANGE (AC/DC or battery level)");
                Engine.Reassert("power status change");
                break;
            case NativeConstants.PBT_POWERSETTINGCHANGE:
                HandlePowerSetting(lParam);
                break;
            default:
                Log.Debug(LogCategory.Power, $"WM_POWERBROADCAST 0x{pbt:X}");
                break;
        }
    }

    private void HandlePowerSetting(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;
        var g = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(lParam);
        var len = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 16);
        int v = len >= 4 ? System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 20) : 0;

        if (g == NativeConstants.GUID_CONSOLE_DISPLAY_STATE || g == NativeConstants.GUID_SESSION_DISPLAY_STATUS)
        {
            var name = v switch { 0 => "Off", 1 => "On", 2 => "Dimmed", _ => "state " + v };
            Log.Info(LogCategory.Display, $"{(g == NativeConstants.GUID_CONSOLE_DISPLAY_STATE ? "Console display" : "Session display")}: {name}");
            if (v == 1) Engine.Reassert("display on");
        }
        else if (g == NativeConstants.GUID_SESSION_USER_PRESENCE)
            Log.Info(LogCategory.Presence, "User presence: " + (v switch { 0 => "Present", 2 => "Inactive", _ => "state " + v }));
        else if (g == NativeConstants.GUID_ACDC_POWER_SOURCE)
        {
            Log.Info(LogCategory.Power, "Power source: " + (v switch { 0 => "AC", 1 => "DC (battery)", 2 => "short term (UPS)", _ => "state " + v }));
            Engine.Reassert("AC/DC change");
        }
        else if (g == NativeConstants.GUID_SYSTEM_AWAYMODE)
            Log.Info(LogCategory.Power, "Away mode: " + (v == 1 ? "entering" : "exiting"));
        else if (g == NativeConstants.GUID_LIDSWITCH_STATE_CHANGE)
            Log.Info(LogCategory.Power, "Lid: " + (v == 0 ? "closed" : "open"));
    }

    public void OnSettingChange(string? area)
    {
        Log.Debug(LogCategory.Session, "WM_SETTINGCHANGE " + (area ?? ""));
        if (!Guards.Running) return;
        TriggerDiagnostics("settings change");
        // debounce: schedule one guard reapply 2 s after the last change broadcast
        if (_settingChangeTimer == null)
        {
            _settingChangeTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _settingChangeTimer.Tick += (_, _) =>
            {
                _settingChangeTimer!.Stop();
                Engine.Reassert("settings change");
                Guards.ReapplyNow();
            };
        }
        _settingChangeTimer.Stop();
        _settingChangeTimer.Start();
    }

    /// <returns>true to allow the session to end, false to ask Windows to wait.</returns>
    public bool OnQueryEndSession(long flags)
    {
        bool applied = EndSession.IsApplied;
        bool block = EndSessionDecision.ShouldBlock(applied, flags);
        Log.Info(LogCategory.EndSession,
            $"WM_QUERYENDSESSION flags={EndSessionDecision.DescribeFlags(flags)}; guard applied={applied}; decision={(block ? "ask Windows to wait" : "allow")}");
        return !block;
    }

    public void OnEndSession(bool ending, long flags)
    {
        if (ending)
        {
            Log.Info(LogCategory.EndSession, $"Session ending (flags {EndSessionDecision.DescribeFlags(flags)}); restoring");
            EmergencyRestore();
            // The engine thread, the power requests and the guards are gone for good now. Mark the activation ended so the UI can never
            // show "Active" over nothing, but keep Settings.LastRunActive/LastActivationArgs so a Restart Manager restart can resume.
            try
            {
                Exiting = true;
                Policy.End();
                _watcher?.Dispose();
                _watcher = null;
                Log.Info(LogCategory.EndSession, "Activation marked ended; exiting the process (resume data kept)");
                Log.FlushNow();
            }
            catch { }
            // WM_ENDSESSION does not terminate a process that was only asked to close for servicing (ENDSESSION_CLOSEAPP, Restart Manager);
            // without this it would linger without any keep-awake and the registered restart would never happen.
            Environment.Exit(0);
        }
        else Log.Info(LogCategory.EndSession, "End session cancelled");
    }

    private void OnSystemEventsSessionEnding(object? sender, SessionEndingEventArgs e) =>
        Log.Info(LogCategory.EndSession, "SystemEvents.SessionEnding: " + e.Reason);

    public void LogWndProc(LogCategory c, string text) => Log.Info(c, text);

    // ---- command pipe -------------------------------------------------------------------------------------------

    private async Task<PipeResponse> HandlePipeAsync(PipeRequest req)
    {
        // Commands that arrive while Start() is still running (stale journal restore, first activation) wait for it to finish.
        await _startupDone.Task.ConfigureAwait(false);
        var tcs = new TaskCompletionSource<PipeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostUi(() =>
        {
            try { tcs.TrySetResult(ExecuteForwarded(req)); }
            catch (Exception ex) { tcs.TrySetResult(new PipeResponse(false, ex.Message)); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private PipeResponse ExecuteForwarded(PipeRequest req)
    {
        var parse = CliParser.Parse(req.Args, Clock.LocalNow(), Clock.TimeZone);
        if (!parse.Ok) return new PipeResponse(false, parse.Error ?? "Invalid arguments.");
        var cmd = parse.Command!;
        Log.Info(LogCategory.Cli, $"Forwarded command: {req.Verb} {CliParser.Quote(req.Args)}");
        switch (cmd.Verb)
        {
            case CliVerb.Start:
                {
                    bool ok = Activate(cmd.Activation ?? ActivationSpec.Indefinite, cmd.Reason, cmd.GuardOverride, out var msg);
                    return new PipeResponse(ok, msg);
                }
            case CliVerb.Stop:
                Deactivate("stopped by --stop");
                return new PipeResponse(true, "NoClose stopped; all guards restored.");
            case CliVerb.Restore:
                {
                    // A running activation is NOT stopped by --restore, and the live journal (this process owns it, its store holds the
                    // in-memory state) is never restored through a second store: only journals of other, dead runs are processed.
                    var others = new[] { JournalStore.DefaultPath(true), JournalStore.DefaultPath(false) }
                        .Where(p => !string.Equals(p, Journal.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var s = Restore.RestoreFromDisk(others);
                    var text = s.ToText() + (IsActive ? Environment.NewLine + "The running activation was left untouched; its own settings are restored when it stops." : "");
                    return new PipeResponse(!s.AnyFailure, text);
                }
            default:
                if (cmd.Minimized) StatusWindow?.EnsureVisibleMinimized(); else ShowStatusWindow();
                return new PipeResponse(true, IsActive ? $"NoClose is active {Policy.Spec.Describe()}." : "NoClose is idle.");
        }
    }

    // ---- windows / commands used by the UI ----------------------------------------------------------------

    public void ShowStatusWindow() => StatusWindow?.BringToFrontRestored();

    public void ToggleStatusWindow()
    {
        var f = StatusWindow;
        if (f == null) return;
        if (f.Visible && f.WindowState != FormWindowState.Minimized) f.CloseToTray();
        else f.BringToFrontRestored();
    }

    private DiagnosticsForm? _diagForm;

    public void ShowDiagnostics()
    {
        if (_diagForm == null || _diagForm.IsDisposed)
        {
            _diagForm = new DiagnosticsForm(this);
            _diagForm.Show();
        }
        else
        {
            _diagForm.Activate();
            _diagForm.WindowState = FormWindowState.Normal;
            _ = _diagForm.RefreshAsync();
        }
    }

    public void OpenLogFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Log.Directory + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn(LogCategory.App, "Cannot open log folder: " + ex.Message); }
    }

    public void SaveSettings() => _settingsStore.Save(Settings);

    private void SaveWindowBounds()
    {
        var f = StatusWindow;
        if (f == null || f.IsDisposed) return;
        var b = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
        Settings.LastWindowBounds = new WindowBounds { X = b.X, Y = b.Y, Width = b.Width, Height = b.Height };
    }

    /// <summary>
    /// Order: (1) restore all guards, (2) close the pipe, (3) release the mutex, (4) start the elevated copy.
    /// If the user cancels UAC the guards are re-applied and the pipe/mutex re-acquired.
    /// </summary>
    public void RestartElevated()
    {
        if (IsElevated) return;
        if (!CanRestartElevated)
        {
            // A standard user would get the over-the-shoulder credential prompt and the elevated copy would run as ANOTHER account
            // (different HKCU, journal, pipe and settings). Refuse instead.
            const string text = "Administrator-only guards require an administrator account. Sign in with an administrator account and start NoClose there.";
            Log.Warn(LogCategory.App, text);
            Tray?.Notify.Show("NoClose cannot elevate", text, ToolTipIcon.Warning, throttle: false);
            return;
        }
        var spec = Policy.Spec;
        var reason = _reason;
        bool wasActive = IsActive;
        var remaining = Policy.Remaining(DateTime.UtcNow);
        var guardOverride = Guards.Running ? Guards.Guards.Where(g => g.Status == GuardStatus.Applied || (g.Id == GuardId.SilentMedia && g.Status == GuardStatus.Degraded)).Select(g => g.Id).ToList() : null;
        // Composed from the CURRENT state, never from the launch arguments (those may hold mode switches that clash, or a stale duration).
        var args = ActivationArgs.ForElevatedCopy(_startCommand.Minimized || Settings.StartMinimized, wasActive, spec, reason, remaining,
            guardOverride, Elevation.CurrentUserSid);

        Log.Info(LogCategory.App, "Restarting elevated");
        if (wasActive) Deactivate("restarting elevated", silent: true);
        _pipe?.Dispose();
        _pipe = null;
        Program.ReleaseMutex();
        bool started;
        try { started = Elevation.TryStartElevated(args); }
        catch (Exception ex)
        {
            Log.Error(LogCategory.App, "Elevated start failed: " + ex.Message);
            started = false;
        }
        if (started) { Exit(); return; }

        Log.Info(LogCategory.App, "Elevation cancelled; continuing without administrator rights");
        Program.ReacquireMutex();
        _pipe = new PipeServer(HandlePipeAsync, m => Log.Warn(LogCategory.Cli, m));
        _pipe.Start();
        if (wasActive)
            Activate(spec.Kind == ActivationKind.Duration && remaining is { } left ? ActivationSpec.ForDuration(left) : spec, reason, guardOverride, out _);
        RaiseViewChanged();
    }

    // ---- standalone restore (--restore without a running instance) ---------------------------------------------

    public static int RunStandaloneRestore(string? outPath)
    {
        using var log = new TimelineLog();
        var elevated = Elevation.IsElevated;
        var power = new PowerSchemeAccessor();
        var svc = new RestoreService(new RegistryAccessor(), power, new ScreenSaverAccessor(), log);
        log.Info(LogCategory.Cli, $"--restore (elevated={elevated})");
        RestoreSummary summary;
        try
        {
            summary = svc.RestoreFromDisk(new[] { JournalStore.DefaultPath(true), JournalStore.DefaultPath(false) });
        }
        catch (Exception ex)
        {
            log.Error(LogCategory.Cli, "--restore failed: " + ex);
            log.FlushNow();
            ConsoleAttach.Print("NoClose restore failed: " + ex.Message, outPath);
            return CliParser.ExitPartialFailure;
        }
        var text = "NoClose restore: " + summary.ToText();
        log.FlushNow();
        ConsoleAttach.Print(text, outPath);
        return summary.AnyFailure ? CliParser.ExitPartialFailure : 0;
    }
}
