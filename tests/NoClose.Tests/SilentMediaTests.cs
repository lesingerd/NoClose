using System.Runtime.InteropServices;
using NoClose.Core.Activation;
using NoClose.Core.Cli;
using NoClose.Core.Diagnostics;
using NoClose.Core.Diagnostics.Rules;
using NoClose.Core.Guards;
using NoClose.Core.Settings;
using NoClose.Guards;
using NoClose.Logging;
using NoClose.Platform.Interop;

namespace NoClose.Tests;

/// <summary>Silent media session: pure logic only. No test opens an audio stream or touches an audio device.</summary>
public class SilentMediaRulesTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 60)]
    [InlineData(500, 60)]
    public void BackoffScheduleCapsAtSixtySeconds(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SilentMediaRules.BackoffDelay(failures));

    [Fact]
    public void BackoffNeverDecreases()
    {
        var last = TimeSpan.Zero;
        for (int i = 1; i < 20; i++)
        {
            var d = SilentMediaRules.BackoffDelay(i);
            Assert.True(d >= last);
            last = d;
        }
    }

    [Fact]
    public void HresultClassification()
    {
        Assert.True(SilentMediaRules.IsDeviceLost(unchecked((int)0x88890004)));
        Assert.True(SilentMediaRules.IsDeviceLost(unchecked((int)0x88890010)));
        Assert.False(SilentMediaRules.IsDeviceLost(0));
        Assert.False(SilentMediaRules.IsDeviceLost(unchecked((int)0x80004005)));
        Assert.True(SilentMediaRules.IsNoEndpoint(unchecked((int)0x80070490)));
        Assert.False(SilentMediaRules.IsNoEndpoint(unchecked((int)0x88890004)));
        Assert.Contains("0x88890004", SilentMediaRules.DescribeHr(unchecked((int)0x88890004)));
        Assert.Contains("audio service", SilentMediaRules.DescribeHr(unchecked((int)0x88890010)));
        Assert.Contains("0x80004005", SilentMediaRules.DescribeHr(unchecked((int)0x80004005)));
    }

    [Fact]
    public void StateToGuardStatus()
    {
        Assert.Equal(GuardStatus.Applied, SilentMediaRules.ToGuardStatus(new(SilentMediaPhase.Running, "", 0)).Status);
        Assert.Equal(GuardStatus.Degraded, SilentMediaRules.ToGuardStatus(new(SilentMediaPhase.Starting, "", 0)).Status);
        var retry = SilentMediaRules.ToGuardStatus(new(SilentMediaPhase.RetryWait, "lost", 2));
        Assert.Equal(GuardStatus.Degraded, retry.Status);
        Assert.Equal("lost", retry.Message);
        Assert.Equal(GuardStatus.Degraded, SilentMediaRules.ToGuardStatus(new(SilentMediaPhase.NoDevice, "no device", 1)).Status);
        Assert.Equal(GuardStatus.Off, SilentMediaRules.ToGuardStatus(SilentMediaState.Stopped).Status);
        Assert.Equal("Degraded", new GuardState(GuardId.SilentMedia, GuardStatus.Degraded, "").ChipText);
    }
}

public class SilentMediaSettingsTests
{
    [Fact]
    public void DefaultIsOffAndNeedsNoAcknowledgement()
    {
        var s = AppSettings.CreateDefault();
        Assert.False(s.GetGuard(GuardId.SilentMedia).Enabled);
        Assert.False(s.IsGuardActive(GuardId.SilentMedia, changesSettings: false));
        s.GetGuard(GuardId.SilentMedia).Enabled = true;
        Assert.True(s.IsGuardActive(GuardId.SilentMedia, changesSettings: false)); // does not change settings: no acknowledgement
    }

    [Fact]
    public void ExistingSettingsFileWithoutTheGuardMigratesToOff()
    {
        const string old = "{\"schemaVersion\":1,\"guards\":{\"ExecutionState\":{\"enabled\":true,\"acknowledged\":true},\"EndSession\":{\"enabled\":true}}}";
        var s = SettingsSerializer.TryDeserialize(old, out var err)!;
        Assert.Null(err);
        Assert.True(s.Guards.ContainsKey("SilentMedia"));
        Assert.False(s.GetGuard(GuardId.SilentMedia).Enabled);
        Assert.True(s.GetGuard(GuardId.EndSession).Enabled); // existing choices untouched
    }

