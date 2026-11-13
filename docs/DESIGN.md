# NoClose v1 — Implementation Specification

Status: authoritative spec for the v1 implementation. Implement exactly what is written here. Where this document says "MUST", it is a requirement; "SHOULD" is a strong default; anything not specified is left to the engineer's judgement but must not contradict this file.

---

## 0. Purpose and principles

NoClose is a Windows 10/11 tray application that keeps the interactive user session awake (no idle sleep, no idle display-off, no idle screen saver) while a long-running interactive job runs, using only documented, non-input mechanisms, and that explains clearly what else might end the session (pending restarts, organisation policies, remote-desktop limits) so that the user can deal with it through proper channels.

Principles (all MUST):

1. **No input simulation, ever.** The codebase must not reference `SendInput`, `keybd_event`, `mouse_event`, `SetCursorPos`, `SendMessage(WM_KEY*/WM_MOUSE*)` to other windows, or any equivalent. A unit test (`NoInputSimulationTests`) greps the source tree for these identifiers and fails if found.
2. **Organisation policy is read-only.** NoClose never writes, deletes or overrides anything under a `...\Policies\...` registry key, MDM/Intune state (`HKLM\SOFTWARE\Microsoft\PolicyManager`, `HKLM\SOFTWARE\Microsoft\Enrollments`), security policy, or `powercfg /requestsoverride` entries. It only detects such settings and explains them. This is enforced in code by `RegistryWriteGuard` (§9.4).
3. **Every change to user/machine settings is opt-in, labelled, journaled before applying, restored on exit and restored after a crash.**
4. **Honest UI.** When something cannot be prevented (forced restarts, policy-enforced locks, RDP idle limits), the UI says so plainly and names who can change it (the machine owner / IT administrator).

---

## 1. Solution layout

```
NoClose.sln
src/NoClose/NoClose.csproj                 (WinExe, net10.0-windows, UseWindowsForms)
src/NoClose/app.manifest
src/NoClose/Resources/noclose-active.ico, noclose-idle.ico, noclose-warn.ico
src/NoClose/
  Program.cs
  AppHost.cs
  Core/          (pure logic: no P/Invoke, no Registry, no WinForms; unit-tested)
    Cli/CliParser.cs, CliCommand.cs, DurationParser.cs
    Activation/ActivationSpec.cs, ActivationPolicy.cs
    Journal/RestoreJournal.cs, JournalEntry.cs, JournalSerializer.cs, RestorePlanner.cs
    Diagnostics/IMachineReader.cs, DiagnosticRule.cs, DiagnosticFinding.cs,
                DiagnosticsEngine.cs, Rules/*.cs, DiagnosticsTextFormatter.cs
    Settings/AppSettings.cs, SettingsSerializer.cs
    Guards/GuardId.cs, GuardState.cs
    Text/ReasonText.cs
    Time/IClock.cs
  Platform/      (Windows-specific implementations)
    Interop/Kernel32.cs, User32.cs, PowrProf.cs, Wtsapi32.cs, Advapi32.cs, Native*.cs (structs/consts)
    PowerSchemeAccessor.cs, RegistryAccessor.cs, WindowsMachineReader.cs
    Elevation.cs, SingleInstance.cs, ConsoleAttach.cs, StartupTask.cs, ProcessWatcher.cs
  Engine/KeepAwakeEngine.cs, EngineCommand.cs, EngineStatus.cs
  Guards/IGuard.cs, GuardContext.cs, GuardManager.cs,
         ExecutionStateGuard.cs, ScreenSaverGuard.cs, PowerPlanGuard.cs,
         ActiveHoursGuard.cs, EndSessionGuard.cs
  Journal/JournalStore.cs, RestoreService.cs, RegistryWriteGuard.cs
  Logging/EventLog.cs (class name `TimelineLog`), LogSink.cs
  Ui/TrayController.cs, StatusForm.cs, StatusForm.Designer.cs (optional; may build in code),
     DiagnosticsForm.cs, Notifications.cs
tests/NoClose.Tests/NoClose.Tests.csproj   (xUnit; references src/NoClose project)
docs/DESIGN.md
```

### 1.1 NoClose.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationIcon>Resources\noclose-active.ico</ApplicationIcon>
    <AssemblyName>NoClose</AssemblyName>
    <RootNamespace>NoClose</RootNamespace>
    <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="NoClose.Tests" />
  </ItemGroup>
</Project>
```

No PackageReference in the app. Tests: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` only; target `net10.0-windows`.

### 1.2 app.manifest

`asInvoker`: the process never prompts for elevation by itself, so every verb (`--stop`, `--status`, `--restore`, `--diagnose`, `--help`) works from schedulers, CI and `CreateProcess` callers without a UAC prompt (a `highestAvailable` manifest fails those with error 740 and makes read-only verbs prompt). Elevation is requested on demand, only for the GUI instance, when an administrator-only guard the user enabled needs it (see 2.3); the start-at-logon task runs with `RunLevel=HighestAvailable` so it starts elevated without a prompt.

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="NoClose.app"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /> <!-- Win10/11 -->
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
```

Also call `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` in `Program.Main` before any window.

---

## 2. Process lifecycle

### 2.1 Program.Main (STAThread)

Order:

1. Parse CLI with `CliParser.Parse(args)` → `CliCommand` (or `CliError`). On error: attach console (§2.4), print usage, exit code 2.
2. If the command is `--diagnose`: run diagnostics without UI and without single-instance (read-only), print/write, exit 0. (Does not touch the mutex.)
3. If the command is `--restore`: acquire the single-instance mutex. If another instance owns it, forward `restore` over the pipe (§2.3) and exit with the result code. Otherwise run `RestoreService.RestoreFromDisk()` standalone, print a summary, exit 0 (1 if any entry failed).
4. Single-instance (§2.2). If not the first instance: forward the command (or `show` when no args) and exit 0.
5. Install exception handlers: `Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)`, `Application.ThreadException`, `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` (log only). Fatal handlers call `AppHost.EmergencyRestore()` (synchronous, best effort, swallow exceptions per entry) then log and exit.
6. `SetProcessShutdownParameters(0x100, 0)` (log failure, continue). Windows notifies HIGHER levels first (`0x3FF` is the first application level, `0x100` the last), so the low level keeps the execution state, power requests and guards in place until the other applications (the protected work) have been told about the session end. On `WM_ENDSESSION(TRUE)` the process restores through the journal, marks the activation ended (keeping `lastRunActive` so a Restart Manager restart can resume) and then exits (`Environment.Exit(0)`), because `WM_ENDSESSION` does not terminate a process that was only asked to close for servicing (`ENDSESSION_CLOSEAPP`).
7. `RegisterApplicationRestart("--restored-after-restart", RESTART_NO_CRASH | RESTART_NO_HANG)` — flags `RESTART_NO_CRASH=1, RESTART_NO_HANG=2`; only if `settings.RegisterForRestart` (default true).
8. Create `AppHost`, call `AppHost.Start(cliCommand)`, then `Application.Run(appHost.ApplicationContext)`.
9. After `Run` returns: `AppHost.Shutdown()` (normal restore path), release mutex, exit 0.

### 2.2 Single instance

- Mutex name: `Local\NoClose.SingleInstance.{6D1F1D7B-3C2B-4C5E-9E0A-2B1E7C9A4F10}` (`Local\` = per session).
- `new Mutex(initiallyOwned: true, name, out bool createdNew)`. Handle `AbandonedMutexException` as "acquired".
- First instance hosts a named pipe server `NoClose.Cmd.<sessionId>.<userSid>` (`System.IO.Pipes.NamedPipeServerStream`, `PipeDirection.InOut`, `PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`) on a background task; one line of UTF-8 JSON request, one line response.
- Request: `{"verb":"start|stop|show|restore|status","args":[...original args...]}`. Response: `{"ok":true,"message":"..."}`.
- The server marshals the command to the UI thread via `SynchronizationContext.Post` and then to the engine.
- Client: connect with 3 s timeout. If the pipe cannot be reached, print "NoClose is already running but did not respond" and exit 3.
- Elevation mismatch: if the first instance is not elevated and the second is (or vice versa), forwarding still works because `CurrentUserOnly` checks the user SID, not integrity. The pipe server is created with a `PipeSecurity` whose DACL grants the current user SID `FullControl`. No explicit mandatory label is added: an unlabeled object has an implicit Medium / no-write-up label, so a medium-integrity client of the same user can talk to a high-integrity server (an attempt to set a label/SACL through `PipeSecurity` fails with "A required privilege is not held" without SeSecurityPrivilege; if connect still fails with access denied the client prints "Use the tray icon of the running instance"). The single-instance mutex has an explicit DACL for the current user; `UnauthorizedAccessException` when opening it means "another instance exists". The pipe server starts right after the mutex is acquired (commands queue until `Start` finished), serves connections concurrently, applies a 5 s read timeout per connection and a 60 s handler timeout, and the client bounds its read (45 s).

### 2.3 Restart elevated

`Elevation.TryStartElevated(string[] args)`: `ProcessStartInfo { FileName = Environment.ProcessPath, Arguments = CliParser.Quote(args), UseShellExecute = true, Verb = "runas" }`. The arguments are composed from the CURRENT state by `ActivationArgs.ForElevatedCopy` (never from the launch arguments): `--minimized` if it applied, then `--start <mode> --reason <text>` when active (a Duration passes the remaining time), `--guards <applied guards>`, and `--handoff <user SID>`. "Restart elevated" is only offered (`ElevationRules.CanRestartElevated`) to an administrator with a limited token (TokenElevationType = limited): a standard user would get the over-the-shoulder credential prompt and the elevated copy would run as another account (other HKCU, journal, pipe, settings), so it is refused with "requires an administrator account". The elevated copy refuses to run when its user SID differs from the `--handoff` SID. At startup an instance that is not elevated, can elevate and has an enabled+acknowledged admin-only guard asks for elevation once (cancelling UAC keeps the non-elevated instance running). Before starting: the current instance runs its full normal shutdown (restore), releases the mutex, then starts the new process. If the user cancels UAC (Win32Exception NativeErrorCode 1223), re-acquire nothing; simply keep running (do the elevation launch before releasing: order is (1) restore all guards, (2) close pipe server, (3) release mutex, (4) `Process.Start`; on 1223 re-acquire mutex and re-apply guards).

`Elevation.IsElevated`: `new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)`.

### 2.4 Console output for a WinExe

`ConsoleAttach.TryAttachParent()`: first `GetStdHandle(STD_OUTPUT_HANDLE)`; if it is valid and `GetFileType != FILE_TYPE_UNKNOWN` (redirected by a scheduler, CI, `Start-Process -RedirectStandardOutput`, ...) use it directly through `Console.OpenStandardOutput()` without `AttachConsole`. Otherwise `AttachConsole(ATTACH_PARENT_PROCESS = unchecked((uint)-1))` (a failure is traced with its Win32 error). Re-open stdout `var w = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }; Console.SetOut(w);` and print a leading `Environment.NewLine` only when stdout is a real console (`FILE_TYPE_CHAR`; the parent prompt is already printed). If there is no usable output and an `--out <file>` was given, write only to file. A WinExe is not waited for by cmd.exe: callers that need the exit code use `start /wait` or `Start-Process -Wait`.

---

## 3. CLI

Grammar (case-insensitive switches; `/x` is accepted as an alias of `--x`):

| Switch | Meaning |
|---|---|
| `--start` | Start keeping awake (with optional mode switches below). Default mode: indefinite. |
| `--stop` | Stop keeping awake and restore all guards (app keeps running in tray). |
| `--duration <d>` | Implies `--start`. Duration: `90m`, `8h`, `1h30m`, `2d`, `45s`, `01:30` (hh:mm), `1.5h`. Range 1 min..30 days. |
| `--until <t>` | Implies `--start`. `HH:mm` (24h, local; if already passed today → tomorrow), or ISO `yyyy-MM-ddTHH:mm`. |
| `--while-process <name\|pid>` | Implies `--start`. All-digits → PID; else image name, `.exe` optional, case-insensitive. |
| `--reason "<text>"` | Reason text; max 120 chars, control chars stripped. Default "Long-running task". |
| `--diagnose` | Read-only scan; output to console and/or `--out`. |
| `--out <path>` | Output file for `--diagnose`/`--restore`. |
| `--restore` | Apply and clear any stale restore journal. |
| `--minimized` | Start with status window minimized (not hidden; see §8.3). |
| `--guards <list>` | Optional override for this run: comma list of guard ids (§6). Opt-in guards listed here still require that the user has confirmed them once in the UI (`settings.Guards[id].Acknowledged == true`); otherwise ignored with a log line (the token `SilentMedia` needs no confirmation: it changes nothing). |
| `--help`, `-?` | Usage. |
| `--handoff [<sid>]`, `--restored-after-restart` | Internal. `--handoff` carries the SID of the user who asked for elevation. |

A value that starts with `--` (or is a known `/switch`) is never consumed as the value of the previous switch: `--reason --stop` is the error "--reason needs a value.".

Rules: at most one of `--duration/--until/--while-process`; `--stop` conflicts with start options; `--diagnose` and `--restore` are exclusive with everything except `--out`. Unknown switch → error.

`CliCommand` record:

```csharp
public enum CliVerb { None, Start, Stop, Diagnose, Restore, Help }
public sealed record CliCommand(CliVerb Verb, ActivationSpec? Activation, string? Reason,
    bool Minimized, string? OutPath, IReadOnlyList<GuardId>? GuardOverride, bool Handoff, bool RestoredAfterRestart, string? HandoffSid = null);
