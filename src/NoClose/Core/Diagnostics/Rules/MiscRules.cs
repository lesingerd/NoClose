using NoClose.Core.Guards;

namespace NoClose.Core.Diagnostics.Rules;

/// <summary>R-REQ: current "powercfg /requests" output (only available when elevated).</summary>
public sealed class PowercfgRequestsRule : IDiagnosticRule
{
    public string Id => "R-REQ";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        var text = r.PowercfgRequests;
        if (string.IsNullOrWhiteSpace(text)) yield break;
        bool mentioned = text.Contains("NoClose:", StringComparison.OrdinalIgnoreCase);
        var trimmed = text.Trim();
        if (trimmed.Length > 2000) trimmed = trimmed[..2000] + "...";
        yield return R.Find(Id, Severity.Info, mentioned ? "NoClose power requests are visible to Windows" : "Current power requests",
            (mentioned ? "\"NoClose:\" appears in the list of active requests." : "No NoClose request is listed (expected when NoClose is idle).")
            + Environment.NewLine + trimmed);
    }
}

/// <summary>R-ENGINE: the keep-awake engine itself is degraded.</summary>
public sealed class EngineRule : IDiagnosticRule
{
    private readonly EngineSnapshot _snapshot;
    public EngineRule(EngineSnapshot snapshot) => _snapshot = snapshot;
    public string Id => "R-ENGINE";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        if (!(_snapshot.Active && _snapshot.Degraded)) yield break;
        yield return R.Find(Id, Severity.High, "Keep-awake engine is degraded",
            _snapshot.DegradedReason ?? "Windows did not accept or reflect NoClose's power requests.",
            "See the log for details; run NoClose --diagnose for other settings that may cause this.",
            GuardId.ExecutionState);
    }
}

/// <summary>R-MEDIA: state of the optional silent media session and (read-only) whether an audio output endpoint exists.</summary>
public sealed class SilentMediaRule : IDiagnosticRule
{
    private readonly SilentMediaSnapshot? _snapshot;
    public SilentMediaRule(SilentMediaSnapshot? snapshot) => _snapshot = snapshot;
    public string Id => "R-MEDIA";

    private const string Complement =
        " The silent media session complements the power requests; it does not replace them, and it does not defeat Remote Desktop idle limits or forced logoff/restart.";

    public IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r)
    {
        bool? endpoint = r.HasAudioRenderEndpoint;
        string endpointText = endpoint switch
        {
            true => "An audio output device is present.",
            false => "No audio output device was found (the silent media session needs one).",
            _ => "Whether an audio output device exists could not be determined.",
        };

        if (_snapshot == null)
        {
            yield return R.Find(Id, Severity.Info, "Silent media session state unknown",
                "Silent media session state unknown (not reported by the command-line scan; use Diagnostics... in the tray menu to see the running instance's state). " + endpointText + Complement,
                "No action needed.", GuardId.SilentMedia);
            yield break;
        }

        if (_snapshot is { Selected: true })
        {
            if (_snapshot.Phase == SilentMediaPhase.Running)
            {
                yield return R.Find(Id, Severity.Info, "Silent media session is active",
                    "NoClose keeps a silent audio stream open on the default output device, so Windows sees active media playback. " + endpointText + Complement,
                    GuardId.SilentMedia);
                yield break;
            }
            if (_snapshot.Phase != SilentMediaPhase.Stopped)
            {
                yield return R.Find(Id, Severity.Low, "Silent media session is not active",
                    (string.IsNullOrWhiteSpace(_snapshot.Message) ? "The silent audio stream is not open." : _snapshot.Message) + " " + endpointText + " NoClose keeps retrying." + Complement,
                    endpoint == false
                        ? "Connect or enable an audio output device (a virtual or HDMI device also works), or switch the guard off."
                        : "Check that the Windows Audio service is running and the default output device is enabled.",
                    GuardId.SilentMedia);
                yield break;
            }
        }
        yield return R.Find(Id, Severity.Info, "Silent media session is off",
            endpointText + " Turn on the optional Silent media session guard if a lock or idle path on this PC reacts to media playback but ignores power requests." + Complement,
            "No action needed unless the PC still locks or idles although the power requests are accepted.", GuardId.SilentMedia);
    }
}
