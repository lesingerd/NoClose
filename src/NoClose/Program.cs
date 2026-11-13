using System.Globalization;
using NoClose.Core.Activation;
using NoClose.Core.Cli;
using NoClose.Core.Diagnostics;
using NoClose.Core.Guards;
using NoClose.Journal;
using NoClose.Logging;
using NoClose.Platform;
using NoClose.Platform.Interop;

namespace NoClose;

internal static class Program
{
    private static SingleInstance? _single;

    internal static void ReleaseMutex() => _single?.Release();
    internal static void ReacquireMutex() => _single?.TryAcquire();

    [STAThread]
    private static int Main(string[] args)
    {
        // 1. parse
        var parse = CliParser.Parse(args);
        if (!parse.Ok)
        {
            ConsoleAttach.Print("NoClose: " + parse.Error + Environment.NewLine + Environment.NewLine + CliParser.Usage);
            return CliParser.ExitUsage;
        }
        var cmd = parse.Command!;

        switch (cmd.Verb)
        {
            case CliVerb.Help:
                ConsoleAttach.Print(CliParser.Usage);
                return CliParser.ExitOk;
            case CliVerb.Diagnose:
                return RunDiagnose(cmd); // 2. read-only, no single instance, no UI
        }

        // An elevated copy started through "Restart elevated" must run as the user who asked for it.
        if (cmd.Handoff && cmd.HandoffSid != null && !ElevationRules.SameUser(cmd.HandoffSid, Elevation.CurrentUserSid))
        {
            const string msg = "NoClose was started elevated as a different user than the one who requested it. " +
                               "Elevation through another account is not supported; sign in with an administrator account instead.";
            ConsoleAttach.Print("NoClose: " + msg);
            MessageBox.Show(msg, "NoClose", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return CliParser.ExitPartialFailure;
        }

        _single = new SingleInstance();
        try
        {
            if (cmd.Verb == CliVerb.Restore)
            {
                // 3. restore: forward to a running instance, otherwise run standalone
                if (!_single.TryAcquire()) return Forward(cmd, args);
                return AppHost.RunStandaloneRestore(cmd.OutPath);
            }

            // 4. single instance
            if (!_single.TryAcquire()) return Forward(cmd, args);

            if (cmd.Verb == CliVerb.Stop)
            {
                ConsoleAttach.Print("NoClose is not running; nothing to stop.");
                return CliParser.ExitOk;
            }
            if (cmd.Verb == CliVerb.Start && cmd.Activation is { Kind: ActivationKind.WhileProcess } wp && !ProcessWatcher.IsRunning(wp))
            {
                ConsoleAttach.Print($"NoClose: the process {(wp.ProcessId?.ToString() ?? wp.ProcessName + ".exe")} is not running; not started.");
                return CliParser.ExitPartialFailure;
            }

            return RunGui(cmd);
        }
        finally
        {
            _single?.Dispose();
        }
    }

    private static int RunGui(CliCommand cmd)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 5. exception handlers (before any window)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { AppHost.Current?.Log.Warn(LogCategory.App, "Unobserved task exception: " + e.Exception); } catch { }
            e.SetObserved();
        };

        var host = new AppHost();

        // 6. run late in the shutdown order: Windows notifies HIGHER levels first (0x3FF is the first application level, 0x100 the last),
        //    so a low level keeps the execution state, power requests and guards in place until the other applications (the protected
        //    work) have been told about the session end.
        if (!Kernel32.SetProcessShutdownParameters(0x100, 0))
            host.Log.Warn(LogCategory.App, "SetProcessShutdownParameters failed, error " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError());

        // 7. ask Windows to restart us after servicing (not after a crash or hang)
        if (host.Settings.RegisterForRestart)
        {
            int hr = Kernel32.RegisterApplicationRestart("--restored-after-restart", NativeConstants.RESTART_NO_CRASH | NativeConstants.RESTART_NO_HANG);
            if (hr != 0) host.Log.Warn(LogCategory.App, $"RegisterApplicationRestart failed, HRESULT 0x{hr:X8}");
        }

        // 8. start and run the message loop
        host.Start(cmd);
        Application.Run(host.ApplicationContext);

        // 9. normal shutdown path
        host.Shutdown();
        return CliParser.ExitOk;
    }

    private static void Fatal(string what, Exception? ex)
    {
        try { AppHost.Current?.Log.Error(LogCategory.App, $"{what}: {ex}"); } catch { }
        try { AppHost.Current?.EmergencyRestore(); } catch { }
        try { AppHost.Current?.Log.FlushNow(); } catch { }
        Environment.Exit(1);
    }

    // ---- --diagnose ---------------------------------------------------------------------------------------------

    private static int RunDiagnose(CliCommand cmd)
    {
        bool elevated = Elevation.IsElevated;
        var reader = new WindowsMachineReader(new PowerSchemeAccessor(), elevated);
        var text = DiagnosticsReport.Build(reader, elevated);

        // --out names the ONLY file written (ConsoleAttach.Print writes it); without --out the default report file is written.
        if (!string.IsNullOrWhiteSpace(cmd.OutPath))
        {
            ConsoleAttach.Print(text + Environment.NewLine + "Report written to " + Path.GetFullPath(cmd.OutPath), cmd.OutPath);
            return CliParser.ExitOk;
        }

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoClose",
            "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        string? fileNote = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(defaultPath)!);
            File.WriteAllText(defaultPath, text, new System.Text.UTF8Encoding(true));
            fileNote = "Report written to " + defaultPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fileNote = "Could not write " + defaultPath + ": " + ex.Message;
        }

        ConsoleAttach.Print(text + Environment.NewLine + fileNote);
        return CliParser.ExitOk;
    }

    // ---- forwarding to the running instance -------------------------------------------------------------------

    private static int Forward(CliCommand cmd, string[] args)
    {
        var response = PipeClient.Send(new PipeRequest(cmd.PipeVerb, args), out bool denied);
        if (response == null)
        {
            ConsoleAttach.Print(denied
                ? "NoClose is already running with different privileges. Use the tray icon of the running instance."
                : "NoClose is already running but did not respond.");
            return CliParser.ExitForwardFailed;
        }
        ConsoleAttach.Print(response.Message, cmd.OutPath);
        return response.Ok ? CliParser.ExitOk : CliParser.ExitPartialFailure;
    }
}
