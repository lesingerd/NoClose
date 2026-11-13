using System.ComponentModel;
using System.Diagnostics;
using NoClose.Core.Activation;
using NoClose.Platform.Interop;

namespace NoClose.Platform;

/// <summary>Result of looking at one process id. "Unobservable" = it exists but Windows will not let us query it.</summary>
internal enum ProcessProbe { Dead, Alive, AliveUnobservable }

/// <summary>
/// Watches the process of a WhileProcess activation. Reports "alive" observations to <c>onPoll</c> (called on a thread-pool
/// thread). By PID: an Exited event plus polling; by name: polling every 5 s. The grace logic (two consecutive misses)
/// lives in <see cref="ActivationPolicy"/>. A process that exists but cannot be opened with query rights (another account, a
/// SYSTEM or service process watched from a non-elevated NoClose) counts as ALIVE, never as ended.
/// </summary>
internal sealed class ProcessWatcher : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ActivationSpec _spec;
    private readonly Action<bool> _onPoll;
    private System.Threading.Timer? _timer;
    private Process? _process;
    private DateTime? _pidStartTime;
    private int _sessionId = -1;

    public ProcessWatcher(ActivationSpec spec, Action<bool> onPoll)
    {
        _spec = spec;
        _onPoll = onPoll;
    }

    /// <summary>Looks at a PID. <paramref name="held"/>/<paramref name="expectedStart"/> detect PID reuse when the start time is readable.</summary>
    internal static ProcessProbe ProbePid(int pid, Process? held = null, DateTime? expectedStart = null)
    {
        try
        {
            var p = held ?? Process.GetProcessById(pid);
            try
            {
                if (p.HasExited) return ProcessProbe.Dead;
                if (expectedStart is { } start && p.StartTime != start) return ProcessProbe.Dead; // PID was reused
                return ProcessProbe.Alive;
            }
            finally
            {
                if (held == null) p.Dispose();
            }
        }
        catch (ArgumentException) { return ProcessProbe.Dead; } // no process with that id
        catch (Win32Exception ex) when (ex.NativeErrorCode == (int)NativeConstants.ERROR_ACCESS_DENIED)
        {
            return ProbeWithSynchronize(pid);
        }
        catch (InvalidOperationException) { return ProcessProbe.Dead; }
        catch (Win32Exception) { return ProbeWithSynchronize(pid); }
    }

    /// <summary>Fallback when the process cannot be queried: SYNCHRONIZE is often still granted, otherwise it merely exists.</summary>
    private static ProcessProbe ProbeWithSynchronize(int pid)
    {
        var h = Kernel32.OpenProcess(NativeConstants.PROCESS_SYNCHRONIZE, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                return Kernel32.WaitForSingleObject(h, 0) == NativeConstants.WAIT_TIMEOUT ? ProcessProbe.Alive : ProcessProbe.Dead;
            }
            finally { Kernel32.CloseHandle(h); }
        }
        var err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (err == (int)NativeConstants.ERROR_ACCESS_DENIED)
        {
            // Not observable, but a process with this id still exists (the id was found): do not report "ended".
            try
            {
                using var p = Process.GetProcessById(pid);
                return ProcessProbe.AliveUnobservable;
            }
            catch (ArgumentException) { return ProcessProbe.Dead; }
            catch (Exception) { return ProcessProbe.AliveUnobservable; }
        }
        return ProcessProbe.Dead; // ERROR_INVALID_PARAMETER: no such process
    }

    public static bool IsRunning(ActivationSpec spec)
    {
        try
        {
            if (spec.ProcessId is { } pid) return ProbePid(pid) != ProcessProbe.Dead;
            if (!string.IsNullOrWhiteSpace(spec.ProcessName)) return AnyByName(spec.ProcessName, CurrentSessionId());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
        return false;
    }

    private static int CurrentSessionId()
    {
        using var me = Process.GetCurrentProcess();
        return me.SessionId;
    }

    /// <summary>Name based watches only consider processes of this logon session: another user's build.exe must not keep us alive.</summary>
    private static bool AnyByName(string name, int sessionId)
    {
        var list = Process.GetProcessesByName(name);
        try
        {
            foreach (var p in list)
            {
                try { if (p.SessionId == sessionId) return true; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* exited meanwhile / unreadable */ }
            }
            return false;
        }
        finally
        {
            foreach (var p in list) p.Dispose();
        }
    }

    public void Start()
    {
        _sessionId = CurrentSessionId();
        if (_spec.ProcessId is { } pid)
        {
            try
            {
                _process = Process.GetProcessById(pid);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                _process = null;
            }
            if (_process != null)
            {
                try { _pidStartTime = _process.StartTime; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    _pidStartTime = null; // PID reuse check unavailable (query access denied); polling still works
                }
                try
                {
                    _process.EnableRaisingEvents = true;
                    _process.Exited += (_, _) => _onPoll(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // No SYNCHRONIZE access: polling alone watches the process.
                }
            }
        }
        _timer = new System.Threading.Timer(_ => Poll(), null, PollInterval, PollInterval);
    }

    private void Poll()
    {
        bool alive;
        if (_spec.ProcessId is { } pid)
            alive = ProbePid(pid, _process, _pidStartTime) != ProcessProbe.Dead;
        else
            alive = AnyByName(_spec.ProcessName ?? "", _sessionId < 0 ? CurrentSessionId() : _sessionId);
        _onPoll(alive);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _process?.Dispose();
        _process = null;
    }
}
