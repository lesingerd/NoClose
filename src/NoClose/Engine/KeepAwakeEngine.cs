using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NoClose.Core.Activation;
using NoClose.Core.Diagnostics;
using NoClose.Logging;
using NoClose.Platform;
using NoClose.Platform.Interop;

namespace NoClose.Engine;

/// <summary>
/// Owns the ONE thread that talks to the power manager. All SetThreadExecutionState and PowerCreateRequest/PowerSetRequest/
/// PowerClearRequest calls in the process happen on that thread (execution state is per thread).
/// </summary>
internal sealed class KeepAwakeEngine : IDisposable
{
    private const EXECUTION_STATE ContinuousFlags = EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED;
    private static readonly TimeSpan EnvCheckInterval = TimeSpan.FromMinutes(30);

    private readonly BlockingCollection<EngineCommand> _queue = new();
    private readonly ILogSink _log;
    private readonly IMachineReader _reader;
    private readonly Func<int> _reassertSeconds;
    private readonly Func<int> _tickLogMinutes;
    private readonly Func<string?> _environmentCheck;
    private readonly Action<DateTime> _tickHook;
    private readonly Dictionary<POWER_REQUEST_TYPE, SafePowerRequestHandle> _handles = new();
    private Thread? _thread;

    // state owned by the engine thread
    private EXECUTION_STATE _flags;
    private bool _active;
    private bool _awayActive;
    private string _reason = "";
    private DateTime _assertedAtUtc;
    private DateTime _nextTickUtc;
    private DateTime _lastEnvCheckUtc;
    private DateTime _lastTickLogUtc;
    private DateTime? _lastVerifiedUtc;
    private string _verifyResult = "not yet verified";
    private string? _stesFailure, _requestFailure, _verifyProblem, _envProblem;
    private string? _note;
    private int _tickCount;
    private bool _changedSinceLastLog;

    private volatile EngineStatus _status = EngineStatus.Idle;

    public KeepAwakeEngine(ILogSink log, IMachineReader reader, Func<int> reassertSeconds, Func<int> tickLogMinutes,
        Func<string?> environmentCheck, Action<DateTime> tickHook)
    {
        _log = log;
        _reader = reader;
        _reassertSeconds = reassertSeconds;
        _tickLogMinutes = tickLogMinutes;
        _environmentCheck = environmentCheck;
        _tickHook = tickHook;
    }

    /// <summary>Raised on the engine thread; UI code must marshal.</summary>
    public event Action<EngineStatus>? StatusChanged;

    public EngineStatus Status => _status;

    private readonly object _threadGate = new();
    private volatile bool _shutdownRequested;
    private string? _fatal;
    private string? _loopProblem;
    private string _lastReason = "";
    private bool _lastAwayRequested;
    private bool _wasActiveBeforeCrash;