```

`DurationParser.TryParse(string, out TimeSpan, out string error)` — pure; regex `^(?:(\d+(?:\.\d+)?)d)?(?:(\d+(?:\.\d+)?)h)?(?:(\d+(?:\.\d+)?)m)?(?:(\d+)s)?$` (at least one group), or `^(\d{1,3}):(\d{2})$`. `UntilParser.TryParse(string, DateTime nowLocal, out DateTime targetLocal)`.

Exit codes: 0 ok, 1 partial failure, 2 usage error, 3 forward failed.

---

## 4. Activation modes

```csharp
public enum ActivationKind { Indefinite, Duration, Until, WhileProcess }
public sealed record ActivationSpec(ActivationKind Kind, TimeSpan? Duration = null,
    DateTime? UntilLocal = null, string? ProcessName = null, int? ProcessId = null);
```

`ActivationPolicy` (pure, uses `IClock`): `Begin(spec, now)` computes `DeadlineUtc` for Duration/Until; `ShouldStop(now, bool processAlive)` → bool + reason string. For WhileProcess, `processAlive` is supplied by `ProcessWatcher`.

`ProcessWatcher` (Platform): for PID, `Process.GetProcessById` once and use `WaitForExitAsync`-equivalent via `process.EnableRaisingEvents` + `Exited`; for names, poll `Process.GetProcessesByName(name)` every 5 s. If the process is not running at activation time: show a warning balloon and do not start (CLI exits 1 if it was the first instance). Grace: stop only after 2 consecutive "not running" polls.

The deadline check runs on the engine thread tick (§5) and a 1 s UI timer for countdown display. When the activation ends: engine stops, all guards restore, balloon "NoClose stopped: <reason>", log entry.

---

## 5. Keep-awake engine

### 5.1 Threading

`KeepAwakeEngine` owns ONE dedicated foreground-false thread: `new Thread(Run) { IsBackground = true, Name = "NoClose.Engine", Priority = ThreadPriority.AboveNormal }`. It lives for the whole process. All `SetThreadExecutionState` and `PowerCreateRequest/PowerSetRequest/PowerClearRequest` calls happen ONLY on this thread. No other code in the process may call STES (enforced by code review + a test that greps for `SetThreadExecutionState` outside `Kernel32.cs`/`KeepAwakeEngine.cs`).

Loop: a `BlockingCollection<EngineCommand>` drained with `TryTake(out cmd, timeout)` where timeout = time until next tick. Commands:

```csharp
abstract record EngineCommand;
record EngineStart(string Reason, bool AwayMode) : EngineCommand;
record EngineStop(string Why) : EngineCommand;
record EngineReassert(string Trigger) : EngineCommand;   // resume, unlock, display-on, settings change
record EngineQuery(TaskCompletionSource<EngineStatus> Reply) : EngineCommand;
record EngineShutdown(ManualResetEventSlim Done) : EngineCommand;
```

Status changes are published via `event Action<EngineStatus> StatusChanged` (raised on the engine thread; UI marshals with `BeginInvoke`).

### 5.2 Assert

On `EngineStart`:

1. `flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED` (+ `ES_AWAYMODE_REQUIRED` if `AwayMode && caps.IsS3Capable && !caps.AoAc`; otherwise log "Away mode not applicable on this machine (Modern Standby)" and omit). NEVER include `ES_USER_PRESENT`.
2. `prev = SetThreadExecutionState(flags)`; `prev == 0` → failure: log Error with `Marshal.GetLastPInvokeError()` (STES does not set last error reliably; log anyway), status `Degraded`.
3. Create one request handle per type (separate handles, so one failure doesn't affect others): `PowerCreateRequest(ref REASON_CONTEXT)` with `SimpleReasonString = "NoClose: " + reason`. The string pointer must remain valid for the call only (Windows copies it); allocate with `Marshal.StringToHGlobalUni` and free after the call. Then `PowerSetRequest(h, type)` for `DisplayRequired(0)`, `SystemRequired(1)`, `ExecutionRequired(3)`, and `AwayModeRequired(2)` under the same condition as above. `INVALID_HANDLE_VALUE` (−1) on create failure; log `GetLastError`.
4. Record `assertedAtUtc`, status `Active`.

### 5.3 Periodic tick

Interval: `settings.ReassertSeconds` (default 45, clamp 15..300). On each tick while active:

1. Pulse: `SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)` (no ES_CONTINUOUS — resets the power manager's idle timers).
2. Re-assert continuous state: `SetThreadExecutionState(flags)` (same flags as 5.2).
3. Every 10th tick (or on `EngineReassert`): self-check (5.4).
4. Activation deadline check (§4).
5. Ask `GuardManager.ReapplyDue()` via `UiDispatcher.Post` (guards that must run on the UI thread do so there; see §6).
6. Log a `Tick` line at most once per 15 minutes (throttle) unless something changed.

On `EngineReassert`: close and re-create all power request handles (clear → close → create → set), then redo step 2. Triggered by: PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND, WTS_SESSION_UNLOCK, WTS_CONSOLE_CONNECT / WTS_REMOTE_CONNECT, display state transitions to On, AC/DC change, and settings changes.

### 5.4 Self-check

`CallNtPowerInformation(SystemExecutionState = 16, IntPtr.Zero, 0, out uint state, 4)` → returns `EXECUTION_STATE` bitmask for the system. Expect `ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED` bits present. If missing → log Warning "Execution state not reflected by system; re-creating" and run the full reassert. Status records `LastVerifiedUtc` and `VerifyResult`.

Also at activation and every 30 min: read the PowerRequestOverride key (§7 rule R-OVR) and the three "allow requests" power settings; if any would cause Windows to ignore NoClose, mark status `Degraded` with the finding text (do not change them).

### 5.5 Stop

`EngineStop`: `PowerClearRequest(h, type)` for each set type, `CloseHandle(h)`, then `SetThreadExecutionState(ES_CONTINUOUS)` to clear. Status `Idle`.

`EngineShutdown`: Stop if active, set `Done`, exit loop. `AppHost.Shutdown` waits up to 2 s; when the engine thread is not running (already shut down or dead) `Shutdown` returns immediately.

Robustness: every loop iteration (queue handling and `Tick`) runs in its own try/catch; a failing iteration is logged, published as `Degraded` ("Keep-awake engine error") and the loop continues. If the thread dies anyway, the engine publishes `Degraded` ("engine stopped unexpectedly"), and the next `Post` (or `Begin`) starts a fresh thread and re-runs `EngineStart` with the last reason/away flag when it had been active. `SetThreadExecutionState` is imported without `SetLastError`: it returns NULL on failure and documents no Win32 error, so the log says "returned NULL (flags=...)". The UI shows "Not keeping awake" (amber) instead of green "Active" when an activation runs while the engine is `Idle` (power-request guard off, engine dead); `ExecutionStateGuard.Reapply` restarts an idle engine and reports `Error`. The environment check (R-OVR, R-ALLOW) only counts findings of the current power source (AC or battery) and is re-run on power source changes.

---

## 6. Guards

### 6.1 Interface

```csharp
public enum GuardId { ExecutionState, ScreenSaver, PowerPlan, ActiveHours, EndSession, SilentMedia }
public enum GuardStatus { Off, Applied, Unavailable, NeedsElevation, BlockedByPolicy, Reverted, Error, Degraded }

