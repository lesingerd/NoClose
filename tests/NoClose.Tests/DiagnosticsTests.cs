using NoClose.Core.Diagnostics;
using NoClose.Core.Diagnostics.Rules;
using NoClose.Core.Guards;

namespace NoClose.Tests;

public class DiagnosticsRuleTests
{
    private const RegHive M = RegHive.HKLM;
    private const RegHive U = RegHive.HKCU;

    private static List<DiagnosticFinding> Eval(IDiagnosticRule rule, FakeMachineReader r) => rule.Evaluate(r).ToList();

    [Fact]
    public void EmptyMachineHasNoHighFindings()
    {
        var findings = DiagnosticsEngine.Run(new FakeMachineReader());
        Assert.DoesNotContain(findings, f => f.Severity >= Severity.Medium);
    }

    [Fact]
    public void EveryRuleIdIsUniqueAndPresent()
    {
        var ids = DiagnosticsEngine.CreateRules(new EngineSnapshot(true, true, "x")).Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var id in new[] { "R-OVR", "R-ALLOW", "R-SLEEP", "R-MS", "R-LID", "R-SIGNIN-WAKE", "R-INACT", "R-SS-POL", "R-SS-USER",
                     "R-MDM-LOCK", "R-DYNLOCK", "R-SCARD", "R-RDP", "R-RDP-LIMITS", "R-WU-REBOOT", "R-WU-HOURS", "R-WU-POLICY",
                     "R-MGMT", "R-ARSO", "R-REQ", "R-ENGINE" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void FailingRuleBecomesInfoFinding()
    {
        var r = new ThrowingReader();
        var findings = DiagnosticsEngine.Run(r);
        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal(Severity.Info, f.Severity));
        Assert.Contains(findings, f => f.Detail.StartsWith("rule failed:"));
    }

    [Fact]
    public void ResultsSortedBySeverityDescending()
    {
        var r = new FakeMachineReader { Caps = new PowerCaps(false, false, true), OnAcPower = false, WuaRebootRequired = true };
        var findings = DiagnosticsEngine.Run(r);
        Assert.Equal(findings.OrderByDescending(f => f.Severity).Select(f => f.Severity), findings.Select(f => f.Severity));
        Assert.Equal(Severity.High, findings[0].Severity);
    }

    // ---- R-OVR ----------------------------------------------------------------------------------------------

    [Fact]
    public void OverrideListingNoCloseIsHigh()
    {
        var r = new FakeMachineReader().Set(M, @"SYSTEM\CurrentControlSet\Control\Power\PowerRequestOverride\Process", "noclose.exe", new[] { "DISPLAY", "SYSTEM" });
        var f = Assert.Single(Eval(new OverrideRule(), r));
        Assert.Equal(Severity.High, f.Severity);
        Assert.Contains("powercfg /requestsoverride", f.Detail);
        Assert.False(f.OrganisationControlled);
    }

    [Fact]
    public void OverrideForOtherProgramsIgnored()
    {
        var r = new FakeMachineReader().Set(M, @"SYSTEM\CurrentControlSet\Control\Power\PowerRequestOverride\Process", "chrome.exe", new[] { "SYSTEM" });
        Assert.Empty(Eval(new OverrideRule(), r));
    }

    // ---- R-ALLOW --------------------------------------------------------------------------------------------

    [Fact]
    public void PlanDisallowingRequestsIsHighAndAddressedByPowerPlan()
    {
        var r = new FakeMachineReader().Power(PowerGuids.SubSleep, PowerGuids.AllowSystemRequired, 0, 1);
        var f = Assert.Single(Eval(new AllowRequestsRule(), r), x => x.Severity == Severity.High);
        Assert.Equal(GuardId.PowerPlan, f.AddressedBy);
        Assert.False(f.OrganisationControlled);
        Assert.Contains("plugged in", f.Title);
    }

    [Fact]
    public void PolicyDisallowingRequestsIsOrganisationControlled()
    {
        var r = new FakeMachineReader().Set(M, PowerGuids.PolicyKeyFor(PowerGuids.AllowDisplayRequired), "DCSettingIndex", 0);
        var f = Assert.Single(Eval(new AllowRequestsRule(), r));
        Assert.Equal(Severity.High, f.Severity);
        Assert.True(f.OrganisationControlled);
        Assert.Null(f.AddressedBy);
        Assert.NotNull(f.PolicyName);
        Assert.Contains("ask them to adjust it", f.Advice);
    }

    [Fact]
    public void PolicyEnablingRequestsReportsNothing()
    {
        var r = new FakeMachineReader().Set(M, PowerGuids.PolicyKeyFor(PowerGuids.AllowSystemRequired), "ACSettingIndex", 1);
        Assert.Empty(Eval(new AllowRequestsRule(), r));
    }

    [Fact]
    public void AwayModeDisallowedIsOnlyLow()
    {
        var r = new FakeMachineReader().Power(PowerGuids.SubSleep, PowerGuids.AllowAwayMode, 0, 0);
        Assert.All(Eval(new AllowRequestsRule(), r), f => Assert.Equal(Severity.Low, f.Severity));
    }

    // ---- R-SLEEP / R-MS / R-LID ----------------------------------------------------------------------------

    [Fact]
    public void SleepTimeoutsListed()
    {
        var r = new FakeMachineReader().Power(PowerGuids.SubSleep, PowerGuids.StandbyIdle, 1800, 900);
        var f = Assert.Single(Eval(new SleepTimeoutRule(), r));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("30 min", f.Detail);
        Assert.Contains("15 min", f.Detail);
    }

    [Fact]
    public void SleepPolicyMakesItOrganisationControlled()
    {
        var r = new FakeMachineReader().Set(M, PowerGuids.PolicyKeyFor(PowerGuids.VideoIdle), "ACSettingIndex", 300);
        var f = Assert.Single(Eval(new SleepTimeoutRule(), r));
        Assert.True(f.OrganisationControlled);
        Assert.Equal(Severity.Low, f.Severity);
    }

    [Fact]
    public void ModernStandbyOnBatteryIsHighOnAcIsInfo()
    {
        var dc = new FakeMachineReader { Caps = new PowerCaps(false, true, true), OnAcPower = false };
        Assert.Equal(Severity.High, Assert.Single(Eval(new ModernStandbyRule(), dc)).Severity);
        var ac = new FakeMachineReader { Caps = new PowerCaps(false, true, true), OnAcPower = true };
        Assert.Equal(Severity.Info, Assert.Single(Eval(new ModernStandbyRule(), ac)).Severity);
        Assert.Empty(Eval(new ModernStandbyRule(), new FakeMachineReader()));
    }

    [Fact]
    public void LidActionSleepIsLow()
    {
        var r = new FakeMachineReader().Power(PowerGuids.SubButtons, PowerGuids.LidAction, 0, 1);
        var f = Assert.Single(Eval(new LidRule(), r));
        Assert.Equal(Severity.Low, f.Severity);
        Assert.Contains("on battery", f.Detail);
        Assert.Empty(Eval(new LidRule(), new FakeMachineReader().Power(PowerGuids.SubButtons, PowerGuids.LidAction, 0, 0)));
    }

    [Fact]
    public void SignInOnWake()
    {
        var plan = new FakeMachineReader().Power(PowerGuids.SubNone, PowerGuids.ConsoleLock, 1, 1);
        var f = Assert.Single(Eval(new SignInOnWakeRule(), plan));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.False(f.OrganisationControlled);

        var pol = new FakeMachineReader().Set(M, PowerGuids.PolicyKeyFor(PowerGuids.ConsoleLock), "ACSettingIndex", 1);
        Assert.True(Assert.Single(Eval(new SignInOnWakeRule(), pol)).OrganisationControlled);

        Assert.Empty(Eval(new SignInOnWakeRule(), new FakeMachineReader().Power(PowerGuids.SubNone, PowerGuids.ConsoleLock, 0, 0)));
    }

    // ---- locking policies ----------------------------------------------------------------------------------

    [Fact]
    public void InactivityLimitMedium()
    {
        var r = new FakeMachineReader().Set(M, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "InactivityTimeoutSecs", 900);
        var f = Assert.Single(Eval(new InactivityLimitRule(), r));
        Assert.Equal(Severity.Medium, f.Severity);
        Assert.True(f.OrganisationControlled);
        Assert.Contains("15", f.Title);
        Assert.Contains("Machine inactivity limit", f.PolicyName);
        Assert.Empty(Eval(new InactivityLimitRule(), new FakeMachineReader().Set(M, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "InactivityTimeoutSecs", 0)));
    }

    [Fact]
    public void ScreenSaverPolicy()
    {
        var r = new FakeMachineReader()
            .Set(U, ScreenSaverPolicyRule.Key, "ScreenSaveActive", "1")
            .Set(U, ScreenSaverPolicyRule.Key, "ScreenSaveTimeOut", "600");
        var f = Assert.Single(Eval(new ScreenSaverPolicyRule(), r));
        Assert.Equal(Severity.Medium, f.Severity);
        Assert.True(f.OrganisationControlled);
        Assert.Contains("Enable screen saver", f.PolicyName);
        Assert.Contains("Screen saver timeout", f.PolicyName);
        Assert.Empty(Eval(new ScreenSaverPolicyRule(), new FakeMachineReader()));
    }

    [Fact]
    public void ScreenSaverUserSettingIsAddressedByGuard()
    {
        var r = new FakeMachineReader()
            .Set(U, @"Control Panel\Desktop", "ScreenSaveActive", "1")
            .Set(U, @"Control Panel\Desktop", "ScreenSaveTimeOut", "300")
            .Set(U, @"Control Panel\Desktop", "ScreenSaverIsSecure", "1");
        var f = Assert.Single(Eval(new ScreenSaverUserRule(), r));
        Assert.Equal(Severity.Low, f.Severity);
        Assert.Equal(GuardId.ScreenSaver, f.AddressedBy);
        Assert.Contains("5 minutes", f.Detail);
        Assert.Contains("sign-in", f.Detail);
    }

    [Fact]
    public void MdmDeviceLock()
    {
        var r = new FakeMachineReader().Set(M, @"SOFTWARE\Microsoft\PolicyManager\current\device\DeviceLock", "MaxInactivityTimeDeviceLock", 10);
        var f = Assert.Single(Eval(new MdmDeviceLockRule(), r));
        Assert.Equal(Severity.Medium, f.Severity);
        Assert.True(f.OrganisationControlled);
        Assert.Contains("MaxInactivityTimeDeviceLock", f.PolicyName);
    }

    [Fact]
    public void DynamicLockUserAndPolicy()
    {
        var user = new FakeMachineReader().Set(U, @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "EnableGoodbye", 1);
        var fu = Assert.Single(Eval(new DynamicLockRule(), user));
        Assert.False(fu.OrganisationControlled);
        Assert.Contains("Sign-in options", fu.Advice);
        var pol = new FakeMachineReader().Set(M, @"SOFTWARE\Policies\Microsoft\PassportForWork\DynamicLock", "DynamicLock", 1);
        Assert.True(Assert.Single(Eval(new DynamicLockRule(), pol)).OrganisationControlled);
        Assert.Empty(Eval(new DynamicLockRule(), new FakeMachineReader()));
    }

    [Theory]
    [InlineData("1", "lock")]
    [InlineData("2", "logoff")]
    [InlineData("3", "disconnect")]
    public void SmartCardRemoval(string value, string expectedWord)
    {
        var r = new FakeMachineReader().Set(M, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "ScRemoveOption", value);
        var f = Assert.Single(Eval(new SmartCardRemovalRule(), r));
        Assert.Contains(expectedWord, f.Detail);
        Assert.True(f.OrganisationControlled);
        Assert.Empty(Eval(new SmartCardRemovalRule(), new FakeMachineReader().Set(M, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "ScRemoveOption", "0")));
    }

    // ---- remote desktop ------------------------------------------------------------------------------------

    private const string TsMachine = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string TsUser = @"Software\Policies\Microsoft\Windows NT\Terminal Services";
    private const string Listener = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private static readonly SessionInfo Remote = new(true, 2, 0, 3, 1);

    [Fact]
    public void RemoteSessionIsMedium()
    {
        var f = Assert.Single(Eval(new RemoteSessionRule(), new FakeMachineReader { Session = Remote }));
        Assert.Equal(Severity.Medium, f.Severity);
        Assert.Contains("not affected by NoClose", f.Detail);
        Assert.Empty(Eval(new RemoteSessionRule(), new FakeMachineReader()));
    }

    [Fact]
    public void RdpLimitsMachinePolicyBeatsUserPolicyBeatsListener()
    {
        var r = new FakeMachineReader { Session = Remote }
            .Set(M, Listener, "MaxIdleTime", 60000)
            .Set(U, TsUser, "MaxIdleTime", 120000)
            .Set(M, TsMachine, "MaxIdleTime", 1800000);
        var f = Assert.Single(Eval(new RemoteLimitsRule(), r));
        Assert.Contains("30 min", f.Detail);
        Assert.Contains("machine policy", f.Detail);
        Assert.Equal(Severity.Medium, f.Severity);
        Assert.True(f.OrganisationControlled);
        Assert.Contains("Set time limit for active but idle Remote Desktop Services sessions", f.PolicyName);

        var userOnly = new FakeMachineReader { Session = Remote }.Set(M, Listener, "MaxIdleTime", 60000).Set(U, TsUser, "MaxIdleTime", 120000);
        Assert.Contains("user policy", Assert.Single(Eval(new RemoteLimitsRule(), userOnly)).Detail);
    }

    [Fact]
    public void RdpListenerLimitIgnoredWhenInheriting()
    {
        var inherit = new FakeMachineReader { Session = Remote }.Set(M, Listener, "MaxIdleTime", 60000).Set(M, Listener, "fInheritMaxIdleTime", 1);
        Assert.Empty(Eval(new RemoteLimitsRule(), inherit));
        var own = new FakeMachineReader { Session = Remote }.Set(M, Listener, "MaxIdleTime", 60000).Set(M, Listener, "fInheritMaxIdleTime", 0);
        var f = Assert.Single(Eval(new RemoteLimitsRule(), own));
        Assert.Contains("RDP-Tcp listener", f.Detail);
        Assert.False(f.OrganisationControlled);
    }

    [Fact]
    public void RdpResetBrokenMakesRemoteHighAndLocalInfo()
    {
        var remote = new FakeMachineReader { Session = Remote }
            .Set(M, TsMachine, "MaxDisconnectionTime", 3600000).Set(M, TsMachine, "fResetBroken", 1);
        var f = Assert.Single(Eval(new RemoteLimitsRule(), remote));
        Assert.Equal(Severity.High, f.Severity);
        Assert.Contains("End session when time limits are reached", f.PolicyName);

        var local = new FakeMachineReader()
            .Set(M, TsMachine, "MaxDisconnectionTime", 3600000).Set(M, TsMachine, "fResetBroken", 1);
        Assert.Equal(Severity.Info, Assert.Single(Eval(new RemoteLimitsRule(), local)).Severity);
    }

    [Fact]
    public void RdpNoLimitsNoFinding()
    {
        Assert.Empty(Eval(new RemoteLimitsRule(), new FakeMachineReader { Session = Remote }.Set(M, TsMachine, "MaxIdleTime", 0)));
    }

    // ---- Windows Update ------------------------------------------------------------------------------------

    [Fact]
    public void PendingRebootFromWuaOrKeysIsHigh()
    {
        var wua = new FakeMachineReader { WuaRebootRequired = true };
        var f = Assert.Single(Eval(new PendingRebootRule(), wua));
        Assert.Equal(Severity.High, f.Severity);
        Assert.Equal(GuardId.ActiveHours, f.AddressedBy);
        Assert.Contains("A restart is pending", f.Detail);

        var key = new FakeMachineReader().AddKey(M, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        Assert.Equal(Severity.High, Assert.Single(Eval(new PendingRebootRule(), key)).Severity);
        var cbs = new FakeMachineReader().AddKey(M, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        Assert.Equal(Severity.High, Assert.Single(Eval(new PendingRebootRule(), cbs)).Severity);
    }

    [Fact]
    public void PendingFileRenamesAloneNeverHigh()
    {
        var r = new FakeMachineReader().Set(M, @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations", new[] { "a", "b" });
        Assert.Equal(Severity.Info, Assert.Single(Eval(new PendingRebootRule(), r)).Severity);
        var vol = new FakeMachineReader().Set(M, @"SOFTWARE\Microsoft\Updates", "UpdateExeVolatile", 1);
        Assert.Equal(Severity.Medium, Assert.Single(Eval(new PendingRebootRule(), vol)).Severity);
        Assert.Empty(Eval(new PendingRebootRule(), new FakeMachineReader { WuaRebootRequired = false }));
    }

    [Fact]
    public void ActiveHoursRuleSeverity()
    {
        const string key = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        FakeMachineReader Hours(int hour, bool reboot)
        {
            var r = new FakeMachineReader { NowLocal = new DateTime(2026, 11, 12, hour, 0, 0), WuaRebootRequired = reboot }
                .Set(M, key, "ActiveHoursStart", 8).Set(M, key, "ActiveHoursEnd", 17).Set(M, key, "SmartActiveHoursState", 0);
            return r;
        }
        Assert.Equal(Severity.Low, Assert.Single(Eval(new ActiveHoursRule(), Hours(10, true))).Severity);
        Assert.Equal(Severity.Low, Assert.Single(Eval(new ActiveHoursRule(), Hours(22, false))).Severity);
        var f = Assert.Single(Eval(new ActiveHoursRule(), Hours(22, true)));
        Assert.Equal(Severity.Medium, f.Severity);
        Assert.Equal(GuardId.ActiveHours, f.AddressedBy);
        Assert.Empty(Eval(new ActiveHoursRule(), new FakeMachineReader()));
    }

    [Fact]
    public void UpdateDeadlinePolicyIsMediumAndOrganisationControlled()
    {
        var r = new FakeMachineReader().Set(M, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ConfigureDeadlineForQualityUpdates", 2);
        var f = Assert.Single(Eval(new UpdatePolicyRule(), r));
        Assert.Equal(Severity.Medium, f.Severity);
        Assert.True(f.OrganisationControlled);
        Assert.Contains("regardless of active hours", f.Detail);
        Assert.Contains("Specify deadlines", f.PolicyName);

        var csp = new FakeMachineReader().Set(M, @"SOFTWARE\Microsoft\PolicyManager\current\device\Update", "ActiveHoursStart", 8)
            .Set(M, @"SOFTWARE\Microsoft\PolicyManager\current\device\Update", "ActiveHoursStart_ProviderSet", 1);
        var fc = Assert.Single(Eval(new UpdatePolicyRule(), csp));
        Assert.Equal(Severity.Info, fc.Severity);
        Assert.Contains("CSP ActiveHoursStart", fc.Detail);
        Assert.DoesNotContain("_ProviderSet", fc.Detail);
    }

    [Fact]
    public void ManagedDeviceDetection()
    {
        var mdm = new FakeMachineReader().Set(M, @"SOFTWARE\Microsoft\Enrollments\GUID1", "ProviderID", "MS DM Server");
        Assert.Contains("MDM", Assert.Single(Eval(new ManagedDeviceRule(), mdm)).Detail);
        var domain = new FakeMachineReader { UserDomainName = "CORP", MachineName = "PC1" };
        Assert.Contains("domain", Assert.Single(Eval(new ManagedDeviceRule(), domain)).Detail);
        var wsus = new FakeMachineReader().Set(M, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer", "http://wsus");
        Assert.Contains("WSUS", Assert.Single(Eval(new ManagedDeviceRule(), wsus)).Detail);
        Assert.Empty(Eval(new ManagedDeviceRule(), new FakeMachineReader()));
    }

    [Fact]
    public void AutomaticRestartSignOn()
    {
        var r = new FakeMachineReader().Set(M, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "DisableAutomaticRestartSignOn", 1);
        var f = Assert.Single(Eval(new AutoRestartSignOnRule(), r));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("will not be resumed automatically", f.Detail);
    }

    // ---- misc ----------------------------------------------------------------------------------------------

    [Fact]
    public void PowercfgRequestsRule()
    {
        Assert.Empty(Eval(new PowercfgRequestsRule(), new FakeMachineReader()));
        var f = Assert.Single(Eval(new PowercfgRequestsRule(), new FakeMachineReader { PowercfgRequests = "DISPLAY:\n[PROCESS] NoClose.exe\nNoClose: Nightly build" }));
        Assert.Contains("\"NoClose:\" appears", f.Detail);
        var g = Assert.Single(Eval(new PowercfgRequestsRule(), new FakeMachineReader { PowercfgRequests = "None." }));
        Assert.Contains("No NoClose request", g.Detail);
    }

    [Fact]
    public void EngineRuleOnlyWhenActiveAndDegraded()
    {
        var r = new FakeMachineReader();
        Assert.Equal(Severity.High, Assert.Single(Eval(new EngineRule(new EngineSnapshot(true, true, "boom")), r)).Severity);
        Assert.Empty(Eval(new EngineRule(new EngineSnapshot(true, false, null)), r));
        Assert.Empty(Eval(new EngineRule(new EngineSnapshot(false, true, "x")), r));
    }

    [Fact]
    public void OrganisationControlledFindingsAlwaysCarryPolicyNameAndStandardAdvice()
    {
        var r = new FakeMachineReader { Session = Remote, UserDomainName = "CORP" }
            .Set(M, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "InactivityTimeoutSecs", 600)
            .Set(M, TsMachine, "MaxIdleTime", 600000)
            .Set(U, ScreenSaverPolicyRule.Key, "ScreenSaveActive", "1")
            .Set(M, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ConfigureDeadlineForFeatureUpdates", 7);
        var org = DiagnosticsEngine.Run(r).Where(f => f.OrganisationControlled).ToList();
        Assert.True(org.Count >= 4);
        Assert.All(org, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.PolicyName));
            Assert.Contains("NoClose does not change it", f.Advice);
            Assert.Contains("IT administrator", f.Advice);
        });
    }

    private sealed class ThrowingReader : IMachineReader
    {
        public object? ReadRegistry(RegHive hive, string subKey, string valueName, RegView view = RegView.Default) => throw new InvalidOperationException("nope");
        public bool KeyExists(RegHive hive, string subKey) => throw new InvalidOperationException("nope");
        public IReadOnlyList<string> SubKeyNames(RegHive hive, string subKey) => throw new InvalidOperationException("nope");
        public IReadOnlyList<string> ValueNames(RegHive hive, string subKey) => throw new InvalidOperationException("nope");
        public uint? ReadPowerValue(Guid scheme, Guid sub, Guid setting, bool ac) => throw new InvalidOperationException("nope");
        public Guid? ActiveScheme => throw new InvalidOperationException("nope");
        public PowerCaps Caps => throw new InvalidOperationException("nope");
        public bool OnAcPower => throw new InvalidOperationException("nope");
        public SessionInfo Session => throw new InvalidOperationException("nope");
        public bool? WuaRebootRequired => throw new InvalidOperationException("nope");
        public string? PowercfgRequests => throw new InvalidOperationException("nope");
        public DateTime NowLocal => throw new InvalidOperationException("nope");
        public string MachineName => throw new InvalidOperationException("nope");
        public string UserName => throw new InvalidOperationException("nope");
        public string UserDomainName => throw new InvalidOperationException("nope");
    }
}

public class DiagnosticsTextFormatterTests
{
    [Fact]
    public void SnapshotStyleOutput()
    {
        var summary = new MachineSummary(new DateTimeOffset(2026, 11, 12, 14, 3, 22, TimeSpan.FromHours(1)), "PC", "alice", true, "1 (console)", "AC", false,
            "Balanced {381b4222-f694-41f0-9685-ff5bb260df2e}");
        var findings = new[]
        {
            new DiagnosticFinding("R-WU-REBOOT", Severity.High, "A restart is pending", "line one\nline two", null, "Restart soon.", GuardId.ActiveHours, false),
            new DiagnosticFinding("R-INACT", Severity.Medium, "Limit", "d", "Interactive logon: Machine inactivity limit", "Ask IT.", null, true),
        };
        var text = DiagnosticsTextFormatter.Format(findings, summary).Replace("\r\n", "\n");
        var expected =
            "NoClose diagnostics — 2026-11-12 14:03:22 (+01:00)\n" +
            "Machine: PC  User: alice  Elevated: yes  Session: 1 (console)\n" +
            "Power: AC  Modern Standby: no  Active scheme: Balanced {381b4222-f694-41f0-9685-ff5bb260df2e}\n" +
            "[HIGH]   R-WU-REBOOT  A restart is pending\n" +
            "         line one\n" +
            "         line two\n" +
            "         Policy: —   Addressed by: Active hours\n" +
            "         Advice: Restart soon.\n" +
            "[MEDIUM] R-INACT  Limit\n" +
            "         d\n" +
            "         Policy: Interactive logon: Machine inactivity limit   Addressed by: —\n" +
            "         Advice: Ask IT.\n";
        Assert.Equal(expected, text);
    }

    [Fact]
    public void NoFindingsMessage()
    {
        var summary = new MachineSummary(DateTimeOffset.UnixEpoch, "PC", "u", false, "1", "Battery", true, "x");
        var text = DiagnosticsTextFormatter.Format(Array.Empty<DiagnosticFinding>(), summary);
        Assert.Contains("No findings.", text);
        Assert.Contains("Elevated: no", text);
        Assert.Contains("Modern Standby: yes", text);
    }

    [Fact]
    public void ReportBuildsFromReader()
    {
        var text = DiagnosticsReport.Build(new FakeMachineReader { WuaRebootRequired = true }, elevated: false, now: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("R-WU-REBOOT", text);
        Assert.Contains("Machine: PC", text);
        Assert.Contains("Active scheme: Balanced", text);
    }
}

public class ActiveHoursPlannerTests
{
    [Theory]
    [InlineData(8, 2)]
    [InlineData(0, 18)]
    [InlineData(6, 0)]    // wraps exactly to midnight
    [InlineData(23, 17)]  // wraps over midnight
    [InlineData(14, 8)]
    public void EighteenHourWindowWrapsAtMidnight(int startHour, int expectedEnd)
    {
        var w = ActiveHoursPlanner.Plan(new DateTime(2026, 11, 12, startHour, 37, 0));
        Assert.Equal(startHour, w.StartHour);
        Assert.Equal(expectedEnd, w.EndHour);
        Assert.Equal(18, w.LengthHours);
    }

    [Fact]
    public void RangeIsClampedToEighteenHours()
    {
        Assert.Equal(18, ActiveHoursPlanner.Plan(DateTime.Today, 24).LengthHours);
        Assert.Equal(1, ActiveHoursPlanner.Plan(DateTime.Today, 0).LengthHours);
    }

    [Theory]
    [InlineData(8, 2, 8, true)]
    [InlineData(8, 2, 23, true)]
    [InlineData(8, 2, 1, true)]
    [InlineData(8, 2, 2, false)]
    [InlineData(8, 2, 5, false)]
    [InlineData(8, 17, 17, false)]
    [InlineData(8, 17, 12, true)]
    [InlineData(5, 5, 5, false)]
    public void Contains(int start, int end, int hour, bool expected) =>
        Assert.Equal(expected, ActiveHoursPlanner.Contains(start, end, hour));

    [Fact]
    public void HoursLeft()
    {
        Assert.Equal(0, ActiveHoursPlanner.HoursLeft(8, 17, new DateTime(2026, 1, 1, 20, 0, 0)));
        Assert.Equal(5, ActiveHoursPlanner.HoursLeft(8, 17, new DateTime(2026, 1, 1, 12, 0, 0)), 3);
        Assert.Equal(4, ActiveHoursPlanner.HoursLeft(8, 2, new DateTime(2026, 1, 1, 22, 0, 0)), 3);
        Assert.Equal(2, ActiveHoursPlanner.HoursLeft(8, 2, new DateTime(2026, 1, 1, 0, 0, 0)), 3);
    }
}

public class EndSessionDecisionTests
{
    [Fact]
    public void OrdinaryLogoffIsBlockedOnlyWhenGuardApplied()
    {
        Assert.True(EndSessionDecision.ShouldBlock(true, EndSessionDecision.Logoff));
        Assert.True(EndSessionDecision.ShouldBlock(true, 0));
        Assert.False(EndSessionDecision.ShouldBlock(false, EndSessionDecision.Logoff));
    }

    [Fact]
    public void CriticalAndCloseAppAreAlwaysAllowed()
    {
        Assert.False(EndSessionDecision.ShouldBlock(true, EndSessionDecision.Critical));
        Assert.False(EndSessionDecision.ShouldBlock(true, EndSessionDecision.CloseApp));
        Assert.False(EndSessionDecision.ShouldBlock(true, EndSessionDecision.Critical | EndSessionDecision.Logoff));
    }

    [Fact]
    public void FlagsDescribed()
    {
        Assert.Contains("ENDSESSION_LOGOFF", EndSessionDecision.DescribeFlags(EndSessionDecision.Logoff));
        Assert.Contains("ENDSESSION_CRITICAL", EndSessionDecision.DescribeFlags(EndSessionDecision.Critical | EndSessionDecision.CloseApp));
        Assert.Contains("shutdown", EndSessionDecision.DescribeFlags(0));
    }
}

public class PowerPlanItemsTests
{
    [Fact]
    public void ItemTableMatchesSpecification()
    {
        var items = PowerPlanItems.All.ToDictionary(i => i.Key);
        Assert.Equal(8, items.Count);
        Assert.Equal(new Guid("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA"), items["sleep"].Setting);
        Assert.Equal(PowerGuids.SubSleep, items["sleep"].Subgroup);
        Assert.Equal(PowerGuids.SubVideo, items["display"].Subgroup);
        Assert.All(new[] { "sleep", "hibernate", "unattendedSleep", "display" }, k => Assert.Equal(0u, items[k].NewValue));
        Assert.All(new[] { "allowSystemRequired", "allowDisplayRequired", "allowAwayMode" }, k => Assert.Equal(1u, items[k].NewValue));
        Assert.False(items["consoleLockDisplayOff"].DefaultOn);
        Assert.False(items["allowAwayMode"].DefaultOn);
        Assert.True(items["allowSystemRequired"].DefaultOn);
        // the sign-in on wake setting is read-only for NoClose
        Assert.DoesNotContain(PowerPlanItems.All, i => i.Setting == PowerGuids.ConsoleLock);
    }

    [Fact]
    public void PolicyKeyFormat() =>
        Assert.Equal(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\A4B195F5-8225-47D8-8012-9D41369786E2", PowerGuids.PolicyKeyFor(PowerGuids.AllowSystemRequired));
}