    [Fact]
    public void EnabledStateRoundTrips()
    {
        var s = AppSettings.CreateDefault();
        s.GetGuard(GuardId.SilentMedia).Enabled = true;
        var json = SettingsSerializer.Serialize(s);
        Assert.Contains("\"SilentMedia\"", json);
        var back = SettingsSerializer.TryDeserialize(json, out _)!;
        Assert.True(back.GetGuard(GuardId.SilentMedia).Enabled);
        Assert.False(back.GetGuard(GuardId.ScreenSaver).Enabled);
    }
}

public class SilentMediaCliTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 14, 0, 0);

    [Theory]
    [InlineData("silentmedia")]
    [InlineData("SilentMedia")]
    [InlineData("SILENTMEDIA")]
    public void GuardsTokenIsCaseInsensitive(string token)
    {
        var r = CliParser.Parse(new[] { "--start", "--guards", "ExecutionState," + token }, Now);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(new[] { GuardId.ExecutionState, GuardId.SilentMedia }, r.Command!.GuardOverride);
    }

    [Fact]
    public void HelpMentionsTheToken() => Assert.Contains("SilentMedia", CliParser.Usage);

    [Fact]
    public void ElevatedCopyRoundTripsTheGuard()
    {
        var args = ActivationArgs.ForElevatedCopy(true, true, ActivationSpec.Indefinite, "Nightly build", null,
            new[] { GuardId.ExecutionState, GuardId.SilentMedia }, "S-1-5-21-1");
        Assert.Contains("ExecutionState,SilentMedia", args);
        var c = CliParser.Parse(args.ToArray(), Now).Command!;
        Assert.Equal(new[] { GuardId.ExecutionState, GuardId.SilentMedia }, c.GuardOverride);
    }
}

public class SilentMediaDiagnosticsTests
{
    private static List<DiagnosticFinding> Eval(SilentMediaSnapshot? s, bool? endpoint) =>
        new SilentMediaRule(s).Evaluate(new FakeMachineReader { HasAudioRenderEndpoint = endpoint }).ToList();

    [Fact]
    public void RuleIsRegistered() =>
        Assert.Contains("R-MEDIA", DiagnosticsEngine.CreateRules(null, new SilentMediaSnapshot(true, SilentMediaPhase.Running, null)).Select(r => r.Id));

    [Fact]
    public void ActiveSessionIsReportedAsInfo()
    {
        var f = Assert.Single(Eval(new SilentMediaSnapshot(true, SilentMediaPhase.Running, "ok"), true));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("is active", f.Title);
        Assert.Contains("audio output device is present", f.Detail);
        Assert.Contains("does not replace", f.Detail);
        Assert.Equal(GuardId.SilentMedia, f.AddressedBy);
    }

    [Fact]
    public void SelectedButNotRunningIsLowAndNamesTheMissingEndpoint()
    {
        var f = Assert.Single(Eval(new SilentMediaSnapshot(true, SilentMediaPhase.NoDevice, "No audio output device is available."), false));
        Assert.Equal(Severity.Low, f.Severity);
        Assert.Contains("not active", f.Title);
        Assert.Contains("No audio output device was found", f.Detail);
        Assert.Contains("audio output device", f.Advice);
    }

    [Fact]
    public void OffStateReportsEndpointPresenceReadOnly()
    {
        var f = Assert.Single(Eval(new SilentMediaSnapshot(false, SilentMediaPhase.Stopped, null), true));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("is off", f.Title);
        Assert.Contains("audio output device is present", f.Detail);
        Assert.Contains("could not be determined", Assert.Single(Eval(null, null)).Detail);
    }

    [Fact]
    public void MissingSnapshotReportsUnknownNotOff()
    {
        var f = Assert.Single(Eval(null, true));
        Assert.Contains("state unknown", f.Title);
        Assert.DoesNotContain("is off", f.Title);
        Assert.Contains("audio output device is present", f.Detail);
    }

    [Fact]
    public void NeverRaisesAMediumOrHigherFinding()
    {
        foreach (var phase in Enum.GetValues<SilentMediaPhase>())
            foreach (bool? ep in new bool?[] { true, false, null })
                Assert.All(Eval(new SilentMediaSnapshot(true, phase, "x"), ep), f => Assert.True(f.Severity <= Severity.Low));
    }
}

