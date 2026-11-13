namespace NoClose.Core.Guards;

public enum SilentMediaPhase { Stopped, Starting, Running, RetryWait, NoDevice }

/// <summary>Immutable state of the silent media session as published by the audio thread.</summary>
public sealed record SilentMediaState(SilentMediaPhase Phase, string Message, int Failures)
{
    public static readonly SilentMediaState Stopped = new(SilentMediaPhase.Stopped, "", 0);
}

/// <summary>What the diagnostics rule is told about the silent media session (not read through the machine reader).</summary>
public sealed record SilentMediaSnapshot(bool Selected, SilentMediaPhase Phase, string? Message);

/// <summary>Pure rules of the silent media session: retry schedule, HRESULT meaning, state to guard status mapping.</summary>
public static class SilentMediaRules
{
    public const string SessionDisplayName = "NoClose silent media session";

    /// <summary>Delays before reopen attempt 1, 2, 3, 4+ (the last one is the cap).</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff = new[]
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60),
    };

    /// <summary>A stream that delivered no render event for this long is treated as dead and reopened.</summary>
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(5);

    /// <summary>A stream that stayed healthy this long resets the failure counter.</summary>
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(30);

    public static TimeSpan BackoffDelay(int failures)
    {
        int index = Math.Clamp(failures - 1, 0, Backoff.Count - 1);
        return Backoff[index];
    }

    public const int E_NOTFOUND = unchecked((int)0x80070490);
    public const int AUDCLNT_E_NOT_INITIALIZED = unchecked((int)0x88890001);
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
    public const int AUDCLNT_E_RESOURCES_INVALIDATED = unchecked((int)0x88890026);

    /// <summary>The endpoint went away or the audio service restarted: reopen, do not give up.</summary>
    public static bool IsDeviceLost(int hr) =>
        hr is AUDCLNT_E_DEVICE_INVALIDATED or AUDCLNT_E_SERVICE_NOT_RUNNING or AUDCLNT_E_RESOURCES_INVALIDATED or AUDCLNT_E_NOT_INITIALIZED;

    /// <summary>The system has no default render endpoint (no sound device, disabled, or audio service stopped).</summary>
    public static bool IsNoEndpoint(int hr) => hr == E_NOTFOUND;

    public static string DescribeHr(int hr)
    {
        string name = hr switch
        {
            AUDCLNT_E_DEVICE_INVALIDATED => "the audio device was removed or changed",
            AUDCLNT_E_SERVICE_NOT_RUNNING => "the Windows audio service is not running",
            AUDCLNT_E_RESOURCES_INVALIDATED => "the audio resources were invalidated",
            AUDCLNT_E_NOT_INITIALIZED => "the audio client is not initialized",
            AUDCLNT_E_DEVICE_IN_USE => "the audio device is in exclusive use by another application",
            AUDCLNT_E_UNSUPPORTED_FORMAT => "the audio device rejected its own mix format",
            E_NOTFOUND => "no audio output device was found",
            _ => "audio call failed",
        };
        return $"{name} (0x{unchecked((uint)hr):X8})";
    }

    /// <summary>Guard status and text for a session state. Starting counts as Degraded until the stream really runs.</summary>
    public static (GuardStatus Status, string Message) ToGuardStatus(SilentMediaState s) => s.Phase switch
    {
        SilentMediaPhase.Running => (GuardStatus.Applied, string.IsNullOrEmpty(s.Message) ? "Silent audio stream open on the default output device" : s.Message),
        SilentMediaPhase.Starting => (GuardStatus.Degraded, string.IsNullOrEmpty(s.Message) ? "Opening the silent audio stream..." : s.Message),
        SilentMediaPhase.RetryWait => (GuardStatus.Degraded, s.Message),
        SilentMediaPhase.NoDevice => (GuardStatus.Degraded, s.Message),
        _ => (GuardStatus.Off, ""),
    };
}