public interface IGuard
{
    GuardId Id { get; }
    string DisplayName { get; }
    string Description { get; }            // plain-language, shown in UI
    bool IsAdminRequired { get; }
    bool ChangesSettings { get; }          // true → journaled, needs one-time acknowledgement
    bool RequiresUiThread { get; }
    GuardAvailability CheckAvailability(GuardContext ctx); // Unavailable/NeedsElevation/BlockedByPolicy + reason
    GuardResult Apply(GuardContext ctx);     // journal BEFORE change
    GuardResult Reapply(GuardContext ctx);   // idempotent; detects reverts
    GuardResult Restore(GuardContext ctx);   // from journal entries owned by this guard
    GuardStatus Status { get; }
}
public sealed record GuardResult(GuardStatus Status, string Message);
public sealed record GuardAvailability(bool CanApply, GuardStatus StatusIfNot, string Reason);
```

`GuardContext`: `JournalStore Journal`, `TimelineLog Log`, `bool IsElevated`, `IMachineReader Reader`, `PowerSchemeAccessor Power`, `RegistryAccessor Registry`, `IClock Clock`, `AppSettings Settings`, `IntPtr StatusWindowHandle`.

`GuardManager`: ordered list `[ExecutionState, SilentMedia, ScreenSaver, PowerPlan, ActiveHours, EndSession]`. `ApplyAll()` applies enabled+acknowledged+available guards in order; `RestoreAll()` in reverse order; each in try/catch, errors logged and surfaced. `ReapplyDue()` calls `Reapply` on each applied guard every `settings.GuardReapplyMinutes` (default 5). All guard calls run on the UI thread (simplest, avoids races with `StatusForm` handle); the ExecutionState guard merely posts commands to the engine.

### 6.2 Common change pattern (for every guard with `ChangesSettings`)

```
original = read current value (or Absent)
journal.Record(entry{guard, kind, target, original, applied=newValue})   // flushed atomically to disk
write newValue
verify by re-reading; if verify fails → restore from that entry immediately, mark Error
```

If the target is policy-controlled (policy value present), the guard does NOT apply that item and reports `BlockedByPolicy` with the policy display name.

`Reapply`: re-read; if the value no longer equals `applied` → status `Reverted`, log "Value changed externally (possibly by policy/management or by the user); NoClose will not re-apply". Do NOT re-write (no fighting). `ScreenSaverGuard` follows the same contract: if the screen saver is on again (the user, management, or a Windows reset) the guard becomes `Reverted` and is left alone; `Reapply` never opens a new journal entry. If a policy appears while applied, the journaled change is undone first and the guard becomes `BlockedByPolicy`. `Restore` always runs when open journal entries exist. Registry restores (journal restore on stop/exit/crash recovery) only write the original back when the current value still equals NoClose's applied value (or already equals the original); a value changed externally is left alone with a message. Restore order is the reverse of append order (wall clock timestamps are display only). ActiveHours compares/updates only the newest open entry per value name and only offers "Shift" while `Applied`.

### 6.3 ExecutionStateGuard (default ON, no admin, no settings change)

Apply → `engine.Post(new EngineStart(reason, settings.AwayMode))`. Restore → `EngineStop`. Reapply → `EngineReassert("guard")`. `ChangesSettings=false` → no journal.

### 6.4 ScreenSaverGuard (default OFF, opt-in with one-time confirmation, no admin, session-only)

Availability: read policy keys `HKCU\Software\Policies\Microsoft\Windows\Control Panel\Desktop` values `ScreenSaveActive`, `ScreenSaverIsSecure`, `ScreenSaveTimeOut`, `SCRNSAVE.EXE`. If any is present → `BlockedByPolicy`: "Your organisation sets the screen saver through Group Policy ('Enable screen saver' / 'Password protect the screen saver' / 'Screen saver timeout' under User Configuration > Administrative Templates > Control Panel > Personalization). NoClose will not change it. NoClose's display request normally keeps the screen saver from starting automatically while active; if your organisation requires the screen to lock, ask the machine owner or IT administrator." Status shows this; the guard does nothing else.

Otherwise:
- original = `SystemParametersInfo(SPI_GETSCREENSAVEACTIVE=0x0010, 0, out BOOL, 0)` plus registry `HKCU\Control Panel\Desktop\ScreenSaveActive` (REG_SZ) for display only.
- Journal entry kind `SpiScreenSaveActive` with original bool. (Even though not persisted, it is journaled so a crash-restart restores runtime state in the same session.)
- `SystemParametersInfo(SPI_SETSCREENSAVEACTIVE=0x0011, FALSE, IntPtr.Zero, 0)` — fWinIni = 0 → NOT written to the profile.
- If it fails with `ERROR_OPERATION_IN_PROGRESS (329)` (machine locked / power-saving), retry on next unlock.
- Restore: `SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, originalBool ? 1u : 0u, IntPtr.Zero, 0)`.
- Reapply: if SPI state differs from FALSE and no policy appeared → set again, log Info once per hour.

### 6.5 PowerPlanGuard (opt-in, admin)

Design: modify a **duplicate** of the active scheme so the user's own plan is never edited.

Apply:
1. `PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid)` → original scheme GUID (free with `LocalFree`).
2. Check policy overrides: for each setting below, if `HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings\<SettingGuid>` has `ACSettingIndex` or `DCSettingIndex` → that item is `BlockedByPolicy` (reported, skipped). Also if `HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings\ActivePowerScheme` exists → whole guard `BlockedByPolicy` ("Select an active power plan" policy).
3. Journal entry kind `PowerActiveScheme` { originalScheme, tempScheme = (to be filled) } written FIRST with `tempScheme=null`.
4. `PowerDuplicateScheme(IntPtr.Zero, ref originalGuid, out IntPtr pNewGuid)`; update journal with tempScheme GUID (re-flush).
5. `PowerWriteFriendlyName(IntPtr.Zero, ref newGuid, IntPtr.Zero, IntPtr.Zero, buffer, size)` → "NoClose temporary (from <original name>)" (UTF-16, size in bytes incl. terminator).
6. For each item (AC and DC): `PowerWriteACValueIndex/PowerWriteDCValueIndex(IntPtr.Zero, ref newGuid, ref sub, ref setting, value)`.
7. `PowerSetActiveScheme(IntPtr.Zero, ref newGuid)`.

Items (configurable checkboxes under the guard, defaults in brackets):

| Item | Subgroup GUID | Setting GUID | New value |
|---|---|---|---|
| Sleep after [on] | SUB_SLEEP `238C9FA8-0AAD-41ED-83F4-97BE242C8F20` | STANDBYIDLE `29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA` | 0 |
| Hibernate after [on] | SUB_SLEEP | HIBERNATEIDLE `9D7815A6-7EE4-497E-8888-515A05F02364` | 0 |
| Unattended sleep timeout [on] | SUB_SLEEP | UNATTENDSLEEP `7BC4A2F9-D8FC-4469-B07B-33EB785AACA0` | 0 |
| Turn off display after [on] | SUB_VIDEO `7516B95F-F776-4464-8C53-06167F40CC99` | VIDEOIDLE `3C0BC021-C8A8-4E07-A973-6B14CBCB2B7E` | 0 |
| Console lock display off [off] | SUB_VIDEO | VIDEOCONLOCK `8EC4B3A5-6868-48C2-BE75-4F3044BE88A7` | 0 |
| Allow system required requests [on] | SUB_SLEEP | `A4B195F5-8225-47D8-8012-9D41369786E2` | 1 |
| Allow display required policy [on] | SUB_VIDEO | `A9CEB8DA-CD46-44FB-A98B-02AF69DE4623` | 1 |
| Allow away mode policy [off] | SUB_SLEEP | `25DFA149-5DD1-4736-B5AB-E8A37B5B8187` | 1 |

(The "require sign-in on wake" setting `CONSOLELOCK 0E796BDB-...` is a sign-in security setting; NoClose only reads and reports it, it is NOT in this list.)

Restore: `PowerSetActiveScheme(original)`; then `PowerDeleteScheme(IntPtr.Zero, ref tempGuid)`; clear journal entry. If the original scheme no longer exists (`PowerReadFriendlyName` fails with ERROR_FILE_NOT_FOUND 2), activate Balanced `381B4222-F694-41F0-9685-FF5BB260DF2E` and log Warning.

Reapply: if `PowerGetActiveScheme` ≠ tempGuid → `Reverted` (the user or management switched plans); do not re-activate.

Errors: `ERROR_ACCESS_DENIED (5)` → status `NeedsElevation`. All PowerProf functions return `uint` Win32 error codes directly (not SetLastError).

### 6.6 ActiveHoursGuard (opt-in, admin)

Uses only the user-facing values that Settings > Windows Update > Active hours writes:
`HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings` — `ActiveHoursStart` (REG_DWORD 0..23), `ActiveHoursEnd` (REG_DWORD 0..23), and `SmartActiveHoursState` (REG_DWORD; 0 = manual, 1 = automatic).

Availability → `BlockedByPolicy` when ANY of these exist (read-only): `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` values `SetActiveHours`, `ActiveHoursStart`, `ActiveHoursEnd`, `ActiveHoursMaxRange`; or `HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Update` values `ActiveHoursStart`, `ActiveHoursEnd`, `ActiveHoursMaxRange`. Message names the GPO "Turn off auto-restart for updates during active hours" / CSP Update/ActiveHoursStart and advises contacting IT.

Apply:
1. Compute window with `ActiveHoursPlanner.Plan(nowLocal, maxRange=18)` (pure): start = current hour, end = (start + 18) % 24. If an activation deadline exists earlier than that, still use 18 h (wider is fine).
2. Journal three entries (`RegistryValue` kind, original value/type or Absent).
3. Write `SmartActiveHoursState=0`, `ActiveHoursStart`, `ActiveHoursEnd` via `RegistryWriteGuard`.
4. Message in UI: "Windows avoids automatic restarts during active hours (max 18 h). This does not prevent restarts after an update deadline set by your organisation, and cannot cover 24 hours."
5. Every hour while applied and if the activation is still running and remaining time > hours left in the window: offer (balloon with button in StatusForm, not automatic) "Shift active hours forward" → re-plan using the same journal entries (originals unchanged).

Restore: write back originals or delete values that were Absent.

Pending-reboot warning (always on, read-only, part of diagnostics and shown as a banner in StatusForm): see rule R-WU-REBOOT.

### 6.7 EndSessionGuard (opt-in, no admin, no settings change)

Purpose: ask Windows to let the user decide before a non-forced logoff/shutdown/restart while a job runs. Best-effort, user-overridable; clearly labelled as such.

- Requires a VISIBLE top-level window: `StatusForm` (§8.3). While the guard is applied, if the form is hidden, it is shown minimized (`WindowState = Minimized; Show()`), never `Visible=false`/`Opacity=0`.
- Apply (UI thread, on `StatusForm.Handle`): `ShutdownBlockReasonCreate(hwnd, "NoClose is keeping this session open: <reason>")`.
- `StatusForm.WndProc` override:
  - `WM_QUERYENDSESSION (0x0011)`: log flags (`ENDSESSION_CLOSEAPP 0x1`, `ENDSESSION_CRITICAL 0x40000000`, `ENDSESSION_LOGOFF 0x80000000`). If guard applied AND `(lParam & ENDSESSION_CRITICAL) == 0` AND `(lParam & ENDSESSION_CLOSEAPP) == 0` → `m.Result = IntPtr.Zero; return;` (no base call). Otherwise `m.Result = 1` and return (allow). Never show dialogs here.
  - `WM_ENDSESSION (0x0016)`: if `wParam != 0` → `AppHost.EmergencyRestore()` synchronously (must finish < 5 s), log "Session ending (flags...)"; else log "End session cancelled".
  - Also set `FormClosing`: if `e.CloseReason == CloseReason.WindowsShutDown` handle the same way (WinForms may route there); the WndProc path runs first so this is a safety net.
- `ENDSESSION_CLOSEAPP` (Restart Manager servicing) is allowed: NoClose restores, and `RegisterApplicationRestart` relaunches it.
- Restore: `ShutdownBlockReasonDestroy(hwnd)`.
- UI copy: "Asks Windows to wait before logoff/shutdown. You can always choose 'Shut down anyway'. Forced restarts (e.g. after an update deadline), remote-desktop time limits and administrator logoffs cannot be blocked."

---

### 6.8 SilentMediaGuard (opt-in, default OFF, no admin, no settings change, no confirmation)

Purpose: keep a real but silent audio playback stream open on the default render endpoint so that Windows and audio drivers see active media playback, the state a media player creates. Some lock and idle paths and drivers respect actual playback while ignoring plain power requests. It **complements** the power requests (§5) and does **not** replace them. It does not defeat Remote Desktop idle limits, organisation lock policies, forced logoff or forced restarts, and the UI and documentation say so. `ChangesSettings=false`, `IsAdminRequired=false`; nothing is journaled, no input is simulated.

Files: `Core/Guards/SilentMedia.cs` (pure: `SilentMediaPhase`, `SilentMediaState`, `SilentMediaSnapshot`, `SilentMediaRules` = backoff schedule, HRESULT classification and texts, state to `GuardStatus` mapping), `Guards/SilentMediaGuard.cs` (guard plus `ISilentMediaSession`), `Platform/SilentAudioSession.cs` (WASAPI session, notification client, `AudioEndpointProbe`), `Platform/Interop/CoreAudio.cs` + `Ole32.cs` (COM interop).

**Interop.** Pure COM, no NuGet. `[GeneratedComInterface]` (source generated, trimming safe) declarations of `IMMDeviceEnumerator` (CLSID_MMDeviceEnumerator via `CoCreateInstance`), `IMMDevice` (`Activate` to `IAudioClient`), `IMMNotificationClient` (implemented by a `[GeneratedComClass]`), `IAudioClient`, `IAudioRenderClient`, `IAudioSessionControl`. Methods are declared in exact vtable order, `[PreserveSig]`, raw HRESULTs. The mix format returned by `GetMixFormat` is freed with `CoTaskMemFree`.

**Stream.** `GetDefaultAudioEndpoint(eRender, eMultimedia)`; `IAudioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 200 ms buffer, 0, mixFormat, null)` with the device's own mix format; `SetEventHandle`; `GetBufferSize`; `GetService(IAudioRenderClient)`; the whole buffer is prefilled by `GetBuffer`/`ReleaseBuffer(frames, AUDCLNT_BUFFERFLAGS_SILENT)`; `Start`. The session display name is set to "NoClose silent media session" through `IAudioSessionControl.SetDisplayName` (best effort).

**Threading.** One dedicated background MTA thread ("NoClose silent media"), neither the UI thread nor the keep-awake engine thread. Event driven loop on `WaitAny(stop, renderEvent, wake)` with a 1 s timeout: on each render event `GetCurrentPadding` then silent `GetBuffer`/`ReleaseBuffer` for the free frames. `Stop()` signals the stop event and joins with a 3 s timeout (a thread that does not stop is abandoned; it is a background thread). `IMMNotificationClient` callbacks (COM threads) only set events and flags; they never call into COM.

**Resilience.** Reopen from scratch (new enumerator, device and client) when: the default `eRender`/`eMultimedia` device changes (`OnDefaultDeviceChanged`), the current device is removed or leaves the active state (`OnDeviceStateChanged`/`OnDeviceRemoved`), any stream call fails (`AUDCLNT_E_DEVICE_INVALIDATED`, `AUDCLNT_E_SERVICE_NOT_RUNNING`, ...), or no render event arrived for 5 s. A device change reopens after 300 ms; failures wait the backoff `2 s, 5 s, 15 s, 60 s (cap)` (`SilentMediaRules.BackoffDelay`), which resets after the stream stayed healthy for 30 s; a device or state change notification ends the wait early (the notification client stays registered for the whole run, across reopen and backoff cycles). Wrappers are created as unique RCWs and released with `ComObject.FinalRelease` on the owning MTA thread; the per-run events are disposed by the thread on exit. No default output device: phase `NoDevice`, status **Degraded** with the message "No audio output device is available. Retrying in N s.", retried forever; no exception ever leaves the thread. `Reapply()` (every `GuardReapplyMinutes`, resume, unlock) calls `EnsureAlive()`: restarts a dead thread, abandons and replaces a thread that made no progress for 15 s, and cuts a backoff short.

**Status.** `GuardStatus.Degraded` (new) covers Starting, RetryWait and NoDevice; `Applied` only while the stream really runs. The audio thread raises `StateChanged`; `AppHost` posts to the UI thread, calls `SilentMediaGuard.Sync()` and `GuardManager.NotifyChanged()`. A guard that stopped (`Restore`) ignores late events. `GuardManager.RestoreGuard` also restores a Degraded guard so the retry loop is always stopped.

**Log (TimelineLog, category `Audio`).** Start, stop, device change, reopen and early retry are logged once per occurrence; repeated failure lines are throttled per (kind, HRESULT) to one per 10 minutes (`Throttle`).

**Wiring.** `GuardId.SilentMedia` (last enum member), constructed second in `GuardManager` (after ExecutionState), settings key `"SilentMedia": { "enabled": false, "acknowledged": false }` (absent in older files, `Normalize()` adds it as OFF), generic row in `StatusForm` (the tray uses the same guard list), CLI token `SilentMedia` (case-insensitive) in `--guards`, and `ActivationArgs.ForElevatedCopy` writes the enum name back so it round-trips. An elevated copy re-applies it when it was applied or degraded.

## 7. Diagnostics (read-only)

### 7.1 Abstraction

```csharp
public interface IMachineReader
{
    object? ReadRegistry(RegHive hive, string subKey, string valueName, RegView view = RegView.Default); // null = absent
    bool KeyExists(RegHive hive, string subKey);
    IReadOnlyList<string> SubKeyNames(RegHive hive, string subKey);
    IReadOnlyList<string> ValueNames(RegHive hive, string subKey);
    uint? ReadPowerValue(Guid scheme, Guid sub, Guid setting, bool ac);
    Guid? ActiveScheme { get; }
    PowerCaps Caps { get; }                 // S3, S4, AoAc (Modern Standby), from GetPwrCapabilities
    bool OnAcPower { get; }                 // GetSystemPowerStatus.ACLineStatus == 1
    SessionInfo Session { get; }            // remote?, protocol, connect state, session id, console session id
    bool? WuaRebootRequired { get; }        // COM Microsoft.Update.SystemInfo, null if unavailable
    string? PowercfgRequests { get; }       // output of "powercfg /requests" (admin only, else null)
    DateTime NowLocal { get; }
}
public enum RegHive { HKLM, HKCU }
public enum Severity { Info, Low, Medium, High }
public sealed record DiagnosticFinding(string RuleId, Severity Severity, string Title, string Detail,
    string? PolicyName, string Advice, GuardId? AddressedBy, bool OrganisationControlled);
