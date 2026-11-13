using NoClose.Core.Activation;
using NoClose.Core.Guards;
using NoClose.Core.Settings;
using NoClose.Core.Text;
using NoClose.Core.Time;

namespace NoClose.Tests;

public class ActivationPolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IndefiniteNeverStops()
    {
        var p = new ActivationPolicy(new FakeClock(T0));
        p.Begin(ActivationSpec.Indefinite, T0);
        Assert.Null(p.DeadlineUtc);
        Assert.False(p.ShouldStop(T0.AddDays(60)).Stop);
        Assert.Null(p.Remaining(T0));
    }

    [Fact]
    public void DurationDeadlineReached()
    {
        var p = new ActivationPolicy(new FakeClock(T0));
        p.Begin(ActivationSpec.ForDuration(TimeSpan.FromHours(2)), T0);
        Assert.Equal(T0.AddHours(2), p.DeadlineUtc);
        Assert.False(p.ShouldStop(T0.AddHours(2).AddSeconds(-1)).Stop);
        var d = p.ShouldStop(T0.AddHours(2));
        Assert.True(d.Stop);
        Assert.Equal("duration elapsed", d.Reason);
        Assert.Equal(TimeSpan.FromMinutes(30), p.Remaining(T0.AddMinutes(90)));
        Assert.Equal(TimeSpan.Zero, p.Remaining(T0.AddHours(5)));
    }

    [Fact]
    public void UntilUsesClockTimeZone()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var clock = new FakeClock(T0, tz); // 14:00 local
        var p = new ActivationPolicy(clock);
        p.Begin(ActivationSpec.ForUntil(new DateTime(2026, 11, 12, 18, 0, 0)), T0);
        Assert.Equal(new DateTime(2026, 11, 12, 16, 0, 0, DateTimeKind.Utc), p.DeadlineUtc);
        Assert.True(p.CheckDeadline(new DateTime(2026, 11, 12, 16, 0, 0, DateTimeKind.Utc)).Stop);
        Assert.Equal("end time reached", p.CheckDeadline(new DateTime(2026, 11, 12, 17, 0, 0, DateTimeKind.Utc)).Reason);
    }

    [Fact]
    public void ProcessNeedsTwoConsecutiveMisses()
    {
        var p = new ActivationPolicy(new FakeClock(T0));
        p.Begin(ActivationSpec.ParseProcess("build.exe"), T0);
        Assert.False(p.ShouldStop(T0, true).Stop);
        Assert.False(p.ShouldStop(T0.AddSeconds(5), false).Stop);   // first miss: grace
        Assert.False(p.ShouldStop(T0.AddSeconds(10), true).Stop);   // seen again: counter reset
        Assert.False(p.ShouldStop(T0.AddSeconds(15), false).Stop);
        var d = p.ShouldStop(T0.AddSeconds(20), false);             // second consecutive miss
        Assert.True(d.Stop);
        Assert.Equal("watched process ended", d.Reason);
    }

    [Fact]
    public void DeadlineCheckDoesNotResetProcessGrace()
    {
        var p = new ActivationPolicy(new FakeClock(T0));
        p.Begin(ActivationSpec.ParseProcess("1234"), T0);
        Assert.False(p.ShouldStop(T0, false).Stop);
        Assert.False(p.CheckDeadline(T0.AddSeconds(1)).Stop);
        Assert.True(p.ShouldStop(T0.AddSeconds(5), false).Stop);
    }

    [Fact]
    public void EndedPolicyNeverStopsAgain()
    {
        var p = new ActivationPolicy(new FakeClock(T0));
        p.Begin(ActivationSpec.ForDuration(TimeSpan.FromMinutes(1)), T0);
        p.End();
        Assert.False(p.IsRunning);
        Assert.False(p.ShouldStop(T0.AddHours(1)).Stop);
    }

    [Fact]
    public void SpecDescriptions()
    {
        Assert.Equal("indefinitely", ActivationSpec.Indefinite.Describe());
        Assert.Equal("for 2h 0m", ActivationSpec.ForDuration(TimeSpan.FromHours(2)).Describe());
        Assert.Equal("while build.exe runs", ActivationSpec.ParseProcess("build").Describe());
        Assert.Equal("while process 42 runs", ActivationSpec.ParseProcess("42").Describe());
    }

    [Fact]
    public void FormatSpan()
    {
        Assert.Equal("1d 2h 3m", ActivationSpec.FormatSpan(new TimeSpan(1, 2, 3, 4)));
        Assert.Equal("5m 7s", ActivationSpec.FormatSpan(new TimeSpan(0, 5, 7)));
        Assert.Equal("0s", ActivationSpec.FormatSpan(TimeSpan.FromSeconds(-3)));
    }
}

