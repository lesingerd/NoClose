using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NoClose.Logging;
using NoClose.Platform;
using NoClose.Platform.Interop;

namespace NoClose.Tests;

public class InteropLayoutTests
{
    [Fact]
    public void ReasonContextSize()
    {
        // 4 (Version) + 4 (Flags) + union: Detailed = pointer + 2 * uint + pointer.
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.SizeOf<REASON_CONTEXT>());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<REASON_UNION>());
    }

    [Fact]
    public void SystemPowerCapabilitiesSize() => Assert.Equal(76, Marshal.SizeOf<SYSTEM_POWER_CAPABILITIES>());

    [Fact]
    public void LastInputInfoSize() => Assert.Equal(8, Marshal.SizeOf<LASTINPUTINFO>());

    [Fact]
    public void PowerBroadcastSettingHeader() => Assert.Equal(20, Marshal.SizeOf<POWERBROADCAST_SETTING>());

    [Fact]
    public void SystemPowerStatusSize() => Assert.Equal(12, Marshal.SizeOf<SYSTEM_POWER_STATUS>());

    [Fact]
    public unsafe void WtsInfoLayout()
    {
        // int State, uint SessionId, 6 x uint, 32+17+21 chars (padded), then 5 FILETIMEs on an 8 byte boundary
        Assert.Equal(0, sizeof(WTSINFOW) % 8);
        Assert.Equal(0, (int)Marshal.OffsetOf<WTSINFOW>(nameof(WTSINFOW.ConnectTime)).ToInt64() % 8);
    }

    [Fact]
    public void ExecutionStateFlagsMatchWin32()
    {
        Assert.Equal(0x00000001u, (uint)EXECUTION_STATE.ES_SYSTEM_REQUIRED);
        Assert.Equal(0x00000002u, (uint)EXECUTION_STATE.ES_DISPLAY_REQUIRED);
        Assert.Equal(0x00000040u, (uint)EXECUTION_STATE.ES_AWAYMODE_REQUIRED);
        Assert.Equal(0x80000000u, (uint)EXECUTION_STATE.ES_CONTINUOUS);
        // the "user present" flag (0x4) is deliberately not defined
        Assert.DoesNotContain(Enum.GetValues<EXECUTION_STATE>(), v => ((uint)v & 0x4) != 0);
    }

    [Fact]
    public void PowerRequestTypesMatchWin32()
    {
        Assert.Equal(0, (int)POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
        Assert.Equal(1, (int)POWER_REQUEST_TYPE.PowerRequestSystemRequired);
        Assert.Equal(2, (int)POWER_REQUEST_TYPE.PowerRequestAwayModeRequired);
        Assert.Equal(3, (int)POWER_REQUEST_TYPE.PowerRequestExecutionRequired);
    }

    [Fact]
    public void ConstantsMatchWin32()
    {
        Assert.Equal(0x0011, NativeConstants.WM_QUERYENDSESSION);
        Assert.Equal(0x0016, NativeConstants.WM_ENDSESSION);
        Assert.Equal(0x0218, NativeConstants.WM_POWERBROADCAST);
        Assert.Equal(0x02B1, NativeConstants.WM_WTSSESSION_CHANGE);
        Assert.Equal(0x8013, NativeConstants.PBT_POWERSETTINGCHANGE);
        Assert.Equal(1223, NativeConstants.ERROR_CANCELLED);
        Assert.Equal("WTS_SESSION_UNLOCK", NativeConstants.WtsEventName(8));
        Assert.Equal("WTS_SESSION_LOCK", NativeConstants.WtsEventName(7));
    }
}

/// <summary>Source tree policy checks (see DESIGN.md section 0 and 5.1).</summary>
public class NoInputSimulationTests
{
    private static readonly string SrcDir = FindSrc();

    private static string FindSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NoClose.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void SourceTreeWasFound() => Assert.True(SourceFiles().Count() > 40);