public interface IDiagnosticRule { string Id { get; } IEnumerable<DiagnosticFinding> Evaluate(IMachineReader r); }
```

`DiagnosticsEngine.Run(IMachineReader)` runs every rule (each in try/catch → an Info finding "rule failed: …"), sorts by Severity desc. `WindowsMachineReader` is the only Platform implementation; tests use `FakeMachineReader` (dictionary-backed).

`WindowsMachineReader.ReadRegistry` uses `RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)` read-only (`OpenSubKey(path, writable:false)`).

### 7.2 Rules

For every rule that reports an organisation-controlled setting: `OrganisationControlled = true`, `PolicyName` = name as shown in gpedit/Intune, `Advice` = "This is set by the machine owner or your IT administrator. NoClose does not change it. If it interferes with your work, ask them to adjust it through their management tools." `AddressedBy = null` unless a NoClose mechanism legitimately covers it by design.

| Id | Reads | Severity | Finding / advice | AddressedBy |
|---|---|---|---|---|
| R-OVR | `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerRequestOverride\{Process,Service,Driver}` subkeys/values; match `NoClose.exe` (case-insens.) | High if NoClose listed | "Windows is configured to ignore NoClose's power requests (powercfg /requestsoverride)." Advice: an administrator can review with `powercfg /requestsoverride`. NoClose never edits it. | — |
| R-ALLOW | power values A4B195F5 (SUB_SLEEP), A9CEB8DA (SUB_VIDEO), 25DFA149 (SUB_SLEEP) on active scheme AC+DC; also policy `HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings\<guid>\{AC,DC}SettingIndex` | High if 0 | "Power plan tells Windows to ignore app requests to stay awake/keep display on." If policy: name "Allow applications to prevent automatic sleep (plugged in / on battery)" / "Turn on the ability for applications to prevent sleep transitions…" (Power Management > Sleep Settings), org-controlled. Else AddressedBy PowerPlan. | PowerPlan (if not policy) |
| R-SLEEP | STANDBYIDLE, HIBERNATEIDLE, UNATTENDSLEEP, VIDEOIDLE AC/DC; policy overrides as above | Info (NoClose active requests cover idle) / Low | Lists current timeouts. | ExecutionState, PowerPlan |
| R-MS | `Caps.AoAc` and `OnAcPower` | High if AoAc && DC, Info if AoAc | "Modern Standby on battery: Windows ends app stay-awake requests 5 min after the sleep timeout. Keep the device plugged in." | — |
| R-LID | power values LIDACTION `5CA83367-6E45-459F-A27B-476B1D01C936` (SUB_BUTTONS `4F971E89-EEBD-4455-A8DE-9E59040E7347`), PBUTTONACTION `7648EFA3-DD9C-4E3E-B566-50F929386280` | Low | "Closing the lid / pressing power will sleep regardless of NoClose." | — |
| R-SIGNIN-WAKE | CONSOLELOCK `0E796BDB-100D-47D6-A2D5-F7D2DAA51F51` in SUB_NONE `FEA3413E-7E05-4911-9A71-700331F1C294`; `HKCU\Control Panel\Desktop\DelayLockInterval`; policy `HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings\0E796BDB-...` | Info | "If the PC does sleep, sign-in will be required on wake." Policy name "Require a password when a computer wakes". NoClose prevents idle sleep; it does not change this sign-in setting. | ExecutionState (prevents the sleep) |
| R-INACT | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\InactivityTimeoutSecs` | Medium if >0 | Policy "Interactive logon: Machine inactivity limit" (Security Settings > Local Policies > Security Options). Org-controlled; explain that the organisation intends the session to lock after N minutes without input, and that NoClose does not change it. | — |
| R-SS-POL | `HKCU\Software\Policies\Microsoft\Windows\Control Panel\Desktop` (`ScreenSaveActive`, `ScreenSaverIsSecure`, `ScreenSaveTimeOut`, `SCRNSAVE.EXE`) | Medium | Policies "Enable screen saver", "Password protect the screen saver", "Screen saver timeout", "Force specific screen saver". Org-controlled. | — |
| R-SS-USER | `HKCU\Control Panel\Desktop` same names; SPI get | Low | User screen saver timeout/secure. | ScreenSaver |
| R-MDM-LOCK | `HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\DeviceLock` values `MaxInactivityTimeDeviceLock`, `DevicePasswordEnabled` | Medium | Intune/MDM "DeviceLock/MaxInactivityTimeDeviceLock". Org-controlled. | — |
| R-DYNLOCK | `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\EnableGoodbye`; policy `HKLM\SOFTWARE\Policies\Microsoft\PassportForWork\DynamicLock\DynamicLock` | Low | "Dynamic Lock locks the PC when your paired phone goes out of range. Turn it off in Settings > Accounts > Sign-in options if needed." NoClose does not change it. | — |
| R-SCARD | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\ScRemoveOption` (REG_SZ "1"/"2"/"3") | Low | Policy "Interactive logon: Smart card removal behavior". Org-controlled. | — |
| R-RDP | `Session` (WTSIsRemoteSession, WTSClientProtocolType, WTSConnectState) | Info/Medium | "You are in a Remote Desktop session. Remote Desktop time limits are based on input and are not affected by NoClose. A disconnected session does not count as signed in for Windows Update restart avoidance." | — |
| R-RDP-LIMITS | `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services` and `HKCU\Software\Policies\Microsoft\Windows NT\Terminal Services`: `MaxIdleTime`, `MaxDisconnectionTime`, `MaxConnectionTime` (ms), `fResetBroken`; listener `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp` same names + `fInheritMaxIdleTime`, `fInheritMaxSessionTime`, `fInheritMaxDisconnectionTime`, `fInheritResetBroken`. Effective = machine policy > user policy > listener. | High if remote && limit>0 && fResetBroken=1; Medium if remote && limit>0; Info if not remote | Policy names under Remote Desktop Services > Remote Desktop Session Host > Session Time Limits: "Set time limit for active but idle Remote Desktop Services sessions", "Set time limit for disconnected sessions", "Set time limit for active Remote Desktop Services sessions", "End session when time limits are reached". Org-controlled. | — |
| R-WU-REBOOT | WUA `RebootRequired`; keys exist: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired`, `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending`; value `HKLM\SOFTWARE\Microsoft\Updates\UpdateExeVolatile` ≠ 0; `PendingFileRenameOperations` (Info only, never alone High) | High if WUA or WU/CBS key | "A restart is pending. Windows may restart outside active hours or at an update deadline. Restart at a convenient time before starting a long job." | ActiveHours (partially) |
| R-WU-HOURS | UX\Settings `ActiveHoursStart/End`, `SmartActiveHoursState`; now | Low/Medium | Shows window; Medium if now is outside it and reboot pending. | ActiveHours |
| R-WU-POLICY | `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate`: `SetActiveHours`, `ActiveHoursStart`, `ActiveHoursEnd`, `ActiveHoursMaxRange`, `ConfigureDeadlineForQualityUpdates`, `ConfigureDeadlineForFeatureUpdates`, `ConfigureDeadlineGracePeriod`, `ConfigureDeadlineGracePeriodForFeatureUpdates`, `ConfigureDeadlineNoAutoReboot`, `WUServer`, `UseWUServer` (under `\AU`); `\AU`: `AUOptions`, `NoAutoRebootWithLoggedOnUsers`, `ScheduledInstallTime`; `HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Update\*` value names | Medium if deadlines present | Policy "Specify deadlines for automatic updates and restarts", etc. "After the deadline Windows restarts regardless of active hours or NoClose." Org-controlled. | — |
| R-MGMT | `HKLM\SOFTWARE\Microsoft\Enrollments` subkeys with `ProviderID` value; WSUS keys above; `Environment.UserDomainName != MachineName` | Info | "This device is managed (MDM / domain / WSUS). Settings set by your organisation take precedence." | — |
| R-ARSO | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\DisableAutomaticRestartSignOn` | Info | "After an update restart the session will not be resumed automatically" (if 1). | — |
| R-REQ | `PowercfgRequests` (admin only, via `powercfg.exe /requests`, 5 s timeout, read stdout) | Info | Lists current requests; flags whether "NoClose:" appears under DISPLAY and SYSTEM when active. | — |
| R-ENGINE | engine status (passed in via an `EngineStatus` snapshot to the engine, not via reader) | High if active && Degraded | | ExecutionState |
| R-MEDIA | `reader.HasAudioRenderEndpoint` (read-only default render endpoint probe, `bool?`) + silent media snapshot (passed in, like R-ENGINE) | Info when active or off, Low when the guard is on but not running | Reports whether the silent media session is active (in-app Diagnostics only; the command-line scan has no snapshot and reports "state unknown"), whether an audio output endpoint exists, and that it complements (does not replace) power requests. | SilentMedia |

`DiagnosticsTextFormatter.Format(IEnumerable<DiagnosticFinding>, MachineSummary)` → plain text:

```
NoClose diagnostics — 2026-11-12 14:03:22 (+01:00)
Machine: <computer>  User: <user>  Elevated: yes  Session: 1 (console)
Power: AC  Modern Standby: no  Active scheme: Balanced {381b...}
[HIGH]   R-WU-REBOOT  A restart is pending
         ...detail...
         Policy: —   Addressed by: Active hours (partial)
         Advice: ...
