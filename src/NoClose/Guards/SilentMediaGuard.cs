using NoClose.Core.Guards;
using NoClose.Logging;

namespace NoClose.Guards;

/// <summary>The audio side of the silent media session; implemented by the WASAPI session, faked in tests.</summary>
internal interface ISilentMediaSession : IDisposable
{
    SilentMediaState State { get; }
    /// <summary>Raised on any thread whenever <see cref="State"/> changed.</summary>
    event Action? StateChanged;
    void Start();
    void Stop();
    /// <summary>Verifies the stream is alive and restarts it if not; true when something was restarted.</summary>
    bool EnsureAlive();
}

/// <summary>
/// Keeps a real but silent audio playback stream open so that Windows and audio drivers see active media playback. Complements the
/// power requests; it does not replace them. Changes no settings (nothing journaled, no admin) and simulates no input.
/// </summary>
internal sealed class SilentMediaGuard : GuardBase
{
    private readonly Func<GuardContext, ISilentMediaSession> _factory;
    private ISilentMediaSession? _session;

    public SilentMediaGuard() : this(ctx => new NoClose.Platform.SilentAudioSession(ctx.Log, ctx.Clock)) { }

    internal SilentMediaGuard(Func<GuardContext, ISilentMediaSession> factory) => _factory = factory;

    public override GuardId Id => GuardId.SilentMedia;
    public override string DisplayName => "Silent media session";
    public override string Description =>
        "Keeps a silent audio stream open on your default output device, so Windows and audio drivers see the same \"media is playing\" " +
        "state a media player creates. Some lock and idle paths and drivers respect real media playback. You hear nothing, no setting is " +
        "changed and no keyboard or mouse input is simulated. It complements the power requests and does not replace them; it does not " +
        "defeat Remote Desktop idle limits or forced logoff/restart. It shows up as \"NoClose silent media session\" in the Windows volume mixer.";

    /// <summary>Raised (on any thread) when the audio session changed state; the host marshals to the UI thread and calls <see cref="Sync"/>.</summary>
    public event Action? SessionChanged;

    /// <summary>True while NoClose wants the stream open (between Apply and Restore).</summary>
    public bool Wanted { get; private set; }

    public override GuardAvailability CheckAvailability(GuardContext ctx) => GuardAvailability.Ok;

    private ISilentMediaSession Session(GuardContext ctx)
    {
        if (_session == null)
        {
            _session = _factory(ctx);
            _session.StateChanged += () => SessionChanged?.Invoke();
        }
        return _session;
    }

    public override GuardResult Apply(GuardContext ctx)
    {
        var s = Session(ctx);
        Wanted = true;
        s.Start();
        return Sync();
    }

    public override GuardResult Reapply(GuardContext ctx)
    {
        if (!Wanted) return new GuardResult(Status, Message);
        var s = Session(ctx);
        if (s.EnsureAlive()) ctx.Log.Info(LogCategory.Guard, $"{DisplayName}: the silent stream was not alive and was restarted");
        return Sync();
    }

    public override GuardResult Restore(GuardContext ctx)
    {
        Wanted = false;
        try { _session?.Stop(); }
        catch (Exception ex) { ctx.Log.Error(LogCategory.Guard, $"{DisplayName}: stop failed: {ex.Message}"); }
        return Set(GuardStatus.Off, "Silent audio stream closed");
    }

    /// <summary>Copies the audio session's state into the guard status. UI thread.</summary>
    public GuardResult Sync()
    {
        if (!Wanted || _session == null) return new GuardResult(Status, Message);
        var (status, message) = SilentMediaRules.ToGuardStatus(_session.State);
        return Set(status, message);
    }

    /// <summary>Snapshot for the diagnostics rule.</summary>
    public SilentMediaSnapshot Snapshot(bool selected) =>
        new(selected, _session?.State.Phase ?? SilentMediaPhase.Stopped, _session?.State.Message);
}