    [Theory]
    [InlineData("SendInput")]
    [InlineData("keybd_event")]
    [InlineData("mouse_event")]
    [InlineData("SetCursorPos")]
    [InlineData("INPUT_KEYBOARD")]
    [InlineData("INPUT_MOUSE")]
    [InlineData("VK_F15")]
    [InlineData("SendKeys")]
    [InlineData("WM_KEYDOWN")]
    [InlineData("WM_MOUSEMOVE")]
    [InlineData("BlockInput")]
    public void ForbiddenInputIdentifiersAbsent(string identifier)
    {
        var hits = SourceFiles().Where(f => File.ReadAllText(f).Contains(identifier, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(hits.Count == 0, $"'{identifier}' found in: {string.Join(", ", hits)}");
    }

    [Fact]
    public void ExecutionStateApiOnlyInKernel32AndEngine()
    {
        var allowed = new[] { "Kernel32.cs", "KeepAwakeEngine.cs" };
        var hits = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("SetThreadExecutionState", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Contains(h, allowed));
    }

    [Fact]
    public void PowerRequestApisOnlyInKernel32AndEngine()
    {
        var hits = SourceFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bPower(Create|Set|Clear)Request\b"))
            .Select(Path.GetFileName).ToList();
        Assert.All(hits, h => Assert.Contains(h, new[] { "Kernel32.cs", "KeepAwakeEngine.cs" }));
    }

    [Fact]
    public void RegistryWritesOnlyInRegistryAccessor()
    {
        var hits = SourceFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"Registry\.SetValue|\.SetValue\(|\.DeleteSubKey|\.CreateSubKey\("))
            .Select(Path.GetFileName)
            .Where(n => n != "RegistryAccessor.cs").ToList();
        Assert.True(hits.Count == 0, "Registry writes outside RegistryAccessor.cs: " + string.Join(", ", hits));
    }

    [Fact]
    public void NoOtherRegistryWritesViaReaderAccessors()
    {
        // Callers must go through the guarded Apply/DeleteValue wrappers and never open keys writable themselves.
        var hits = SourceFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"writable:\s*true"))
            .Select(Path.GetFileName).Where(n => n != "RegistryAccessor.cs").ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void GuardedRegistryWriteApisAreOnlyUsedByKnownCallers()
    {
        // Apply/DeleteValue are the vetted wrappers (RegistryWriteGuard runs inside). Only the active hours guard and the
        // restore service may call them.
        var allowed = new[] { "RegistryAccessor.cs", "RestoreService.cs", "ActiveHoursGuard.cs" };
        var hits = SourceFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"(?i)registry\.(Apply|DeleteValue)\("))
            .Select(Path.GetFileName).Where(n => !allowed.Contains(n)).ToList();
        Assert.True(hits.Count == 0, "Unexpected registry writers: " + string.Join(", ", hits));
    }
    [Fact]
    public void CoreFolderHasNoPlatformDependencies()
    {
        var core = Path.Combine(SrcDir, "NoClose", "Core");
        foreach (var f in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("DllImport", text);
            Assert.DoesNotContain("LibraryImport", text);
            Assert.DoesNotContain("Microsoft.Win32", text);
            Assert.DoesNotContain("System.Windows.Forms", text);
            Assert.DoesNotContain("NoClose.Platform", text);
        }
    }

    [Fact]
    public void ProjectHasNoPackageReferences()
    {
        var csproj = File.ReadAllText(Path.Combine(SrcDir, "NoClose", "NoClose.csproj"));
        Assert.DoesNotContain("PackageReference", csproj);
        Assert.Contains("net10.0-windows", csproj);
    }

    [Fact]
    public void ManifestRequestsAsInvoker()
    {
        var manifest = XDocument.Load(Path.Combine(SrcDir, "NoClose", "app.manifest"));
        var level = manifest.Descendants().First(e => e.Name.LocalName == "requestedExecutionLevel");
        Assert.Equal("asInvoker", (string?)level.Attribute("level"));
        Assert.Equal("false", (string?)level.Attribute("uiAccess"));
    }
}