```

Text only — no user names other than the local user name; the file stays local.

`--diagnose` output: console (inherited stdout or attached parent console); with `--out` ONLY that file is written and its path is printed, without `--out` the default `%LocalAppData%\NoClose\diagnostics-yyyyMMdd-HHmmss.txt` is written (path printed).

---

## 8. UI

### 8.1 TrayController (ApplicationContext)

- `NotifyIcon` with icons: idle (grey), active (green), warning (amber; any High finding or Degraded engine).
- Tooltip (≤127 chars): "NoClose — Active (6h 12m left)" / "NoClose — Idle".
- Left-click: toggle StatusForm visibility (restore if minimized). Double-click: Start/Stop.
- Context menu:
  - Start ▸ Indefinitely / For 1 h / 2 h / 4 h / 8 h / 24 h / Custom… / Until… / While process…
  - Stop
  - Status window
  - Diagnostics…
  - Open log folder
  - Restart elevated (only for an administrator running with a limited token)
  - Start at logon (checkbox)
  - Exit

### 8.2 Session/power notification hosting

`StatusForm` (always has a handle; created at startup with `CreateHandle()` via forcing `Handle` access) registers:
- `WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION=0)`; unregister in `OnHandleDestroyed`.
- `RegisterPowerSettingNotification(hwnd, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE=0)` for: GUID_CONSOLE_DISPLAY_STATE `6FE69556-704A-47A0-8F24-C28D936FDA47`, GUID_SESSION_DISPLAY_STATUS `2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5`, GUID_SESSION_USER_PRESENCE `3C0F4548-C03F-4C4D-B9F2-237EDE686376`, GUID_ACDC_POWER_SOURCE `5D3E9A59-E9D5-4B00-A6BD-FF34FF516548`, GUID_SYSTEM_AWAYMODE `98A7F580-01F7-48AA-9C0F-44352C29E5C0`, GUID_LIDSWITCH_STATE_CHANGE `BA3E0F4D-B817-4094-A2D1-D56379E6A0F3`. Keep handles; `UnregisterPowerSettingNotification` on destroy.
- `RegisterSuspendResumeNotification(hwnd, DEVICE_NOTIFY_WINDOW_HANDLE)`; `UnregisterSuspendResumeNotification`.
- WndProc handles `WM_WTSSESSION_CHANGE (0x02B1)`, `WM_POWERBROADCAST (0x0218)` with `PBT_APMSUSPEND 0x4`, `PBT_APMRESUMESUSPEND 0x7`, `PBT_APMPOWERSTATUSCHANGE 0xA`, `PBT_APMRESUMEAUTOMATIC 0x12`, `PBT_POWERSETTINGCHANGE 0x8013` (lParam → `POWERBROADCAST_SETTING`; read `PowerSetting` GUID, `DataLength`, then first DWORD of `Data` at offset 20), `WM_SETTINGCHANGE (0x001A)` (log + schedule guard reapply in 2 s), `WM_QUERYENDSESSION`, `WM_ENDSESSION`. Return `TRUE` (1) for WM_POWERBROADCAST.
- Each event → `TimelineLog` + (where listed in §5.3) `engine.Post(EngineReassert)`.

Also subscribe `Microsoft.Win32.SystemEvents.SessionEnding` for logging only (detach on exit).

### 8.3 StatusForm

Small window (~420×520 logical px, `FormBorderStyle.FixedSingle`, `MaximizeBox=false`, `ShowInTaskbar = true`, `AutoScaleMode.Dpi`). Closing it (X) minimizes to tray **unless** the EndSession guard is applied, in which case it minimizes to the taskbar (stays visible). `--minimized` → `WindowState = Minimized` on first show. When no guard requires visibility and the user closes it, `Hide()` is fine.

Layout (TableLayoutPanel, top to bottom):
1. Status header: big label "Active" / "Idle" / "Degraded", activation text ("until 18:00", "while build.exe runs"), countdown, reason textbox (editable when idle).
2. Start/Stop button + mode selector (ComboBox: Indefinite / Duration / Until / While process) + parameter field.
3. Warnings banner (amber panel) listing High findings in one line each; "Details…" opens DiagnosticsForm.
4. Guards list: for each guard a row: CheckBox (enabled), name, status chip text (`Applied`, `Off`, `Needs elevation`, `Set by your organisation`, `Changed externally`, `Error`), "?" button showing `Description`. Admin-required guards are disabled with tooltip "Requires administrator — use Restart elevated" when not elevated.
5. Footer: "Restart elevated" (LinkLabel, shield icon via `SystemIcons.Shield`; hidden unless the account is an administrator running with a limited token), "Open log", elevation status text.

Enabling a guard with `ChangesSettings` for the first time shows a confirmation dialog listing exactly what will change and that it is restored on exit; on OK set `Acknowledged=true` in settings.

### 8.4 DiagnosticsForm

ListView (Details): Severity, Rule, Title, Addressed by, "Organisation setting" yes/no. Bottom: detail text box (Detail + PolicyName + Advice), buttons "Refresh", "Copy report", "Save report…". Runs diagnostics on a background `Task` (reader is read-only and thread-safe) and marshals results.

### 8.5 Notifications

Balloon tips (`NotifyIcon.ShowBalloonTip`) for: activation started/ended, guard Reverted/Error, new High finding, stale journal restored at startup. Throttle identical messages to once per hour.

---

## 9. Restore journal

### 9.1 Location

`JournalStore.Path`: if elevated → `%ProgramData%\NoClose\restore-journal.json`; else `%LocalAppData%\NoClose\restore-journal.json`. At startup, check BOTH paths (a crash in an elevated run may be recovered when running elevated; if only non-elevated and the ProgramData journal contains admin entries → show banner "Settings from a previous elevated session need restoring — Restart elevated" and skip those entries).

Directory creation: `%ProgramData%\NoClose` is created with a protected DACL: `FullControl` for Administrators and SYSTEM, and `ReadAndExecute` (inherited) for BUILTIN\Users, so standard users cannot tamper with an admin journal but a non-elevated instance can still see and parse it and report "requires elevation" (an existing directory from an older build is brought up to this DACL by an elevated run). A journal that exists but cannot be read is reported as "needs elevation", never as absent. Every save increments a `sequence`, flushes the file and then the containing directory (NTFS commits a same-volume rename lazily), and a load prefers a complete, parseable `.tmp` with a higher sequence than the main file. Leftover inactive `NoClose temporary (...)` power plans that no open entry refers to are deleted at elevated startup; an active one is only reported; the PowerPlan guard refuses to duplicate such a plan unless an open entry supplies the real original.

### 9.2 Format (schemaVersion 1)

```json
{
  "schemaVersion": 1,
  "sessionId": 1,
  "processId": 12345,
  "processStartUtc": "2026-11-12T13:02:11.123Z",
  "machine": "PC-NAME",
  "userSid": "S-1-5-21-...",
  "createdUtc": "2026-11-12T13:02:11.200Z",
  "entries": [
    {
      "id": "6b1c...guid",
      "guard": "ActiveHours",
      "kind": "RegistryValue",
      "recordedUtc": "...",
      "target": { "hive": "HKLM", "view": "64", "key": "SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings", "name": "ActiveHoursStart" },
      "original": { "state": "present", "type": "DWord", "data": "8" },
      "applied":  { "state": "present", "type": "DWord", "data": "14" },
      "status": "Applied"
    },
    {
      "id": "...", "guard": "PowerPlan", "kind": "PowerActiveScheme",
      "target": { "originalScheme": "381b4222-f694-41f0-9685-ff5bb260df2e", "tempScheme": "a1b2...", "tempName": "NoClose temporary (from Balanced)" },
      "status": "Applied"
    },
    {
      "id": "...", "guard": "ScreenSaver", "kind": "SpiScreenSaveActive",
      "target": { "sessionId": 1 }, "original": { "state": "present", "type": "Bool", "data": "true" },
      "status": "Applied"
    }
  ]
}
```

- `original.state` ∈ `present|absent`. `type` ∈ `DWord|QWord|String|ExpandString|MultiString|Binary|Bool`. `data`: DWord/QWord as decimal string, String as is, MultiString as JSON array string (serialize as JSON array inside `data`? → use a separate `"dataList"` array field for MultiString), Binary as base64.
- `status` ∈ `Pending` (journal written, change not yet confirmed), `Applied`, `Restored`, `Failed`.
- `SpiScreenSaveActive` entries are session-scoped: at startup, restore only if `sessionId` matches the current session AND the user SID matches; otherwise discard with a log line (a new logon already reset the runtime value).

Serialization: `System.Text.Json` with a source-generated `JsonSerializerContext` (`[JsonSerializable(typeof(RestoreJournal))]`), camelCase, enums as strings (`JsonStringEnumConverter<T>`), indented.

### 9.3 Atomic write

`JournalStore.Save(journal)` (increments `sequence`; after the rename the directory is flushed, see 9.1): serialize → write to `restore-journal.json.tmp` with `FileStream(FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)`, `Flush(true)` → `File.Move(tmp, path, overwrite: true)` (uses MoveFileEx REPLACE_EXISTING). If `Move` fails, retry 3× 50 ms. When all entries are Restored → delete the file (and tmp). Thread-safety: all journal operations under one `lock`.

`Record(entry)` sets `status=Pending`, saves, returns; caller makes the change then calls `MarkApplied(id)` (saves). On restore success `MarkRestored(id)`.

### 9.4 RegistryWriteGuard

All registry writes in the app go through `RegistryAccessor.SetValue/DeleteValue`, which first calls `RegistryWriteGuard.EnsureAllowed(hive, key)`:
- Throw `InvalidOperationException` if `key` contains (case-insensitive) `\Policies\` or starts with `Policies\` or `SOFTWARE\Policies`, or starts with `SOFTWARE\Microsoft\PolicyManager`, `SOFTWARE\Microsoft\Enrollments`, `SYSTEM\CurrentControlSet\Control\Power\PowerRequestOverride`, `SYSTEM\CurrentControlSet\Control\Terminal Server`.
- Allow-list (anything else also throws): `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings` (values `ActiveHoursStart`, `ActiveHoursEnd`, `SmartActiveHoursState` only).
Unit tests cover both lists. (No other guard writes the registry in v1; screen saver uses SPI, power plan uses PowerProf on a duplicate scheme.)

### 9.5 RestorePlanner (pure)

`RestorePlan Plan(RestoreJournal j, RestoreContext ctx)` where ctx = { currentSessionId, userSid, isElevated }. Produces ordered `RestoreAction`s in reverse APPEND order (list index; `recordedUtc` is wall clock time that can jump backwards and is display only): `RestoreRegistryValue(target, original)`, `DeleteRegistryValue(target)`, `ActivateScheme(original) + DeleteScheme(temp)` (if temp null → only check active scheme; if `tempScheme` is active, activate original), `SetSpiScreenSaveActive(bool)`, `Skip(reason)`. `Pending` entries are restored too (the change may have partially happened). Entries needing admin when not elevated → `Skip("requires elevation")` and kept in journal.

### 9.6 When restore runs

- Normal stop (`--stop`, Stop button, activation end): `GuardManager.RestoreAll()`.
- Exit: same, then delete journal.
- `WM_ENDSESSION(wParam=TRUE)`: `AppHost.EmergencyRestore()` (journal-driven, no UI), then the activation is marked ended (resume data kept) and the process exits.
- Restart after servicing (`--restored-after-restart`): `ResumeDecision.Decide` reads the saved arguments and the saved absolute deadline; unreadable or expired state is NOT resumed (never turned into an indefinite activation) and a Duration resumes with its remaining time only.
- Forwarded `--restore` to a running instance: the running activation is not stopped and the instance's own journal is not touched; only journals of other (dead) runs are restored.
- Unhandled exceptions: `AppHost.EmergencyRestore()`.
- Startup: if a journal exists and its `processId` is not a live process with the same `processStartUtc` (compare `Process.GetProcessById(pid).StartTime.ToUniversalTime()` within 1 s), treat as stale → `RestoreService.RestoreFromDisk()` before anything else, balloon + log.
- `--restore` CLI.

`EmergencyRestore` must be idempotent and safe to call multiple times (guarded by `Interlocked.Exchange` flag), each action in its own try/catch, total budget 4 s.

---

## 10. Settings

`%AppData%\NoClose\settings.json` (atomic write like §9.3, without WriteThrough requirement). Source-generated JSON.

```json
{
  "schemaVersion": 1,
  "reason": "Long-running task",
  "defaultActivation": { "kind": "Indefinite" },
  "startActiveOnLaunch": false,
  "awayMode": false,
  "reassertSeconds": 45,
  "guardReapplyMinutes": 5,
  "registerForRestart": true,
  "startAtLogon": false,
  "startMinimized": true,
  "guards": {
    "ExecutionState": { "enabled": true,  "acknowledged": true },
    "ScreenSaver":    { "enabled": false, "acknowledged": false },
    "PowerPlan":      { "enabled": false, "acknowledged": false,
                        "items": { "sleep": true, "hibernate": true, "unattendedSleep": true, "display": true,
                                   "consoleLockDisplayOff": false, "allowSystemRequired": true,
                                   "allowDisplayRequired": true, "allowAwayMode": false } },
    "ActiveHours":    { "enabled": false, "acknowledged": false },
    "EndSession":     { "enabled": false, "acknowledged": false },
    "SilentMedia":    { "enabled": false, "acknowledged": false }
  },
  "log": { "retainDays": 14, "maxFileMB": 10, "tickLogMinutes": 15 },
  "lastWindowBounds": null
}
```

Unknown fields ignored; missing fields default; invalid file → rename to `settings.json.bad-<timestamp>` and use defaults (log Warning). Values clamped on load.

---

## 11. Start at logon

`StartupTask` uses `schtasks.exe` (full path `%SystemRoot%\System32\schtasks.exe`), task name `\NoClose\NoClose at logon (<user name>)`.

- Create (requires elevation; if not elevated, run the command via `runas` with `UseShellExecute=true, Verb="runas"`): generate task XML to a temp file in `%TEMP%` (UTF-16 LE with BOM) and run `schtasks /Create /TN "<name>" /XML "<file>" /F`. XML essentials:
  - `<LogonTrigger><UserId>DOMAIN\user</UserId><Delay>PT30S</Delay></LogonTrigger>`
  - `<Principal id="Author"><UserId>{user SID}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>`
  - Settings: `MultipleInstancesPolicy=IgnoreNew`, `DisallowStartIfOnBatteries=false`, `StopIfGoingOnBatteries=false`, `ExecutionTimeLimit=PT0S`, `AllowHardTerminate=false`, `StartWhenAvailable=false`, `Priority=7`.
  - Action: `<Exec><Command>"{Environment.ProcessPath}"</Command><Arguments>--minimized</Arguments></Exec>`.
- Delete: `schtasks /Delete /TN "<name>" /F`.
- Query: `schtasks /Query /TN "<name>"` exit code 0 = exists.
- Delete the temp XML afterwards. Log command + exit code (not the XML).

This is a user setting for the app itself (not a Windows/policy change), so it is not journaled; it is explicitly toggled by the user.

---

## 12. Timeline log

`TimelineLog`: thread-safe, background writer (`Channel<string>` + one writer task), file `%LocalAppData%\NoClose\logs\noclose-yyyyMMdd.log`, UTF-8, roll when > `maxFileMB` (suffix `-1`, `-2`), delete files older than `retainDays` at startup. Flush after each line (AutoFlush) — losing the last lines in a crash defeats the purpose. Synchronous `FlushNow()` used by EmergencyRestore.

Line format: `2026-11-12T14:03:22.123+01:00 [INFO ] [Session] Locked (session 1)` — level ∈ `DEBUG|INFO|WARN|ERROR`; category ∈ `App, Engine, Guard, Journal, Session, Power, Display, Presence, EndSession, Diag, Cli`.

Must log: app start (version, elevated, session id, remote/console, OS build, AoAc, AC/DC), activation start/stop with reason and mode, every guard apply/restore/reapply result with before/after values (values only; no secrets), every WTS event name, every power broadcast, display state changes (Off/On/Dimmed), user presence (Present/Inactive), WM_QUERYENDSESSION with decoded flags and decision, WM_ENDSESSION, SessionEnding (SystemEvents) reason, settings change broadcast, engine tick summary every `tickLogMinutes` (uptime of activation, verify result, `WTSINFO.LastInputTime` age if available), diagnostics High findings on start and when they change, journal writes/restores, unhandled exceptions with stack.

On startup also log "Previous run ended: <last line of previous log>" to help reconstruct hour 48.

---

## 13. P/Invoke reference

All in `Platform/Interop`, `internal static partial class`, `[LibraryImport]` with `StringMarshalling.Utf16` where strings appear. Use `[MarshalAs(UnmanagedType.Bool)]` for BOOL returns. `SetLastError = true` only where noted; read with `Marshal.GetLastPInvokeError()`.

### 13.1 kernel32

```csharp
[Flags] internal enum EXECUTION_STATE : uint {
    ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002,
    ES_AWAYMODE_REQUIRED = 0x00000040, ES_CONTINUOUS = 0x80000000 }