public class SilentMediaInteropTests
{
    [Fact]
    public void WaveFormatExIs18BytesPacked()
    {
        Assert.Equal(18, Marshal.SizeOf<WAVEFORMATEX>());
        Assert.Equal(14, (int)Marshal.OffsetOf<WAVEFORMATEX>(nameof(WAVEFORMATEX.wBitsPerSample)).ToInt64());
        Assert.Equal(4, (int)Marshal.OffsetOf<WAVEFORMATEX>(nameof(WAVEFORMATEX.nSamplesPerSec)).ToInt64());
    }

    [Fact]
    public void PropertyKeyIs20Bytes() => Assert.Equal(20, Marshal.SizeOf<PROPERTYKEY>());

    [Fact]
    public void EnumsAndConstantsMatchWin32()
    {
        Assert.Equal(0, (int)EDataFlow.eRender);
        Assert.Equal(1, (int)ERole.eMultimedia);
        Assert.Equal(0, (int)AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED);
        Assert.Equal(0x00040000u, CoreAudioConstants.AUDCLNT_STREAMFLAGS_EVENTCALLBACK);
        Assert.Equal(0x2u, CoreAudioConstants.AUDCLNT_BUFFERFLAGS_SILENT);
        Assert.Equal(2_000_000L, CoreAudioConstants.BufferDuration); // 200 ms in 100 ns units
        Assert.Equal(1u, CoreAudioConstants.DEVICE_STATE_ACTIVE);
    }

    [Fact]
    public void InterfaceIdsMatchTheGuidAttributes()
    {
        Assert.Equal(CoreAudioConstants.IID_IMMDeviceEnumerator, typeof(IMMDeviceEnumerator).GUID);
        Assert.Equal(CoreAudioConstants.IID_IAudioClient, typeof(IAudioClient).GUID);
        Assert.Equal(CoreAudioConstants.IID_IAudioRenderClient, typeof(IAudioRenderClient).GUID);
        Assert.Equal(CoreAudioConstants.IID_IAudioSessionControl, typeof(IAudioSessionControl).GUID);
        Assert.Equal(new Guid("D666063F-1587-4E43-81F1-B948E807363F"), typeof(IMMDevice).GUID);
        Assert.Equal(new Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), typeof(IMMNotificationClient).GUID);
        Assert.Equal(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), CoreAudioConstants.CLSID_MMDeviceEnumerator);
    }

    [Fact]
    public void VtableOrderOfTheInterfaces()
    {
        // Core Audio vtable slots (after IUnknown) in declaration order; the generator numbers methods in declaration order.
        AssertOrder(typeof(IAudioClient), "Initialize", "GetBufferSize", "GetStreamLatency", "GetCurrentPadding", "IsFormatSupported",
            "GetMixFormat", "GetDevicePeriod", "Start", "Stop", "Reset", "SetEventHandle", "GetService");
        AssertOrder(typeof(IAudioRenderClient), "GetBuffer", "ReleaseBuffer");
        AssertOrder(typeof(IMMDeviceEnumerator), "EnumAudioEndpoints", "GetDefaultAudioEndpoint", "GetDevice",
            "RegisterEndpointNotificationCallback", "UnregisterEndpointNotificationCallback");
        AssertOrder(typeof(IMMDevice), "Activate", "OpenPropertyStore", "GetId", "GetState");
        AssertOrder(typeof(IMMNotificationClient), "OnDeviceStateChanged", "OnDeviceAdded", "OnDeviceRemoved", "OnDefaultDeviceChanged", "OnPropertyValueChanged");
        AssertOrder(typeof(IAudioSessionControl), "GetState", "GetDisplayName", "SetDisplayName", "GetIconPath", "SetIconPath",
            "GetGroupingParam", "SetGroupingParam", "RegisterAudioSessionNotification", "UnregisterAudioSessionNotification");
    }

    private static void AssertOrder(Type t, params string[] expected)
    {
        var actual = t.GetMethods().Where(m => !m.IsStatic).OrderBy(m => m.MetadataToken).Select(m => m.Name).ToArray();
        Assert.Equal(expected, actual);
    }
}

internal sealed class FakeSilentSession : ISilentMediaSession
{
    public SilentMediaState State { get; set; } = SilentMediaState.Stopped;
    public bool Started, Stopped, Disposed;
    public int EnsureCalls;
    public bool EnsureRestarts;
    public event Action? StateChanged;
    public void Raise() => StateChanged?.Invoke();
    public void Start() { Started = true; State = new(SilentMediaPhase.Running, "", 0); }
    public void Stop() { Stopped = true; State = SilentMediaState.Stopped; }
    public bool EnsureAlive() { EnsureCalls++; return EnsureRestarts; }
    public void Dispose() => Disposed = true;
}

