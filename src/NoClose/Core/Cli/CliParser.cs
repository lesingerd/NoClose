using System.Text;
using NoClose.Core.Activation;
using NoClose.Core.Guards;
using NoClose.Core.Text;

namespace NoClose.Core.Cli;

public static class CliParser
{
    public const int ExitOk = 0, ExitPartialFailure = 1, ExitUsage = 2, ExitForwardFailed = 3;

    public const string Usage =
@"NoClose - keeps an interactive Windows session awake, unlocked and logged on.

Usage: NoClose.exe [options]

  --start                    Start keeping awake (indefinitely unless a mode is given)
  --stop                     Stop keeping awake and restore every guard (app keeps running in the tray)
  --duration <d>             Start for a duration: 90m, 8h, 1h30m, 2d, 01:30 (hh:mm), 1.5h  (1 minute .. 30 days)
  --until <t>                Start until HH:mm (local; tomorrow if already passed) or yyyy-MM-ddTHH:mm
  --while-process <n|pid>    Start while the process (image name or PID) is running
  --reason ""<text>""          Reason shown to Windows (max 120 chars, default ""Long-running task"")
  --guards <list>            Per-run override: comma list of ExecutionState,ScreenSaver,PowerPlan,ActiveHours,EndSession,SilentMedia
                             (opt-in guards still need a one-time confirmation in the window)
  --minimized                Start with the status window minimized
  --diagnose                 Read-only scan of settings that can lock/log off/restart the session
  --restore                  Apply and clear any stale restore journal
  --out <path>               Output file for --diagnose / --restore
  --help, -?                 This text

Switches are case-insensitive and /switch is accepted as an alias of --switch.
Exit codes: 0 ok, 1 partial failure, 2 usage error, 3 forwarding to the running instance failed.";

    public static CliParseResult Parse(string[] args) => Parse(args, DateTime.Now, null);