// ES_USER_PRESENT (0x4) intentionally NOT defined.

[LibraryImport("kernel32.dll", SetLastError = true)]
internal static partial EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

internal enum POWER_REQUEST_TYPE { PowerRequestDisplayRequired = 0, PowerRequestSystemRequired = 1,
    PowerRequestAwayModeRequired = 2, PowerRequestExecutionRequired = 3 }

[StructLayout(LayoutKind.Sequential)]
internal struct REASON_CONTEXT {
    public uint Version;          // POWER_REQUEST_CONTEXT_VERSION = 0
    public uint Flags;            // POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1
    public IntPtr SimpleReasonString; // LPWSTR (union; on x64 the union is 8-byte aligned, total size 32)
    private IntPtr _pad1; private IntPtr _pad2; // reserve union size (Detailed: HMODULE, ULONG, ULONG, LPWSTR*)
}
```
Union note: the native union `Reason` is `{ struct { HMODULE LocalizedReasonModule; ULONG LocalizedReasonId; ULONG ReasonStringCount; LPWSTR* ReasonStrings; } Detailed; LPWSTR SimpleReasonString; }` → size 24 on x64, 16 on x86. Declaring `IntPtr + IntPtr + IntPtr` gives 24/12; x86 needs 16 → use `[StructLayout(LayoutKind.Explicit)]` instead:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct REASON_CONTEXT {
    public uint Version; public uint Flags; public REASON_UNION Reason; }
[StructLayout(LayoutKind.Sequential)]
internal struct REASON_DETAILED { public IntPtr LocalizedReasonModule; public uint LocalizedReasonId; public uint ReasonStringCount; public IntPtr ReasonStrings; }
[StructLayout(LayoutKind.Explicit)]
internal struct REASON_UNION { [FieldOffset(0)] public REASON_DETAILED Detailed; [FieldOffset(0)] public IntPtr SimpleReasonString; }
```
(Use this explicit form; the app builds AnyCPU/x64 — set `<PlatformTarget>x64</PlatformTarget>` plus an `arm64` RID-neutral build is fine; layout is correct on all.)

