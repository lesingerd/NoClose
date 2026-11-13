# NoClose

NoClose is a small Windows 10/11 tray application that keeps an **interactive** user session awake, unlocked and logged on while a long-running interactive job runs (a build, a render, a simulation, a data migration, a GUI tool that cannot run as a service).

It replaces older "keep awake" tools that only call `SetThreadExecutionState` with a multimedia style state and then stop working after a day or two. NoClose still uses that call, but adds the other documented mechanisms Windows offers, explains what else could end your session (pending restarts, organisation policies, Remote Desktop limits) and lets you opt in to a few carefully journaled settings changes.

> **Hard rule: NoClose never simulates user input.** No `SendInput`, no `keybd_event`, no mouse movement, no fake key presses. A unit test scans the source tree for such calls and fails the build if one ever appears. Features that Windows ties to real input (for example the "Machine inactivity limit" security policy) are reported, not worked around.

NoClose is a normal desktop application that runs in your session. It is **not** a service and installs nothing except an optional scheduled task for "Start at logon".

---

## What it does

While NoClose is **Active** it:

1. Calls `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)` (plus `ES_AWAYMODE_REQUIRED` when you enable away mode and the machine supports it) from one dedicated thread, and never sets `ES_USER_PRESENT`.
2. Creates separate Windows power requests (`PowerCreateRequest` / `PowerSetRequest`) for *display required*, *system required* and *execution required* with the reason text `NoClose: <your reason>`. You can see them with `powercfg /requests`.
3. Every 45 seconds (configurable, 15 to 300) pulses the idle timers and re-asserts the state. Every tenth tick it reads the system's execution state (`CallNtPowerInformation`) to verify Windows actually reflects the request, and rebuilds the requests if not.
4. Re-asserts immediately after resume from sleep, session unlock, console/remote connect, display turning on, AC/DC change and setting-change broadcasts.
5. Records a timeline log (see [Diagnostics](#diagnostics-and-logs)) so that "what happened at hour 48" can be reconstructed.
6. Optionally applies the **guards** below.

Activation modes: indefinitely, for a duration, until a time, or while a given process (name or PID) is running.

The tray icon is grey (idle), green (active) or amber (a high-severity finding or a degraded engine). Left-click shows or hides the status window, double-click starts or stops, right-click opens the menu.

---

## Guards

A guard is one mechanism that NoClose can turn on. Every guard that changes anything is **opt-in**, **labelled**, **journaled to disk before it is applied**, **restored on stop/exit** and **restored at the next start if NoClose crashed**. Settings-changing guards ask for a one-time confirmation that lists exactly what will change.

| Guard | Default | Admin? | What it changes | Restored by |
|---|---|---|---|---|
| **Keep awake (power requests)** | on | no | Nothing on the system. Process-local execution state and power requests only. | Releasing the requests |
| **Screen saver off (this session)** | off (opt-in, one-time confirmation) | no | The *runtime* "screen saver active" flag of the current logon session via `SystemParametersInfo` with no profile update. Your saved screen saver settings are not touched. Skipped completely if a screen saver Group Policy exists (and undone first if a policy appears while it is applied). If the screen saver is switched on again by you or by management the guard shows **Changed externally** and leaves it alone. NoClose's display request already keeps the screen saver from starting, so this guard is not needed for keep-awake. | Setting the flag back (journaled, session scoped) |
| **Power plan (temporary copy)** | off | yes | Duplicates your active power plan, sets sleep / hibernate / unattended sleep / display timeouts to *never* and the "allow apps to keep the PC awake" settings to *on* **in the copy**, and activates the copy. Optional extras are individual checkboxes (console lock display timeout, away mode policy). Your own plan is never edited. Items controlled by a power Group Policy are skipped. | Re-activating your original plan and deleting the copy |
| **Windows Update active hours (18 h)** | off | yes | The three values behind *Settings > Windows Update > Active hours*: `ActiveHoursStart`, `ActiveHoursEnd`, `SmartActiveHoursState` under `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings`. Sets an 18 hour window starting now (the maximum Windows allows). It can offer, never silently perform, a shift forward every hour. | Writing the original values back (or deleting values that did not exist) |
| **Ask before logoff/shutdown** | off | no | Nothing is changed. Registers a shutdown block reason on the (always visible, possibly minimized) status window and answers `WM_QUERYENDSESSION` with "wait" for ordinary logoff/shutdown/restart requests so Windows shows *"NoClose is keeping this session open"*. You can always choose *Shut down anyway*. | Removing the block reason |
| **Silent media session** | off (opt-in, no confirmation needed) | no | Nothing is changed (no settings, no journal entries, no input). Keeps a real but silent audio playback stream open on the default output device (`eRender`/`eMultimedia`, WASAPI shared mode, every buffer flagged *silent*), so Windows and audio drivers see the same "media is playing" state a media player creates. It appears as *NoClose silent media session* in the volume mixer. Runs on its own background thread, follows the default device when it changes and reopens with a 2 s / 5 s / 15 s / 60 s backoff when the device or the audio service goes away. With no audio output device the guard shows **Degraded** and keeps retrying; NoClose never fails because of it. | Closing the stream |

The end-session guard is best effort and always user-overridable. It cannot and does not stop: forced restarts after an update deadline, Remote Desktop time limits, administrator logoffs, critical shutdowns.

**Why the silent media session exists.** Some lock and idle paths, and some drivers, treat real media playback as "the user is busy" even when they ignore ordinary power requests. The silent stream gives them that signal. It **complements** the power requests and does not replace them (keep the first guard on), and it does **not** defeat Remote Desktop idle limits, organisation lock policies, forced logoff or forced restarts. The tray menu's Diagnostics window reports whether the stream is active; `NoClose --diagnose` (a separate process) cannot see the running instance's stream, so it reports the state as unknown and, read-only, whether an audio output endpoint exists (`R-MEDIA`).

---

## What NoClose deliberately does NOT change

NoClose **never writes** anything under a `...\Policies\...` registry key, nor MDM/Intune state (`PolicyManager`, `Enrollments`), nor security policy, nor `powercfg /requestsoverride`, nor Remote Desktop Services settings. This is enforced in code (`RegistryWriteGuard`: an allow-list of exactly three Windows Update values; everything else, and every policy path, throws) and by unit tests.

Why:

* Those settings express a decision by the machine owner or your organisation. Silently overriding them would be circumventing a security control, may violate your acceptable-use rules, and would usually be reverted by policy refresh anyway.
* "Lock after N minutes of inactivity", smart-card removal behaviour, Remote Desktop idle/disconnect limits and update deadlines exist precisely to end unattended sessions. The only way to defeat the inactivity timers would be to fake input, which NoClose will not do.

Instead NoClose **detects and reports** them (see diagnostics) with the policy name as it appears in Group Policy Editor / Intune and a note that the machine owner or IT administrator can change it through proper channels.

NoClose also reads, but never changes, the "require sign-in on wake" setting.

---

## Administrator rights

* The manifest requests `asInvoker`: NoClose never prompts by itself, and every command line verb (`--stop`, `--restore`, `--diagnose`, `--help`) runs unelevated without a UAC prompt. When you enabled (and confirmed) an administrator-only guard and the account is an administrator with a filtered token, the GUI asks for elevation once at start (UAC consent); cancelling keeps NoClose running unelevated. The *Start at logon* task runs with the highest available run level, so it starts elevated without a prompt.
* **Not needed for:** the keep-awake engine, the screen saver guard, the end-session guard, the silent media session, diagnostics (except the `powercfg /requests` section).
* **Needed for:** the power plan guard, the active hours guard, creating/removing the "Start at logon" scheduled task, and restoring `HKLM` / power-plan journal entries.
* If you are not elevated the admin guards are greyed out with "Requires administrator — use Restart elevated". *Restart elevated* (tray menu or status window, offered only to an administrator account) first restores everything, then relaunches itself with `runas`. The command line of the new copy is built from the CURRENT state (running activation with its remaining time, applied guards, `--minimized`) plus `--handoff <your SID>`, not from how NoClose was originally launched. If you cancel the UAC prompt NoClose carries on as before. A standard user who would have to type another account's credentials is not offered this: the elevated copy would run as that other account (different settings, journal and command pipe), so NoClose says it requires an administrator account.
* NoClose is not a service and does not try to become one: a service has no interactive session to keep alive.

---

## Command line

```
NoClose.exe [options]

  --start                    start keeping awake (indefinitely unless a mode is given)
  --stop                     stop, restore every guard (the app keeps running in the tray)
  --duration <d>             90m, 8h, 1h30m, 2d, 01:30 (hh:mm), 1.5h   (1 minute .. 30 days)
  --until <t>                HH:mm (local; tomorrow if already passed) or yyyy-MM-ddTHH:mm
  --while-process <name|pid> run while the process (image name, .exe optional, or PID) exists
  --reason "<text>"          reason text (max 120 characters, default "Long-running task")
  --guards <list>            per-run override: ExecutionState,ScreenSaver,PowerPlan,ActiveHours,EndSession,SilentMedia
                             (opt-in guards still need to have been confirmed once in the window)
  --minimized                start with the status window minimized
  --diagnose                 read-only scan, prints a report and writes it (to --out if given, else the default file)
  --restore                  apply and clear any stale restore journal
  --out <path>               output file for --diagnose / --restore
  --help, -?                 usage
```

* Switches are case-insensitive; `/switch` works like `--switch`.
* `--duration`, `--until` and `--while-process` imply `--start`; at most one of them. `--stop` cannot be combined with start options. `--diagnose` and `--restore` accept only `--out`.
* Only one instance runs per session. A second invocation forwards its command over a per-user named pipe to the running instance, prints the answer and exits.
* Exit codes: `0` ok, `1` partial failure (for example the watched process is not running), `2` usage error, `3` the running instance did not answer.
* Output works from a console even though NoClose is a Windows-subsystem program (it attaches to the parent console).

Examples:

```
NoClose.exe --duration 36h --reason "Nightly migration"
NoClose.exe --while-process build.exe --guards ExecutionState,ScreenSaver
NoClose.exe --until 07:30
NoClose.exe --stop
NoClose.exe --diagnose --out C:\temp\noclose-report.txt
```

---

## How restore works

Every change goes through one pattern: **read the original, write it to the journal (atomic, write-through), make the change, read it back to verify** (if verification fails the change is undone immediately).

* The journal is `%LocalAppData%\NoClose\restore-journal.json` (or `%ProgramData%\NoClose\restore-journal.json` when elevated: writable only by Administrators and SYSTEM, readable by Users so an unelevated start can see it and offer *Restart elevated*).
* Normal restore: *Stop*, activation end, tray *Exit*: all guards are restored in reverse order and the journal is deleted when every entry is restored.
* Session end (`WM_ENDSESSION`) and unhandled exception: a synchronous, journal-driven emergency restore with a 4 second budget; after a session end NoClose marks the activation ended and exits (the saved activation lets a restart after servicing resume it with its remaining time only; an expired or unreadable one is not resumed). Restart-elevated uses the normal Stop/guard restore path.
* **After a crash or power loss:** at the next start NoClose finds a journal whose owning process is not running any more and restores it before doing anything else, then tells you with a balloon and a log entry. Entries that need administrator rights are kept and the status window says *Restart elevated* if you started unelevated. `NoClose.exe --restore` does the same on demand; forwarded to a running instance it restores only journals of other (dead) runs and leaves the running activation alone.
* Restore rules: newest change is undone first (by the order the entries were recorded, not by wall clock time); a registry value that was changed by someone else after NoClose set it is left alone (with a message); a leftover `NoClose temporary` power plan from a crash is deleted at the next elevated start when nothing refers to it; entries still `Pending` (journal written, change unconfirmed) are restored too; the screen saver runtime flag is only restored in the same logon session (a new logon already reset it); if the original power plan has been deleted Balanced is activated; if you switched power plans yourself in the meantime NoClose will not override your choice and just removes its temporary copy when it can.
* NoClose never "fights" outside changes: if a guarded value is changed externally the guard shows **Changed externally** and is not re-applied.

---

## Diagnostics and logs

`NoClose.exe --diagnose` (or *Diagnostics...* in the tray menu) is **read-only** and produces a text report, saved by default to `%LocalAppData%\NoClose\diagnostics-<timestamp>.txt`. Rules cover, among others:

* a pending restart (Windows Update Agent, CBS, `RebootRequired`) and your configured active hours
* Windows Update deadline / WSUS / MDM policies that restart regardless of active hours
* Machine inactivity limit, MDM `MaxInactivityTimeDeviceLock`, screen saver policies, Dynamic Lock, smart card removal behaviour, "require sign-in on wake"
* Remote Desktop session state and Session Host time limits (machine policy > user policy > listener precedence)
* Modern Standby on battery, lid/power button actions, sleep/display timeouts, "allow apps to keep awake" settings
* `powercfg /requestsoverride` entries naming NoClose and, when elevated, the current `powercfg /requests`

Findings are sorted by severity. Settings controlled by your organisation are marked as such, with the policy name and a note to talk to the machine owner or IT administrator.

The **timeline log** (`%LocalAppData%\NoClose\logs\noclose-yyyyMMdd.log`, kept 14 days) records start/stop, every guard result, session lock/unlock/connect events, power broadcasts, display on/off, user presence, end-session queries with decoded flags, engine ticks (every 15 minutes) with the time since last input, diagnostics changes (re-checked every 15 minutes while active, hourly while idle, and after resume or unlock) and the last line of the previous run, so you can see what happened right before a lock or logoff. Open it from the tray menu (*Open log folder*).

Settings live in `%AppData%\NoClose\settings.json`.

---

## Troubleshooting: "the session still locks after 2 days"

Work through this list; each item is something NoClose either reports or cannot influence.

1. **Run `NoClose.exe --diagnose`** and read every `HIGH` and `MEDIUM` finding first.
2. **Look at the log around the time it happened.** Was NoClose still Active (`Tick` lines)? Was there a `WTS_SESSION_LOCK` and, just before it, a `WM_QUERYENDSESSION`, a power broadcast, or an engine `Degraded` warning?
3. **A restart was pending** (`R-WU-REBOOT`). Restart at a convenient time *before* starting the job. Enable the Active hours guard (elevated) to widen the window to 18 hours. It cannot cover 24 hours.
4. **An update deadline or WSUS/MDM policy** (`R-WU-POLICY`): after the deadline Windows restarts regardless of active hours. Only your IT administrator can change this.
5. **Machine inactivity limit / MDM device lock** (`R-INACT`, `R-MDM-LOCK`): your organisation wants idle sessions locked, based on real input. NoClose will not fake input; ask IT for an exemption for this machine if the job is legitimate.
6. **Remote Desktop** (`R-RDP`, `R-RDP-LIMITS`): idle/disconnect/active limits and "end session when time limits are reached" are enforced by the host and ignore power requests. A disconnected RDP session also does not count as "signed in" for update restart avoidance. Prefer the console session, or ask IT to raise the limits.
7. **Modern Standby on battery** (`R-MS`): Windows ends stay-awake requests five minutes after the sleep timeout. Keep the device plugged in.
8. **Windows is ignoring NoClose** (`R-OVR`, `R-ALLOW`): a `powercfg /requestsoverride` entry or a power plan / policy setting that disables app requests. The status window shows *Degraded*. Use the Power plan guard if the plan (not a policy) is the cause.
9. **Smart card removal, Dynamic Lock, screen saver policy** (`R-SCARD`, `R-DYNLOCK`, `R-SS-POL`): policies or a paired phone leaving range can lock the PC regardless.
10. **Lid close / power button** (`R-LID`): the PC will sleep regardless of NoClose.
11. **Another tool or user logged off / restarted the machine**: check the Windows event log (System: Kernel-Power, User32 1074, Winlogon) for who initiated it. Turn on *Ask before logoff/shutdown* so ordinary requests prompt you.
12. **NoClose itself ended**: if the log ends without a stop line the process was killed (crash, Task Manager, reboot). Enable *Start at logon*, and the restore journal repairs any changed settings next start.

---

## Build instructions

Requirements: .NET SDK 10 (`net10.0-windows`, Windows Forms), Windows 10/11 x64. No NuGet packages are used by the app; the test project uses xUnit.

> **Visual Studio note:** Visual Studio 2022 does not ship the .NET 10 SDK targets. Open the solution in a newer Visual Studio (2026) or simply use the .NET CLI, which works with any editor.

```
dotnet build NoClose.sln -c Release
dotnet test  NoClose.sln -c Release
```

The executable is `src\NoClose\bin\Release\net10.0-windows\NoClose.exe`. To publish a self-contained single folder:

```
dotnet publish src\NoClose\NoClose.csproj -c Release -r win-x64 --self-contained false -o publish
```

The manifest requests `asInvoker`, so command line verbs never show a UAC prompt. NoClose is a WinExe: cmd.exe does not wait for it, so scripts that need the exit code use `start /wait NoClose.exe ...` or `Start-Process -Wait`. When stdout is redirected (scheduler, CI, `Start-Process -RedirectStandardOutput`) the output goes to that redirection.

Repository layout:

```
NoClose.sln
src/NoClose          the application (Core = pure logic, Platform = Win32, Engine, Guards, Journal, Logging, Ui)
tests/NoClose.Tests  xUnit tests (pure logic, interop layout, source-tree policy checks)
docs/DESIGN.md       the implementation specification
```

---

## License

No license file is included yet; add one before redistributing.
