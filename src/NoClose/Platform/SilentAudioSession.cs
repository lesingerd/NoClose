using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NoClose.Core.Guards;
using NoClose.Core.Time;
using NoClose.Guards;
using NoClose.Logging;
using NoClose.Platform.Interop;

namespace NoClose.Platform;

/// <summary>
/// Keeps a real but silent WASAPI shared-mode render stream open on the default output device (eRender / eMultimedia), so Windows and
/// audio drivers see active media playback. Runs on its own dedicated MTA thread with an event driven render loop (every buffer is
/// submitted with AUDCLNT_BUFFERFLAGS_SILENT). Reopens with a backoff when the device goes away, the default device changes or the
/// audio service restarts. Changes no setting and simulates no input.
/// </summary>
internal sealed class SilentAudioSession : ISilentMediaSession
{
    private enum EndKind { Stopped, DeviceChanged, NoDevice, Failed }

    private readonly record struct StreamEnd(EndKind Kind, int Hr, string Text);

    /// <summary>Everything one audio thread owns. A new one is created per Start so that an abandoned (stuck) thread cannot disturb its successor.</summary>
    internal sealed class Run : IDisposable
    {
        public readonly ManualResetEvent Stop = new(false);
        public readonly AutoResetEvent Render = new(false);
        public readonly AutoResetEvent Wake = new(false);
        public volatile int ReopenRequested;
        public volatile string? CurrentDeviceId;
        public long LastActivityTick = Environment.TickCount64;

        public void Dispose()
        {
            Stop.Dispose();
            Render.Dispose();
            Wake.Dispose();
        }
    }

    private readonly ILogSink _log;
    private readonly Throttle _throttle;
    private readonly object _gate = new();
    private Run? _run;
    private Thread? _thread;
    private SilentMediaState _state = SilentMediaState.Stopped;

    public SilentAudioSession(ILogSink log, IClock clock)
    {
        _log = log;
        _throttle = new Throttle(TimeSpan.FromMinutes(10), clock);
    }

    public event Action? StateChanged;

    public SilentMediaState State
    {
        get { lock (_gate) return _state; }
    }