    /// <summary>Starts the engine thread; restarts it when a previous one has died. Idempotent while the thread is alive.</summary>
    public void Begin()
    {
        lock (_threadGate)
        {
            if (_shutdownRequested) return;
            if (_thread is { IsAlive: true }) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "NoClose.Engine", Priority = ThreadPriority.AboveNormal };
            if (_wasActiveBeforeCrash)
            {
                // The previous thread died while keeping awake: resume with the last request.
                _wasActiveBeforeCrash = false;
                try { _queue.Add(new EngineStart(_lastReason, _lastAwayRequested)); } catch (InvalidOperationException) { }
            }
            _thread.Start();
        }
    }

    public void Post(EngineCommand command)
    {
        if (_shutdownRequested && command is not EngineShutdown) return;
        // A dead engine thread (unexpected failure) is restarted by the next command instead of leaving the queue undrained forever.
        if (_thread != null && !_thread.IsAlive) Begin();
        try { _queue.Add(command); } catch (InvalidOperationException) { /* shut down */ }
    }

    public void Start(string reason, bool awayMode) => Post(new EngineStart(reason, awayMode));
    public void Stop(string why) => Post(new EngineStop(why));
    public void Reassert(string trigger) => Post(new EngineReassert(trigger));

    public Task<EngineStatus> QueryAsync()
    {
        var tcs = new TaskCompletionSource<EngineStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new EngineQuery(tcs));
        return tcs.Task;
    }

    /// <summary>Stops (if active) and ends the engine thread; waits up to <paramref name="timeout"/>. Returns at once when no thread is running.</summary>
    public bool Shutdown(TimeSpan timeout)
    {
        Thread? t;
        lock (_threadGate) t = _thread;
        if (t == null || !t.IsAlive) { _shutdownRequested = true; return true; }
        using var done = new ManualResetEventSlim(false);
        Post(new EngineShutdown(done));
        bool ok = done.Wait(timeout);
        _shutdownRequested = true;
        return ok;
    }

    // ---- engine thread ------------------------------------------------------------------------------------

    private void Run()
    {
        try
        {
            while (true)
            {
                // One failing iteration must never end the thread: log it, surface it as Degraded and carry on.
                try
                {
                    int timeout = _active
                        ? (int)Math.Clamp((_nextTickUtc - DateTime.UtcNow).TotalMilliseconds, 0, int.MaxValue)
                        : 30000;
                    if (_queue.TryTake(out var cmd, timeout))
                    {
                        if (Handle(cmd)) return;
                    }
                    if (_active && DateTime.UtcNow >= _nextTickUtc)
                    {
                        Tick();
                        if (_loopProblem != null) { _loopProblem = null; Publish(); }
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(LogCategory.Engine, "Engine loop iteration failed: " + ex);
                    _loopProblem = "Keep-awake engine error: " + ex.Message;
                    if (_active) _nextTickUtc = DateTime.UtcNow.AddSeconds(15); // do not spin on a persistent failure
                    try { Publish(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogCategory.Engine, "Engine thread crashed: " + ex);
            _wasActiveBeforeCrash = _active;
            _fatal = "The keep-awake engine stopped unexpectedly and was released; it restarts with the next request.";
            SafeStopAll();
        }
    }

    /// <returns>true when the thread should exit.</returns>
    private bool Handle(EngineCommand cmd)
    {
        try
        {
            switch (cmd)
            {
                case EngineStart s: DoStart(s); break;
                case EngineStop s: DoStop(s.Why); break;
                case EngineReassert r: DoReassert(r.Trigger); break;
                case EngineQuery q: q.Reply.TrySetResult(_status); break;
                case EngineShutdown sd:
                    if (_active) DoStop("shutdown");
                    sd.Done.Set();
                    return true;
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogCategory.Engine, $"Engine command {cmd.GetType().Name} failed: {ex.Message}");
        }
        return false;
    }

    private bool AwayModeApplicable(bool requested, out string? note)
    {
        note = null;
        if (!requested) return false;
        var caps = _reader.Caps;
        if (caps.AoAc)
        {
            note = "Away mode not applicable on this machine (Modern Standby)";
            _log.Info(LogCategory.Engine, note);
            return false;
        }
        if (!caps.IsS3Capable)
        {
            note = "Away mode not applicable on this machine (no S3 sleep state)";
            _log.Info(LogCategory.Engine, note);
            return false;
        }
        return true;
    }

    private void DoStart(EngineStart s)
    {
        if (_active) DoStop("restart");
        _reason = s.Reason;
        _lastReason = s.Reason;
        _lastAwayRequested = s.AwayMode;
        _fatal = null;
        _loopProblem = null;
        _awayActive = AwayModeApplicable(s.AwayMode, out _note);
        _flags = ContinuousFlags | (_awayActive ? EXECUTION_STATE.ES_AWAYMODE_REQUIRED : 0);
        _stesFailure = _requestFailure = _verifyProblem = _envProblem = null;
        _tickCount = 0;

        AssertFlags(initial: true);
        CreateRequests();
        _assertedAtUtc = DateTime.UtcNow;
        _active = true;
        _lastVerifiedUtc = null;
        _verifyResult = "not yet verified";
        _changedSinceLastLog = true;
        _nextTickUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(_reassertSeconds(), 15, 300));
        _lastTickLogUtc = DateTime.UtcNow;
        _lastEnvCheckUtc = DateTime.UtcNow;
        _log.Info(LogCategory.Engine, $"Keep-awake asserted: flags={_flags}, reason=\"{_reason}\"");
        SelfCheck();
        RunEnvironmentCheck();
        Publish();
    }

    private void AssertFlags(bool initial)
    {
        var prev = Kernel32.SetThreadExecutionState(_flags);
        if (prev == 0)
        {
            _stesFailure = $"SetThreadExecutionState returned NULL (flags=0x{(uint)_flags:X})"; // the API does not document a Win32 error
            _log.Error(LogCategory.Engine, _stesFailure);
        }
        else if (_stesFailure != null)
        {
            _stesFailure = null;
            _log.Info(LogCategory.Engine, "SetThreadExecutionState succeeded again");
        }
        if (initial && prev != 0) _log.Debug(LogCategory.Engine, $"Previous execution state: {prev}");
    }

    private void CreateRequests()
    {
        ClearRequests();
        var failures = new List<string>();
        var types = new List<POWER_REQUEST_TYPE>
        {
            POWER_REQUEST_TYPE.PowerRequestDisplayRequired,
            POWER_REQUEST_TYPE.PowerRequestSystemRequired,
            POWER_REQUEST_TYPE.PowerRequestExecutionRequired,
        };
        if (_awayActive) types.Add(POWER_REQUEST_TYPE.PowerRequestAwayModeRequired);

        foreach (var type in types)
        {
            IntPtr text = Marshal.StringToHGlobalUni(Core.Text.ReasonText.PowerRequestReason(_reason));
            try
            {
                var ctx = new REASON_CONTEXT { Version = 0, Flags = 0x1, Reason = new REASON_UNION { SimpleReasonString = text } };
                var raw = Kernel32.PowerCreateRequest(in ctx);
                if (raw == NativeConstants.INVALID_HANDLE_VALUE || raw == IntPtr.Zero)
                {
                    var err = Marshal.GetLastPInvokeError();
                    failures.Add($"{type} create failed (error {err})");
                    _log.Error(LogCategory.Engine, $"PowerCreateRequest({type}) failed, error {err}");
                    continue;
                }
                var handle = new SafePowerRequestHandle(raw);
                if (!Kernel32.PowerSetRequest(raw, type))
                {
                    var err = Marshal.GetLastPInvokeError();
                    failures.Add($"{type} set failed (error {err})");
                    _log.Error(LogCategory.Engine, $"PowerSetRequest({type}) failed, error {err}");
                    handle.Dispose();
                    continue;
                }
                _handles[type] = handle;
            }
            finally
            {
                Marshal.FreeHGlobal(text); // Windows copies the string
            }
        }
        _requestFailure = failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private void ClearRequests()
    {
        foreach (var (type, handle) in _handles)
        {
            try { Kernel32.PowerClearRequest(handle.DangerousGetHandle(), type); } catch { }
            try { handle.Dispose(); } catch { }
        }
        _handles.Clear();
    }

    private void DoStop(string why)
    {
        if (!_active && _handles.Count == 0) { Publish(); return; }
        ClearRequests();
        Kernel32.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        var uptime = DateTime.UtcNow - _assertedAtUtc;
        _active = false;
        _awayActive = false;
        _flags = 0;
        _log.Info(LogCategory.Engine, $"Keep-awake released ({why}); was active for {ActivationSpec.FormatSpan(uptime)}");
        Publish();
    }

    private void SafeStopAll()
    {
        try { ClearRequests(); Kernel32.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS); } catch { }
        _active = false;
        Publish();
    }

    private void DoReassert(string trigger)
    {
        if (!_active) return;
        _log.Info(LogCategory.Engine, $"Re-asserting keep-awake (trigger: {trigger})");
        FullReassert();
        SelfCheck();
        // The environment check only looks at the current power source: re-run it when that may have changed.
        if (trigger.StartsWith("AC/DC", StringComparison.Ordinal) || trigger.StartsWith("power", StringComparison.Ordinal)) RunEnvironmentCheck();
        _changedSinceLastLog = true;
        Publish();
    }

    /// <summary>clear, close, create, set the request handles and re-assert the continuous state.</summary>
    private void FullReassert()
    {
        CreateRequests();
        AssertFlags(initial: false);
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        _nextTickUtc = now.AddSeconds(Math.Clamp(_reassertSeconds(), 15, 300));
        _tickCount++;

        // 1. pulse without ES_CONTINUOUS: resets the idle timers
        Kernel32.SetThreadExecutionState(EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED);
        // 2. re-assert the continuous state
        AssertFlags(initial: false);
        // 3. every 10th tick: self check
        if (_tickCount % 10 == 0) SelfCheck();
        if (now - _lastEnvCheckUtc >= EnvCheckInterval) RunEnvironmentCheck();
        // 4 + 5. deadline check and guard reapply happen on the host (UI thread)
        try { _tickHook(now); } catch (Exception ex) { _log.Error(LogCategory.Engine, "Tick hook failed: " + ex.Message); }

        // 6. throttled tick line
        if (_changedSinceLastLog || now - _lastTickLogUtc >= TimeSpan.FromMinutes(Math.Max(1, _tickLogMinutes())))
        {
            var idle = SessionIdleProbe.LastInputAge();
            _log.Info(LogCategory.Engine,
                $"Tick: active {ActivationSpec.FormatSpan(now - _assertedAtUtc)}, verify={_verifyResult}, last input {(idle is { } a ? ActivationSpec.FormatSpan(a) + " ago" : "unknown")}");
            _lastTickLogUtc = now;
            _changedSinceLastLog = false;
        }
        Publish();
    }

    private void SelfCheck()
    {
        var rc = PowrProf.CallNtPowerInformation(PowrProf.SystemExecutionState, IntPtr.Zero, 0, out var state, 4);
        if (rc != 0)
        {
            _verifyResult = $"unavailable (NTSTATUS 0x{rc:X8})";
            _lastVerifiedUtc = DateTime.UtcNow;
            return;
        }
        const uint need = (uint)(EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED);
        if ((state & need) == need)
        {
            _verifyResult = "ok";
            _verifyProblem = null;
        }
        else
        {
            _log.Warn(LogCategory.Engine, $"Execution state not reflected by system (state=0x{state:X}); re-creating");
            FullReassert();
            PowrProf.CallNtPowerInformation(PowrProf.SystemExecutionState, IntPtr.Zero, 0, out state, 4);
            if ((state & need) == need)
            {
                _verifyResult = "ok (after re-assert)";
                _verifyProblem = null;
            }
            else
            {
                _verifyResult = $"missing (state=0x{state:X})";
                _verifyProblem = "Windows does not reflect NoClose's execution state";
            }
            _changedSinceLastLog = true;
        }
        _lastVerifiedUtc = DateTime.UtcNow;
    }

    private void RunEnvironmentCheck()
    {
        _lastEnvCheckUtc = DateTime.UtcNow;
        string? problem = null;
        try { problem = _environmentCheck(); }
        catch (Exception ex) { _log.Warn(LogCategory.Engine, "Environment check failed: " + ex.Message); }
        if (problem != _envProblem)
        {
            _changedSinceLastLog = true;
            if (problem != null) _log.Warn(LogCategory.Engine, "Windows may ignore NoClose: " + problem);
        }
        _envProblem = problem;
    }

    private void Publish()
    {
        var problems = new[] { _fatal, _loopProblem, _stesFailure, _requestFailure, _verifyProblem, _envProblem }.Where(p => p != null).ToList();
        EngineState state = _fatal != null ? EngineState.Degraded : !_active ? EngineState.Idle : problems.Count > 0 ? EngineState.Degraded : EngineState.Active;
        var status = new EngineStatus(state, _reason, _active ? _assertedAtUtc : null, _lastVerifiedUtc, _verifyResult,
            problems.Count > 0 ? string.Join("; ", problems) : null, _awayActive, _note);
        _status = status;
        try { StatusChanged?.Invoke(status); } catch (Exception ex) { _log.Error(LogCategory.Engine, "StatusChanged handler failed: " + ex.Message); }
    }

    public void Dispose() => _queue.CompleteAdding();
}