public class SettingsTests
{
    [Fact]
    public void DefaultsMatchSpecification()
    {
        var s = AppSettings.CreateDefault();
        Assert.Equal("Long-running task", s.Reason);
        Assert.Equal(45, s.ReassertSeconds);
        Assert.Equal(5, s.GuardReapplyMinutes);
        Assert.True(s.RegisterForRestart);
        Assert.True(s.StartMinimized);
        Assert.False(s.AwayMode);
        Assert.True(s.GetGuard(GuardId.ExecutionState).Enabled);
        Assert.False(s.GetGuard(GuardId.ScreenSaver).Enabled);
        Assert.False(s.GetGuard(GuardId.ScreenSaver).Acknowledged);
        foreach (var id in new[] { GuardId.PowerPlan, GuardId.ActiveHours, GuardId.EndSession })
        {
            Assert.False(s.GetGuard(id).Enabled);
            Assert.False(s.GetGuard(id).Acknowledged);
        }
        var items = s.GetGuard(GuardId.PowerPlan).Items!;
        Assert.True(items["sleep"]);
        Assert.False(items["consoleLockDisplayOff"]);
        Assert.False(items["allowAwayMode"]);
    }

    [Fact]
    public void RoundTripUsesCamelCaseAndStringEnums()
    {
        var s = AppSettings.CreateDefault();
        s.DefaultActivation.Kind = ActivationKind.Duration;
        s.DefaultActivation.DurationMinutes = 90;
        var json = SettingsSerializer.Serialize(s);
        Assert.Contains("\"reassertSeconds\": 45", json);
        Assert.Contains("\"kind\": \"Duration\"", json);
        Assert.Contains("\"ExecutionState\"", json);
        var back = SettingsSerializer.TryDeserialize(json, out var err);
        Assert.NotNull(back);
        Assert.Null(err);
        Assert.Equal(ActivationKind.Duration, back!.DefaultActivation.Kind);
        Assert.Equal(TimeSpan.FromMinutes(90), back.DefaultActivation.ToSpec().Duration);
    }

    [Fact]
    public void UnknownFieldsIgnoredAndMissingFieldsDefaulted()
    {
        var s = SettingsSerializer.TryDeserialize("{\"future\": 1, \"awayMode\": true}", out _);
        Assert.NotNull(s);
        Assert.True(s!.AwayMode);
        Assert.Equal(45, s.ReassertSeconds);
        Assert.True(s.GetGuard(GuardId.PowerPlan).Items!.ContainsKey("display"));
    }

    [Fact]
    public void ValuesAreClamped()
    {
        var s = SettingsSerializer.TryDeserialize("{\"reassertSeconds\": 1, \"guardReapplyMinutes\": 99999, \"log\": {\"retainDays\": 0, \"maxFileMB\": 5000}}", out _)!;
        Assert.Equal(15, s.ReassertSeconds);
        Assert.Equal(120, s.GuardReapplyMinutes);
        Assert.Equal(1, s.Log.RetainDays);
        Assert.Equal(200, s.Log.MaxFileMB);
        var hi = SettingsSerializer.TryDeserialize("{\"reassertSeconds\": 9999}", out _)!;
        Assert.Equal(300, hi.ReassertSeconds);
    }

    [Fact]
    public void InvalidJsonReturnsNull()
    {
        Assert.Null(SettingsSerializer.TryDeserialize("{ not json", out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void StoreRenamesBadFileAndUsesDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "noclose-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ broken");
            var store = new SettingsStore(path);
            var s = store.Load(out var warning);
            Assert.NotNull(warning);
            Assert.Equal(45, s.ReassertSeconds);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(dir, "settings.json.bad-*"));

            s.AwayMode = true;
            store.Save(s);
            Assert.True(store.Load(out _).AwayMode);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GuardActiveRequiresAcknowledgementWhenChangingSettings()
    {
        var s = AppSettings.CreateDefault();
        s.GetGuard(GuardId.ActiveHours).Enabled = true;
        Assert.False(s.IsGuardActive(GuardId.ActiveHours, changesSettings: true));
        s.GetGuard(GuardId.ActiveHours).Acknowledged = true;
        Assert.True(s.IsGuardActive(GuardId.ActiveHours, changesSettings: true));
        Assert.True(s.IsGuardActive(GuardId.ExecutionState, changesSettings: false));
    }
}

public class ReasonTextTests
{
    [Theory]
    [InlineData(null, "Long-running task")]
    [InlineData("", "Long-running task")]
    [InlineData("   ", "Long-running task")]
    [InlineData("\u0001\u0002", "Long-running task")]
    [InlineData("  Nightly build  ", "Nightly build")]
    public void Sanitize(string? input, string expected) => Assert.Equal(expected, ReasonText.Sanitize(input));

    [Fact]
    public void TruncatesTo120() => Assert.Equal(120, ReasonText.Sanitize(new string('a', 500)).Length);

    [Fact]
    public void Prefixes()
    {
        Assert.Equal("NoClose: job", ReasonText.PowerRequestReason("job"));
        Assert.Equal("NoClose is keeping this session open: job", ReasonText.ShutdownBlockReason("job"));
    }
}

public class ThrottleTests
{
    [Fact]
    public void IdenticalKeyOncePerInterval()
    {
        var clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var t = new Throttle(TimeSpan.FromHours(1), clock);
        Assert.True(t.TryPass("a"));
        Assert.False(t.TryPass("a"));
        Assert.True(t.TryPass("b"));
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(t.TryPass("a"));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(t.TryPass("a"));
    }
}