public class SilentMediaGuardTests : GuardTestBase
{
    private static SilentMediaGuard Make(FakeSilentSession fake) => new(_ => fake);

    [Fact]
    public void MetadataDeclaresNoSettingsChangeAndNoAdmin()
    {
        var g = new SilentMediaGuard();
        Assert.Equal(GuardId.SilentMedia, g.Id);
        Assert.False(g.ChangesSettings);
        Assert.False(g.IsAdminRequired);
        Assert.Contains("complements", g.Description);
        Assert.Contains("does not replace", g.Description);
        Assert.True(g.CheckAvailability(Ctx(elevated: false)).CanApply);
    }

    [Fact]
    public void ManagerListsTheGuardAndItIsNotSelectedByDefault()
    {
        var ctx = Ctx();
        var m = new GuardManager(ctx);
        var g = m.Get(GuardId.SilentMedia);
        Assert.False(m.IsSelected(g));
        ctx.Settings.GetGuard(GuardId.SilentMedia).Enabled = true;
        Assert.True(m.IsSelected(g));
        m.SetOverride(new[] { GuardId.ExecutionState });
        Assert.False(m.IsSelected(g));
    }

    [Fact]
    public void ApplyStartsTheSessionAndReportsApplied()
    {
        var fake = new FakeSilentSession();
        var g = Make(fake);
        var r = g.Apply(Ctx());
        Assert.True(fake.Started);
        Assert.Equal(GuardStatus.Applied, r.Status);
        Assert.Empty(Journal.OpenEntries(GuardId.SilentMedia)); // nothing is journaled: nothing changes
    }

    [Fact]
    public void DegradedSessionIsReportedAndRecoveryFollowsOnSync()
    {
        var fake = new FakeSilentSession();
        var g = Make(fake);
        g.Apply(Ctx());
        fake.State = new(SilentMediaPhase.NoDevice, "No audio output device is available.", 1);
        var r = g.Sync();
        Assert.Equal(GuardStatus.Degraded, r.Status);
        Assert.Equal(GuardStatus.Degraded, g.Status);
        fake.State = new(SilentMediaPhase.Running, "", 0);
        Assert.Equal(GuardStatus.Applied, g.Sync().Status);
    }

    [Fact]
    public void ReapplyAsksTheSessionToVerifyItself()
    {
        var fake = new FakeSilentSession { EnsureRestarts = true };
        var g = Make(fake);
        var ctx = Ctx();
        g.Apply(ctx);
        var r = g.Reapply(ctx);
        Assert.Equal(1, fake.EnsureCalls);
        Assert.Equal(GuardStatus.Applied, r.Status);
        Assert.Contains(Log.Lines, l => l.Contains("restarted"));
    }

    [Fact]
    public void ReapplyBeforeApplyOrAfterRestoreDoesNothing()
    {
        var fake = new FakeSilentSession();
        var g = Make(fake);
        var ctx = Ctx();
        g.Reapply(ctx);
        Assert.Equal(0, fake.EnsureCalls);
        g.Apply(ctx);
        g.Restore(ctx);
        g.Reapply(ctx);
        Assert.Equal(0, fake.EnsureCalls);
    }

    [Fact]
    public void RestoreStopsTheSessionAndLateEventsDoNotRevive()
    {
        var fake = new FakeSilentSession();
        var g = Make(fake);
        var ctx = Ctx();
        g.Apply(ctx);
        var r = g.Restore(ctx);
        Assert.True(fake.Stopped);
        Assert.Equal(GuardStatus.Off, r.Status);
        fake.State = new(SilentMediaPhase.Running, "", 0); // a late event from the audio thread
        Assert.Equal(GuardStatus.Off, g.Sync().Status);
    }

    [Fact]
    public void SessionChangesAreForwardedToTheHost()
    {
        var fake = new FakeSilentSession();
        var g = Make(fake);
        int raised = 0;
        g.SessionChanged += () => raised++;
        g.Apply(Ctx());
        fake.Raise();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SnapshotReflectsSelectionAndPhase()
    {
        var fake = new FakeSilentSession();
        var g = Make(fake);
        Assert.Equal(new SilentMediaSnapshot(false, SilentMediaPhase.Stopped, null), g.Snapshot(false));
        g.Apply(Ctx());
        Assert.Equal(SilentMediaPhase.Running, g.Snapshot(true).Phase);
        Assert.True(g.Snapshot(true).Selected);
    }
}
