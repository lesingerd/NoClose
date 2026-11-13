using System.Diagnostics;
using System.Security;
using System.Text;
using NoClose.Logging;

namespace NoClose.Platform;

/// <summary>Start at logon through a Task Scheduler task (highest available run level, interactive token). Not journaled: it is an app setting.</summary>
internal sealed class StartupTask
{
    private readonly ILogSink _log;

    public StartupTask(ILogSink log) => _log = log;

    public static string TaskName => $@"\NoClose\NoClose at logon ({Environment.UserName})";

    private static string SchTasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static string BuildXml(string userName, string sid, string exePath)
    {
        string E(string s) => SecurityElement.Escape(s) ?? "";
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Starts NoClose (keeps the session awake) when {E(userName)} logs on.</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{E(userName)}</UserId>
      <Delay>PT30S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{E(sid)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>""{E(exePath)}""</Command>
      <Arguments>--minimized</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    private int Run(string arguments, bool elevate)
    {
        var psi = new ProcessStartInfo(SchTasks, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = elevate,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevate) psi.Verb = "runas";
        else { psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start schtasks.exe");
        if (!elevate) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); }
        p.WaitForExit(30000);
        var code = p.HasExited ? p.ExitCode : -1;
        _log.Write(LogLevel.Info, LogCategory.App, $"schtasks {arguments.Split(' ')[0]} exit code {code}");
        return code;
    }

    public bool Exists()
    {
        try { return Run($"/Query /TN \"{TaskName}\"", elevate: false) == 0; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    public bool Create()
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "noclose-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unknown.");
            File.WriteAllText(xmlPath, BuildXml(System.Security.Principal.WindowsIdentity.GetCurrent().Name, Elevation.CurrentUserSid, exe), Encoding.Unicode);
            return Run($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", elevate: !Elevation.IsElevated) == 0;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Write(LogLevel.Warn, LogCategory.App, "Start at logon not created: " + ex.Message);
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    public bool Delete()
    {
        try { return Run($"/Delete /TN \"{TaskName}\" /F", elevate: !Elevation.IsElevated) == 0; }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Write(LogLevel.Warn, LogCategory.App, "Start at logon not removed: " + ex.Message);
            return false;
        }
    }
}