    // ---- control (UI thread) ------------------------------------------------------------------------------------

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true } && _run != null) return;
            StartLocked();
        }
        RaiseChanged();
    }

    private void StartLocked()
    {
        var run = new Run();
        var thread = new Thread(() => ThreadMain(run)) { IsBackground = true, Name = "NoClose silent media" };
        thread.SetApartmentState(ApartmentState.MTA);
        _run = run;
        _thread = thread;
        _state = new SilentMediaState(SilentMediaPhase.Starting, "Opening the silent audio stream...", 0);
        thread.Start();
        _log.Info(LogCategory.Audio, "Silent media session: starting audio thread");
    }

    public void Stop()
    {
        Run? run;
        Thread? thread;
        lock (_gate)
        {
            run = _run;
            thread = _thread;
            _run = null;
            _thread = null;
        }
        if (run == null) return;
        try { run.Stop.Set(); } catch (ObjectDisposedException) { /* the thread already ended and disposed its state */ }
        if (thread != null && thread.IsAlive && !thread.Join(TimeSpan.FromSeconds(3)))
            _log.Warn(LogCategory.Audio, "Silent media session: the audio thread did not stop within 3 seconds; it is abandoned (background thread, disposes its own state on exit)");
        lock (_gate) _state = SilentMediaState.Stopped;
        _log.Info(LogCategory.Audio, "Silent media session: stopped");
        RaiseChanged();
    }

    public void Dispose() => Stop();

    /// <summary>Verifies the stream is alive; restarts the thread / stream if not. Returns true when something was restarted.</summary>
    public bool EnsureAlive()
    {
        bool restarted = false;
        lock (_gate)
        {
            var run = _run;
            if (run == null) return false; // not wanted
            if (_thread is not { IsAlive: true })
            {
                _log.Warn(LogCategory.Audio, "Silent media session: the audio thread was not running; restarting it");
                StartLocked();
                restarted = true;
            }
            else if (_state.Phase == SilentMediaPhase.Running)
            {
                long idle = Environment.TickCount64 - Interlocked.Read(ref run.LastActivityTick);
                if (idle > SilentMediaRules.StallLimit.TotalMilliseconds * 3)
                {
                    _log.Error(LogCategory.Audio, $"Silent media session: the audio thread made no progress for {idle / 1000} s; abandoning it and starting a new one");
                    try { run.Stop.Set(); } catch (ObjectDisposedException) { }
                    StartLocked();
                    restarted = true;
                }
            }
            else if (_state.Phase is SilentMediaPhase.RetryWait or SilentMediaPhase.NoDevice)
            {
                try { run.Wake.Set(); } catch (ObjectDisposedException) { } // skip the rest of the backoff: the guard check is a good moment to look again
            }
        }
        if (restarted) RaiseChanged();
        return restarted;
    }

    // ---- state ---------------------------------------------------------------------------------------------------

    private void SetState(Run run, SilentMediaPhase phase, string message, int failures)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(run, _run)) return; // stale thread
            var next = new SilentMediaState(phase, message, failures);
            if (next == _state) return;
            _state = next;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { _log.Warn(LogCategory.Audio, "Silent media session: state handler failed: " + ex.Message); }
    }

    private void LogFailure(string key, string text)
    {
        if (_throttle.TryPass(key)) _log.Warn(LogCategory.Audio, "Silent media session: " + text);
    }

    // ---- audio thread --------------------------------------------------------------------------------------------

    private void ThreadMain(Run run)
    {
        using var mta = new MtaScope();
        int failures = 0;
        IMMDeviceEnumerator? enumerator = null;
        AudioNotificationClient? notifications = null;
        bool registered = false;
        try
        {
            while (!run.Stop.WaitOne(0))
            {
                // One enumerator + notification client per Run, kept registered across reopen and backoff cycles.
                if (!registered)
                {
                    try { registered = TryRegister(run, ref enumerator, ref notifications); }
                    catch (Exception) { registered = false; }
                    if (!registered) LogFailure("register", "device change notifications could not be registered; relying on periodic checks");
                }

                StreamEnd end;
                try { end = OpenAndRender(run, enumerator, ref failures); }
                catch (Exception ex)
                {
                    end = new StreamEnd(EndKind.Failed, ex is COMException ce ? ce.HResult : ex.HResult, "unexpected error: " + ex.Message);
                }
                if (end.Kind == EndKind.Stopped) break;

                if (end.Kind == EndKind.DeviceChanged)
                {
                    failures = 0;
                    _log.Info(LogCategory.Audio, "Silent media session: " + end.Text + "; reopening");
                    SetState(run, SilentMediaPhase.Starting, "Output device changed; reopening...", 0);
                    if (run.Stop.WaitOne(300)) break; // let the device settle
                    continue;
                }

                failures++;
                var delay = SilentMediaRules.BackoffDelay(failures);
                bool none = end.Kind == EndKind.NoDevice;
                var phase = none ? SilentMediaPhase.NoDevice : SilentMediaPhase.RetryWait;
                var text = none
                    ? "No audio output device is available"
                    : "The silent audio stream is not running: " + end.Text;
                SetState(run, phase, $"{text}. Retrying in {delay.TotalSeconds:0} s.", failures);
                LogFailure("fail:" + end.Kind + ":" + end.Hr, $"{text} (attempt {failures}, next try in {delay.TotalSeconds:0} s)");

                int woke = WaitHandle.WaitAny(new WaitHandle[] { run.Stop, run.Wake }, delay);
                if (woke == 0) break;
                if (woke == 1) _log.Info(LogCategory.Audio, "Silent media session: retrying early (device or state change)");
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogCategory.Audio, "Silent media session: audio thread failed: " + ex);
            SetState(run, SilentMediaPhase.RetryWait, "The audio thread failed: " + ex.Message + " The guard check will restart it.", failures);
        }
        finally
        {
            if (registered && enumerator != null && notifications != null)
            {
                try { enumerator.UnregisterEndpointNotificationCallback(notifications); } catch (Exception) { }
            }
            ComHelper.Release(enumerator);
            try { run.Dispose(); } catch (Exception) { }
        }
    }

    private bool TryRegister(Run run, ref IMMDeviceEnumerator? enumerator, ref AudioNotificationClient? notifications)
    {
        if (enumerator == null)
        {
            int hr = ComHelper.CreateEnumerator(out enumerator);
            if (hr < 0 || enumerator == null) { enumerator = null; return false; }
        }
        notifications ??= new AudioNotificationClient(this, run);
        return enumerator.RegisterEndpointNotificationCallback(notifications) >= 0;
    }

    private StreamEnd OpenAndRender(Run run, IMMDeviceEnumerator? enumerator, ref int failures)
    {
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        IAudioSessionControl? control = null;
        bool started = false;
        IntPtr mixFormat = IntPtr.Zero;
        long runningSince = 0;
        try
        {
            SetState(run, SilentMediaPhase.Starting, "Opening the silent audio stream...", failures);

            if (enumerator == null) return Fail(unchecked((int)0x80004005), "the audio device enumerator is unavailable");

            // Reset before the query: a change landing after this point sets the flag and causes at most one extra reopen.
            run.CurrentDeviceId = null;
            run.ReopenRequested = 0;

            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var pDevice);
            if (hr < 0 || pDevice == IntPtr.Zero)
                return SilentMediaRules.IsNoEndpoint(hr) || pDevice == IntPtr.Zero && hr >= 0
                    ? new StreamEnd(EndKind.NoDevice, hr, "no default audio output device")
                    : Fail(hr, "default audio output device query failed");
            device = ComHelper.Wrap<IMMDevice>(pDevice);
            if (device.GetId(out var pId) >= 0) run.CurrentDeviceId = ComHelper.TakeString(pId);

            hr = device.Activate(CoreAudioConstants.IID_IAudioClient, Ole32.CLSCTX_ALL, IntPtr.Zero, out var pClient);
            if (hr < 0) return Fail(hr, "the audio client could not be activated");
            client = ComHelper.Wrap<IAudioClient>(pClient);

            hr = client.GetMixFormat(out mixFormat);
            if (hr < 0 || mixFormat == IntPtr.Zero) return Fail(hr, "the mix format could not be read");
            var fmt = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormat);

            hr = client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, CoreAudioConstants.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                CoreAudioConstants.BufferDuration, 0, mixFormat, IntPtr.Zero);
            if (hr < 0) return Fail(hr, "the audio stream could not be initialized");

            hr = client.SetEventHandle(run.Render.SafeWaitHandle.DangerousGetHandle());
            if (hr < 0) return Fail(hr, "the render event could not be set");
            hr = client.GetBufferSize(out uint bufferFrames);
            if (hr < 0 || bufferFrames == 0) return Fail(hr, "the buffer size could not be read");
            hr = client.GetService(CoreAudioConstants.IID_IAudioRenderClient, out var pRender);
            if (hr < 0) return Fail(hr, "the render client could not be obtained");
            render = ComHelper.Wrap<IAudioRenderClient>(pRender);

            // Best effort: a readable name in the Windows volume mixer.
            if (client.GetService(CoreAudioConstants.IID_IAudioSessionControl, out var pControl) >= 0 && pControl != IntPtr.Zero)
            {
                control = ComHelper.Wrap<IAudioSessionControl>(pControl);
                control.SetDisplayName(SilentMediaRules.SessionDisplayName, IntPtr.Zero);
            }

            hr = SubmitSilence(render, bufferFrames); // prefill the whole buffer with silence
            if (hr < 0) return Fail(hr, "the first silent buffer could not be written");
            hr = client.Start();
            if (hr < 0) return Fail(hr, "the audio stream could not be started");
            started = true;

            runningSince = Environment.TickCount64;
            Interlocked.Exchange(ref run.LastActivityTick, runningSince);
            var description = $"Silent audio stream open on the default output device ({fmt.nSamplesPerSec} Hz, {fmt.nChannels} ch)";
            _log.Info(LogCategory.Audio, "Silent media session: stream started; " + description);
            SetState(run, SilentMediaPhase.Running, description, 0);

            var handles = new WaitHandle[] { run.Stop, run.Render, run.Wake };
            while (true)
            {
                int index = WaitHandle.WaitAny(handles, 1000);
                if (index == 0) return new StreamEnd(EndKind.Stopped, 0, "");
                if (run.ReopenRequested != 0)
                {
                    run.ReopenRequested = 0;
                    return new StreamEnd(EndKind.DeviceChanged, 0, "the default output device changed or was removed");
                }
                long now = Environment.TickCount64;
                if (index == 1)
                {
                    hr = client.GetCurrentPadding(out uint padding);
                    if (hr >= 0 && bufferFrames > padding) hr = SubmitSilence(render, bufferFrames - padding);
                    if (hr < 0) return Fail(hr, SilentMediaRules.DescribeHr(hr));
                    Interlocked.Exchange(ref run.LastActivityTick, now);
                }
                else if (index == WaitHandle.WaitTimeout && now - Interlocked.Read(ref run.LastActivityTick) > SilentMediaRules.StallLimit.TotalMilliseconds)
                {
                    return Fail(SilentMediaRules.AUDCLNT_E_DEVICE_INVALIDATED, "the audio stream stalled (no render events)");
                }
            }
        }
        finally
        {
            if (runningSince != 0 && Environment.TickCount64 - runningSince >= SilentMediaRules.HealthyAfter.TotalMilliseconds) failures = 0;
            if (client != null && started)
            {
                try { client.Stop(); client.Reset(); } catch (Exception) { }
            }
            if (mixFormat != IntPtr.Zero) Ole32.CoTaskMemFree(mixFormat);
            ComHelper.Release(control);
            ComHelper.Release(render);
            ComHelper.Release(client);
            ComHelper.Release(device);
        }
    }

    private static StreamEnd Fail(int hr, string what) =>
        new(EndKind.Failed, hr, hr < 0 && !what.Contains("0x", StringComparison.Ordinal) ? $"{what}: {SilentMediaRules.DescribeHr(hr)}" : what);

    private static int SubmitSilence(IAudioRenderClient render, uint frames)
    {
        int hr = render.GetBuffer(frames, out _);
        if (hr < 0) return hr;
        return render.ReleaseBuffer(frames, CoreAudioConstants.AUDCLNT_BUFFERFLAGS_SILENT);
    }

    // ---- device notifications (COM callback threads; they only set events) ---------------------------------------

    internal void OnDefaultDeviceChanged(Run run, EDataFlow flow, ERole role, string? deviceId)
    {
        if (flow != EDataFlow.eRender || role != ERole.eMultimedia) return;
        if (deviceId != null && deviceId == run.CurrentDeviceId) return;
        _log.Info(LogCategory.Audio, $"Silent media session: default output device changed to {deviceId ?? "(none)"}");
        run.ReopenRequested = 1;
        run.Wake.Set();
    }

    internal void OnDeviceStateChanged(Run run, string deviceId, uint newState)
    {
        if (deviceId == run.CurrentDeviceId && newState != CoreAudioConstants.DEVICE_STATE_ACTIVE)
        {
            _log.Info(LogCategory.Audio, $"Silent media session: the output device is no longer active (state 0x{newState:X})");
            run.ReopenRequested = 1;
        }
        // an endpoint became usable (or changed) while we are not rendering: look again now
        run.Wake.Set();
    }

    internal void OnDeviceRemoved(Run run, string deviceId)
    {
        if (deviceId == run.CurrentDeviceId)
        {
            _log.Info(LogCategory.Audio, "Silent media session: the output device was removed");
            run.ReopenRequested = 1;
        }
        run.Wake.Set();
    }

    internal void OnDeviceAdded(Run run) => run.Wake.Set();
}