public class TimelineLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noclose-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void LineFormat()
    {
        var t = new DateTimeOffset(2026, 11, 12, 14, 3, 22, 123, TimeSpan.FromHours(1));
        Assert.Equal("2026-11-12T14:03:22.123+01:00 [INFO ] [Session] Locked (session 1)",
            TimelineLog.FormatLine(t, LogLevel.Info, LogCategory.Session, "Locked (session 1)"));
        Assert.StartsWith("2026-11-12T14:03:22.123+01:00 [WARN ] [Guard]", TimelineLog.FormatLine(t, LogLevel.Warn, LogCategory.Guard, "x"));
        Assert.Contains("[ERROR]", TimelineLog.FormatLine(t, LogLevel.Error, LogCategory.App, "x"));
        Assert.Contains("[DEBUG]", TimelineLog.FormatLine(t, LogLevel.Debug, LogCategory.App, "x"));
    }

    [Fact]
    public void MultiLineMessagesStayOnOneLine() =>
        Assert.DoesNotContain('\n', TimelineLog.FormatLine(DateTimeOffset.Now, LogLevel.Info, LogCategory.App, "a\r\nb\nc"));

    [Fact]
    public void FlushNowWritesSynchronously()
    {
        using var log = new TimelineLog(_dir);
        log.Info(LogCategory.App, "hello");
        log.Error(LogCategory.Engine, "boom");
        log.FlushNow();
        var files = Directory.GetFiles(_dir, "noclose-*.log");
        var file = Assert.Single(files);
        var text = File.ReadAllText(file);
        Assert.Contains("[INFO ] [App] hello", text);
        Assert.Contains("[ERROR] [Engine] boom", text);
    }

    [Fact]
    public void OldLogsAreDeletedAtStartup()
    {
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "noclose-20200101.log");
        File.WriteAllText(old, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-60));
        using var log = new TimelineLog(_dir, new NoClose.Core.Settings.LogSettings { RetainDays = 14 });
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void PreviousRunLastLine()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "noclose-20200101.log"), "first\nlast line here\n\n");
        using var log = new TimelineLog(_dir, new NoClose.Core.Settings.LogSettings { RetainDays = 100000 });
        Assert.Equal("last line here", log.LastLineOfPreviousRun());
    }
}

public class StartupTaskTests
{
    [Fact]
    public void TaskXmlContainsRequiredSettings()
    {
        var xml = StartupTask.BuildXml(@"CORP\alice & bob", "S-1-5-21-1-2-3-1001", @"C:\Program Files\NoClose\NoClose.exe");
        var doc = XDocument.Parse(xml); // must be well formed (escaping of '&')
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("PT30S", (string?)doc.Descendants(ns + "Delay").Single());
        Assert.Equal(@"CORP\alice & bob", (string?)doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId"));
        var principal = doc.Descendants(ns + "Principal").Single();
        Assert.Equal("S-1-5-21-1-2-3-1001", (string?)principal.Element(ns + "UserId"));
        Assert.Equal("InteractiveToken", (string?)principal.Element(ns + "LogonType"));
        Assert.Equal("HighestAvailable", (string?)principal.Element(ns + "RunLevel"));
        var s = doc.Descendants(ns + "Settings").Single();
        Assert.Equal("IgnoreNew", (string?)s.Element(ns + "MultipleInstancesPolicy"));
        Assert.Equal("false", (string?)s.Element(ns + "DisallowStartIfOnBatteries"));
        Assert.Equal("false", (string?)s.Element(ns + "StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", (string?)s.Element(ns + "ExecutionTimeLimit"));
        Assert.Equal("false", (string?)s.Element(ns + "AllowHardTerminate"));
        Assert.Equal("false", (string?)s.Element(ns + "StartWhenAvailable"));
        Assert.Equal("7", (string?)s.Element(ns + "Priority"));
        var exec = doc.Descendants(ns + "Exec").Single();
        Assert.Equal("\"C:\\Program Files\\NoClose\\NoClose.exe\"", (string?)exec.Element(ns + "Command"));
        Assert.Equal("--minimized", (string?)exec.Element(ns + "Arguments"));
    }

    [Fact]
    public void TaskNameIsInNoCloseFolder() => Assert.StartsWith(@"\NoClose\NoClose at logon (", StartupTask.TaskName);
}