```csharp
[LibraryImport("kernel32.dll", SetLastError = true)]
internal static partial IntPtr PowerCreateRequest(in REASON_CONTEXT context);   // INVALID_HANDLE_VALUE (-1) on failure
[LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool PowerSetRequest(IntPtr powerRequest, POWER_REQUEST_TYPE requestType);
[LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool PowerClearRequest(IntPtr powerRequest, POWER_REQUEST_TYPE requestType);
[LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool CloseHandle(IntPtr handle);
```
Wrap the handle in a `SafePowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid` whose `ReleaseHandle` calls `CloseHandle`.

```csharp
[LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);
[LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
internal static partial int RegisterApplicationRestart(string? pwzCommandline, uint dwFlags); // HRESULT
[LibraryImport("kernel32.dll")] internal static partial int UnregisterApplicationRestart();
[LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool AttachConsole(uint dwProcessId);
[LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
[LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool ProcessIdToSessionId(uint pid, out uint sessionId);
[LibraryImport("kernel32.dll")] internal static partial uint WTSGetActiveConsoleSessionId();
[LibraryImport("kernel32.dll")] internal static partial IntPtr LocalFree(IntPtr hMem);

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
```

### 13.2 powrprof

All return `uint` Win32 error (0 = ERROR_SUCCESS) unless noted; no SetLastError.

```csharp
[LibraryImport("powrprof.dll")] internal static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid); // LocalFree
[LibraryImport("powrprof.dll")] internal static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);
[LibraryImport("powrprof.dll")] internal static partial uint PowerDuplicateScheme(IntPtr rootPowerKey, in Guid sourceSchemeGuid, ref IntPtr destinationSchemeGuid); // pass IntPtr.Zero in; LocalFree out
[LibraryImport("powrprof.dll")] internal static partial uint PowerDeleteScheme(IntPtr rootPowerKey, in Guid schemeGuid);
[LibraryImport("powrprof.dll")] internal static partial uint PowerReadACValueIndex(IntPtr root, in Guid scheme, in Guid sub, in Guid setting, out uint acValueIndex);
[LibraryImport("powrprof.dll")] internal static partial uint PowerReadDCValueIndex(IntPtr root, in Guid scheme, in Guid sub, in Guid setting, out uint dcValueIndex);
[LibraryImport("powrprof.dll")] internal static partial uint PowerWriteACValueIndex(IntPtr root, in Guid scheme, in Guid sub, in Guid setting, uint acValueIndex);
[LibraryImport("powrprof.dll")] internal static partial uint PowerWriteDCValueIndex(IntPtr root, in Guid scheme, in Guid sub, in Guid setting, uint dcValueIndex);
[LibraryImport("powrprof.dll")] internal static partial uint PowerReadFriendlyName(IntPtr root, in Guid scheme, IntPtr sub, IntPtr setting, byte* buffer, ref uint bufferSize); // call twice (size query)
[LibraryImport("powrprof.dll")] internal static partial uint PowerWriteFriendlyName(IntPtr root, in Guid scheme, IntPtr sub, IntPtr setting, byte* buffer, uint bufferSize);
[LibraryImport("powrprof.dll")] internal static partial int CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength, out uint outputBuffer, uint outputBufferLength); // NTSTATUS; level SystemExecutionState = 16
[LibraryImport("powrprof.dll")] [return: MarshalAs(UnmanagedType.U1)]
internal static partial bool GetPwrCapabilities(out SYSTEM_POWER_CAPABILITIES caps); // BOOLEAN
```
For `PowerReadFriendlyName` with scheme-only, pass `IntPtr.Zero` for sub/setting (NULL GUID pointers) — hence `IntPtr` not `in Guid`.

`SYSTEM_POWER_CAPABILITIES` (sequential; BOOLEAN = `byte`):
```
byte PowerButtonPresent, SleepButtonPresent, LidPresent, SystemS1, SystemS2, SystemS3, SystemS4, SystemS5,
     HiberFilePresent, FullWake, VideoDimPresent, ApmPresent, UpsPresent, ThermalControl, ProcessorThrottle;
byte ProcessorMinThrottle, ProcessorMaxThrottle;
byte FastSystemS4; byte Hiberboot; byte WakeAlarmPresent; byte AoAc;
byte DiskSpinDown;
byte HiberFileType; byte AoAcConnectivitySupported; fixed byte spare3[6];
byte SystemBatteriesPresent, BatteriesAreShortTerm;
BATTERY_REPORTING_SCALE BatteryScale0, BatteryScale1, BatteryScale2;   // each { uint Granularity; uint Capacity; }
int AcOnLineWake, SoftLidWake, RtcWake, MinDeviceWakeState, DefaultLowLatencyWake; // SYSTEM_POWER_STATE enums (int)
```
Add a unit test asserting `Marshal.SizeOf<SYSTEM_POWER_CAPABILITIES>() == 76`.

### 13.3 user32