/// <summary>IMMNotificationClient implementation. Callbacks arrive on COM threads and must return quickly and never call back into COM.</summary>
[GeneratedComClass]
internal sealed partial class AudioNotificationClient : IMMNotificationClient
{
    private readonly SilentAudioSession _owner;
    private readonly SilentAudioSession.Run _run;

    public AudioNotificationClient(SilentAudioSession owner, SilentAudioSession.Run run)
    {
        _owner = owner;
        _run = run;
    }

    public int OnDeviceStateChanged(string deviceId, uint newState)
    {
        try { _owner.OnDeviceStateChanged(_run, deviceId, newState); } catch (Exception) { }
        return 0;
    }

    public int OnDeviceAdded(string deviceId)
    {
        try { _owner.OnDeviceAdded(_run); } catch (Exception) { }
        return 0;
    }

    public int OnDeviceRemoved(string deviceId)
    {
        try { _owner.OnDeviceRemoved(_run, deviceId); } catch (Exception) { }
        return 0;
    }

    public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
    {
        try { _owner.OnDefaultDeviceChanged(_run, flow, role, defaultDeviceId); } catch (Exception) { }
        return 0;
    }

    public int OnPropertyValueChanged(string deviceId, PROPERTYKEY key) => 0;
}

/// <summary>Read-only probe: is there a default audio render endpoint? Runs on a short lived MTA thread.</summary>
internal static class AudioEndpointProbe
{
    internal static bool? HasDefaultRenderEndpoint()
    {
        bool? result = null;
        var thread = new Thread(() =>
        {
            using var mta = new MtaScope();
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            try
            {
                if (ComHelper.CreateEnumerator(out enumerator) < 0 || enumerator == null) return;
                int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var p);
                if (hr >= 0 && p != IntPtr.Zero)
                {
                    device = ComHelper.Wrap<IMMDevice>(p);
                    result = true;
                }
                else if (SilentMediaRules.IsNoEndpoint(hr)) result = false;
            }
            catch (Exception) { result = null; }
            finally
            {
                ComHelper.Release(device);
                ComHelper.Release(enumerator);
            }
        }) { IsBackground = true, Name = "NoClose audio probe" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(3)) ? result : null;
    }
}