    public static CliParseResult Parse(string[] args, DateTime nowLocal, TimeZoneInfo? tz = null)
    {
        bool start = false, stop = false, diagnose = false, restore = false, help = false, minimized = false;
        bool handoff = false, restoredAfterRestart = false;
        string? handoffSid = null;
        string? reason = null, outPath = null;
        List<GuardId>? guards = null;
        ActivationSpec? mode = null;
        int modeCount = 0;

        for (int i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            string name;
            string? inlineValue = null;
            if (raw == "-?" || raw == "/?") name = "help";
            else if (raw.StartsWith("--", StringComparison.Ordinal) && raw.Length > 2) name = raw[2..];
            else if (raw.StartsWith('/') && raw.Length > 1) name = raw[1..];
            else return Fail($"Unexpected argument '{raw}'.");

            int eq = name.IndexOf('=');
            if (eq > 0) { inlineValue = name[(eq + 1)..]; name = name[..eq]; }
            name = name.ToLowerInvariant();

            string? Value()
            {
                if (inlineValue != null) return inlineValue;
                // A following switch is not a value: "--reason --stop" must be an error, not reason="--stop".
                if (i + 1 < args.Length && !LooksLikeSwitch(args[i + 1])) return args[++i];
                return null;
            }

            switch (name)
            {
                case "start": start = true; break;
                case "stop": stop = true; break;
                case "diagnose": diagnose = true; break;
                case "restore": restore = true; break;
                case "minimized": minimized = true; break;
                case "help": help = true; break;
                case "handoff":
                    handoff = true;
                    // optional value: the SID of the user who asked for elevation
                    handoffSid = inlineValue ?? (i + 1 < args.Length && args[i + 1].StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? args[++i] : null);
                    break;
                case "restored-after-restart": restoredAfterRestart = true; break;
                case "duration":
                    {
                        var v = Value();
                        if (v == null) return Fail("--duration needs a value.");
                        if (!DurationParser.TryParse(v, out var d, out var err)) return Fail(err);
                        mode = ActivationSpec.ForDuration(d); modeCount++;
                        break;
                    }
                case "until":
                    {
                        var v = Value();
                        if (v == null) return Fail("--until needs a value.");
                        if (!UntilParser.TryParse(v, nowLocal, out var t, tz))
                            return Fail($"Cannot parse --until '{v}'. Use HH:mm or yyyy-MM-ddTHH:mm (within 30 days, in the future).");
                        mode = ActivationSpec.ForUntil(t); modeCount++;
                        break;
                    }
                case "while-process":
                    {
                        var v = Value();
                        if (string.IsNullOrWhiteSpace(v)) return Fail("--while-process needs a process name or PID.");
                        mode = ActivationSpec.ParseProcess(v); modeCount++;
                        break;
                    }
                case "reason":
                    {
                        var v = Value();
                        if (v == null) return Fail("--reason needs a value.");
                        reason = ReasonText.Sanitize(v);
                        break;
                    }
                case "out":
                    {
                        var v = Value();
                        if (string.IsNullOrWhiteSpace(v)) return Fail("--out needs a path.");
                        outPath = v;
                        break;
                    }
                case "guards":
                    {
                        var v = Value();
                        if (v == null) return Fail("--guards needs a comma separated list.");
                        guards = new List<GuardId>();
                        foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!Enum.TryParse<GuardId>(part, ignoreCase: true, out var g) || !Enum.IsDefined(g))
                                return Fail($"Unknown guard '{part}'.");
                            if (!guards.Contains(g)) guards.Add(g);
                        }
                        break;
                    }
                default:
                    return Fail($"Unknown switch '{raw}'.");
            }
        }

        if (modeCount > 1) return Fail("Use at most one of --duration, --until, --while-process.");
        bool startOptions = start || mode != null || reason != null || guards != null;

        if (help)
            return new CliParseResult(new CliCommand(CliVerb.Help, null, null, false, null, null, false, false), null);

        if (diagnose && restore) return Fail("--diagnose and --restore cannot be combined.");
        if (diagnose || restore)
        {
            if (startOptions || stop || minimized)
                return Fail((diagnose ? "--diagnose" : "--restore") + " cannot be combined with other options except --out.");
            return Ok(new CliCommand(diagnose ? CliVerb.Diagnose : CliVerb.Restore, null, null, false, outPath, null, handoff, restoredAfterRestart, handoffSid));
        }

        if (outPath != null) return Fail("--out is only valid with --diagnose or --restore.");
        if (stop && startOptions) return Fail("--stop cannot be combined with start options.");
        if (stop)
            return Ok(new CliCommand(CliVerb.Stop, null, null, minimized, null, null, handoff, restoredAfterRestart, handoffSid));
        if (startOptions)
            return Ok(new CliCommand(CliVerb.Start, mode ?? ActivationSpec.Indefinite, reason, minimized, null, guards, handoff, restoredAfterRestart, handoffSid));
        return Ok(new CliCommand(CliVerb.None, null, null, minimized, null, null, handoff, restoredAfterRestart, handoffSid));

        static CliParseResult Fail(string message) => new(null, message);
        static CliParseResult Ok(CliCommand c) => new(c, null);
    }

    private static readonly HashSet<string> KnownSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "stop", "diagnose", "restore", "minimized", "help", "handoff", "restored-after-restart", "duration", "until",
        "while-process", "reason", "out", "guards",
    };

    private static bool LooksLikeSwitch(string token)
    {
        if (token == "-?" || token == "/?") return true;
        if (token.StartsWith("--", StringComparison.Ordinal)) return token.Length > 2;
        if (token.Length > 1 && token[0] == '/')
        {
            var name = token[1..];
            int eq = name.IndexOf('=');
            if (eq > 0) name = name[..eq];
            return KnownSwitches.Contains(name);
        }
        return false;
    }

    /// <summary>Quote arguments for a Windows command line (CommandLineToArgvW rules).</summary>
    public static string Quote(IEnumerable<string> args) => string.Join(' ', args.Select(QuoteOne));

    public static string QuoteOne(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes); backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