```csharp
[LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
[LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool SystemParametersInfoBool(uint uiAction, uint uiParam, out int pvParam, uint fWinIni); // BOOL out
// SPI_GETSCREENSAVETIMEOUT=0x000E, SPI_GETSCREENSAVEACTIVE=0x0010, SPI_SETSCREENSAVEACTIVE=0x0011,
// SPI_GETSCREENSAVERRUNNING=0x0072, SPI_GETSCREENSAVESECURE=0x0076. fWinIni always 0 for SET.

[LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);
[LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool ShutdownBlockReasonDestroy(IntPtr hWnd);
[LibraryImport("user32.dll", SetLastError = true)]
internal static partial IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, in Guid powerSettingGuid, uint flags);
[LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool UnregisterPowerSettingNotification(IntPtr handle);
[LibraryImport("user32.dll", SetLastError = true)]
internal static partial IntPtr RegisterSuspendResumeNotification(IntPtr hRecipient, uint flags);
[LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool UnregisterSuspendResumeNotification(IntPtr handle);
[LibraryImport("user32.dll")] internal static partial int GetSystemMetrics(int nIndex); // SM_REMOTESESSION = 0x1000
[LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool GetLastInputInfo(ref LASTINPUTINFO plii); // diagnostics/log only
[StructLayout(LayoutKind.Sequential)] internal struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

[StructLayout(LayoutKind.Sequential)]
internal struct POWERBROADCAST_SETTING { public Guid PowerSetting; public uint DataLength; /* byte Data[1] follows at offset 20 */ }
```
Read setting data: `Guid g = Marshal.PtrToStructure<Guid>(lParam); uint len = (uint)Marshal.ReadInt32(lParam, 16); int value = Marshal.ReadInt32(lParam, 20);` (for DWORD settings; GUID_SYSTEM_AWAYMODE and others are DWORD; GUID_ACTIVE_POWERSCHEME is a GUID — not registered).

### 13.4 wtsapi32

```csharp
[LibraryImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags); // NOTIFY_FOR_THIS_SESSION = 0
[LibraryImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool WTSUnRegisterSessionNotification(IntPtr hWnd);
[LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool WTSQuerySessionInformation(IntPtr hServer, uint sessionId, WTS_INFO_CLASS infoClass, out IntPtr ppBuffer, out uint pBytesReturned);
[LibraryImport("wtsapi32.dll")] internal static partial void WTSFreeMemory(IntPtr pMemory);
// WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero; WTS_CURRENT_SESSION = 0xFFFFFFFF
internal enum WTS_INFO_CLASS { WTSConnectState = 8, WTSClientProtocolType = 16, WTSSessionInfo = 24, WTSIsRemoteSession = 29 }
// WTSConnectState → int (WTS_CONNECTSTATE_CLASS: 0 Active, 1 Connected, ..., 4 Disconnected);
// WTSClientProtocolType → ushort (0 console, 1 ICA, 2 RDP); WTSIsRemoteSession → byte/BOOL (read 1 byte != 0).
```
`WTSINFOW` (for `LastInputTime`): sequential, `CharSet` Unicode — `int State; uint SessionId; uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes; fixed char WinStationName[32]; fixed char Domain[17]; fixed char UserName[21]; long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;` (FILETIME as `long`; requires `unsafe` struct with explicit 8-byte alignment — sequential layout gives correct padding). LastInputTime may be 0 for console sessions; treat 0 as unknown.

### 13.5 WUA COM (late bound, read-only)

`Type t = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo"); dynamic si = Activator.CreateInstance(t)!; bool r = si.RebootRequired;` inside try/catch, on a background STA-agnostic call (MTA ok), timeout 10 s via Task; `Marshal.FinalReleaseComObject`. Requires `<EnableComHosting>` NOT needed; `dynamic` requires `Microsoft.CSharp` (in-box for net10.0). If trimming is off (default for WinForms), fine.

### 13.6 Win32 constants summary

```
WM_QUERYENDSESSION 0x0011, WM_ENDSESSION 0x0016, WM_SETTINGCHANGE 0x001A, WM_POWERBROADCAST 0x0218, WM_WTSSESSION_CHANGE 0x02B1
ENDSESSION_CLOSEAPP 0x00000001, ENDSESSION_CRITICAL 0x40000000, ENDSESSION_LOGOFF 0x80000000
PBT_APMPOWERSTATUSCHANGE 0x000A, PBT_APMRESUMEAUTOMATIC 0x0012, PBT_APMRESUMESUSPEND 0x0007, PBT_APMSUSPEND 0x0004, PBT_POWERSETTINGCHANGE 0x8013
WTS_CONSOLE_CONNECT 1 .. WTS_SESSION_TERMINATE 0xB (names per §12 logging)
DEVICE_NOTIFY_WINDOW_HANDLE 0
ERROR_ACCESS_DENIED 5, ERROR_FILE_NOT_FOUND 2, ERROR_OPERATION_IN_PROGRESS 329, ERROR_CANCELLED 1223
```


### 13.7 ole32 and Core Audio (COM, source generated)

`ole32`: `CoInitializeEx` (COINIT_MULTITHREADED), `CoUninitialize`, `CoTaskMemFree`, `CoCreateInstance`. Core Audio interfaces (`IMMDeviceEnumerator`, `IMMDevice`, `IMMNotificationClient`, `IAudioClient`, `IAudioRenderClient`, `IAudioSessionControl`) use `[GeneratedComInterface]`; see §6.8. Layout checks: `WAVEFORMATEX` = 18 bytes (Pack 2), `PROPERTYKEY` = 20 bytes; constants `AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000`, `AUDCLNT_BUFFERFLAGS_SILENT = 0x2`, buffer 200 ms = 2,000,000 (100 ns units).
---

## 14. AppHost wiring

`AppHost` owns: `AppSettings`, `TimelineLog`, `JournalStore`, `RestoreService`, `KeepAwakeEngine`, `GuardManager`, `TrayController`, `StatusForm`, `PipeServer`, `ProcessWatcher`, `WindowsMachineReader`, `DiagnosticsEngine`.

`Start(cmd)`:
1. Load settings; init log; log start banner.
2. Stale-journal check & restore (§9.6).
3. Create StatusForm (force handle), TrayController.
4. Start engine thread (idle).
5. Run diagnostics in background; update banner/icon. Diagnostics are refreshed periodically (every 15 min while active, hourly while idle; overlapping runs are skipped) and after resume, session unlock and (throttled) settings changes; new High findings are logged and shown (banner, amber icon, balloon).
6. If `cmd.Verb == Start` or `settings.StartActiveOnLaunch` or `cmd.RestoredAfterRestart && lastRunWasActive` (persisted flag `settings.lastRunActive`) → `Activate(spec, reason)`.
7. (The pipe server was started before step 2; queued commands are released now.)

`Activate(spec, reason)`: validate (process exists etc.) → `GuardManager.ApplyAll()` → `ActivationPolicy.Begin` → UI update → log. `Deactivate(why)`: `GuardManager.RestoreAll()` → UI update → log.

`Shutdown()`: Deactivate; engine shutdown; journal delete if empty; detach SystemEvents; dispose NotifyIcon (`Visible=false` first); flush log.

---

## 15. Tests (tests/NoClose.Tests)

- `CliParserTests`: all switches, aliases, conflicts, quoting, unknown switches, exit codes.
- `DurationParserTests`: `8h`, `1h30m`, `90m`, `1.5h`, `2d`, `01:30`, invalid, bounds.
- `UntilParserTests`: past time → tomorrow, ISO, DST edge (use fixed `TimeZoneInfo`).
- `ActivationPolicyTests`: deadline reached, process grace.
- `JournalSerializerTests`: round-trip all kinds/types, absent originals, unknown fields tolerated, schemaVersion mismatch → error.
- `RestorePlannerTests`: reverse order, Pending entries, session mismatch for SPI, elevation skip, null tempScheme.
- `RegistryWriteGuardTests`: Policies/PolicyManager/Enrollments/PowerRequestOverride/Terminal Server rejected; allow-list accepted; everything else rejected.
- `ActiveHoursPlannerTests`: wrap-around at midnight, 18 h length.
- `DiagnosticsRuleTests`: each rule with `FakeMachineReader` (present/absent/policy cases, effective RDP precedence, AoAc+DC high).
- `DiagnosticsTextFormatterTests`: snapshot-style.
- `InteropLayoutTests`: `SizeOf<REASON_CONTEXT>() == (IntPtr.Size == 8 ? 32 : 20)`, `SYSTEM_POWER_CAPABILITIES == 76`, `LASTINPUTINFO == 8`.
- `NoInputSimulationTests`: scan `src/**/*.cs` for forbidden identifiers (`SendInput`, `keybd_event`, `mouse_event`, `SetCursorPos`, `INPUT_KEYBOARD`, `VK_F15`) → none; scan for `SetThreadExecutionState` only in `Kernel32.cs` and `KeepAwakeEngine.cs`; scan for `Registry.SetValue`/`.SetValue(` only in `RegistryAccessor.cs`.
- `SilentMedia*Tests`: pure logic only. Backoff schedule (2 s, 5 s, 15 s, 60 s cap), HRESULT classification, state to guard status mapping, settings default OFF and migration of a settings file without the guard, serialization round-trip, `--guards silentmedia` token parsing (any case), `ForElevatedCopy` round-trip, `R-MEDIA` with a fake reader (`HasAudioRenderEndpoint`), interop sanity (`WAVEFORMATEX` = 18, `PROPERTYKEY` = 20, GUIDs, constants, vtable declaration order), and the guard against a fake `ISilentMediaSession`. No test opens an audio stream or touches an audio device.

Tests must not touch the real registry, power settings or SPI.

---

## 16. Implementation order

1. Solution/projects/manifest; Core (CLI, parsers, settings, journal, planner, registry guard) + tests.
2. Interop + layout tests; TimelineLog.
3. KeepAwakeEngine + ExecutionStateGuard; tray + StatusForm skeleton; single instance + pipe.
4. Session/power notifications + logging.
5. Diagnostics engine + rules + WindowsMachineReader + `--diagnose`.
6. ScreenSaverGuard, PowerPlanGuard, ActiveHoursGuard, EndSessionGuard; JournalStore/RestoreService; crash recovery; `--restore`.
7. Start at logon; Restart elevated; polish UI copy.

## 17. Manual verification checklist (to run on a test VM)

- `powercfg /requests` shows `NoClose: <reason>` under DISPLAY, SYSTEM, EXECUTION while active; gone after Stop.
- Kill the process with Task Manager while PowerPlan + ActiveHours applied → relaunch restores original scheme, deletes temp scheme, restores UX values; journal file removed.
- Locked/unlocked/display events appear in the log with timestamps.
- Logoff with EndSession guard → Windows shows "NoClose is keeping this session open"; "Sign out anyway" works and settings are restored.
- Non-elevated run: admin guards disabled, "Restart elevated" works and the command line is preserved.
- `NoClose.exe --diagnose` from cmd prints the report and writes the file.
- On a machine with policy screen-saver keys: ScreenSaver guard shows "Set by your organisation" and nothing is changed.
