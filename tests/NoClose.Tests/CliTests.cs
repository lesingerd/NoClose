using NoClose.Core.Activation;
using NoClose.Core.Cli;
using NoClose.Core.Guards;

namespace NoClose.Tests;

public class CliParserTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 14, 0, 0);

    private static CliCommand Ok(params string[] args)
    {
        var r = CliParser.Parse(args, Now);
        Assert.True(r.Ok, r.Error);
        return r.Command!;
    }

    private static string Err(params string[] args)
    {
        var r = CliParser.Parse(args, Now);
        Assert.False(r.Ok);
        return r.Error!;
    }

    [Fact]
    public void NoArgsIsNone() => Assert.Equal(CliVerb.None, Ok().Verb);

    [Fact]
    public void StartDefaultsToIndefinite()
    {
        var c = Ok("--start");
        Assert.Equal(CliVerb.Start, c.Verb);
        Assert.Equal(ActivationKind.Indefinite, c.Activation!.Kind);
    }

    [Fact]
    public void DurationImpliesStart()
    {
        var c = Ok("--duration", "1h30m");
        Assert.Equal(CliVerb.Start, c.Verb);
        Assert.Equal(TimeSpan.FromMinutes(90), c.Activation!.Duration);
    }

    [Fact]
    public void UntilImpliesStartAndRollsToTomorrow()
    {
        var c = Ok("--until", "09:00");
        Assert.Equal(new DateTime(2026, 11, 13, 9, 0, 0), c.Activation!.UntilLocal);
        Assert.Equal(CliVerb.Start, c.Verb);
    }

    [Fact]
    public void WhileProcessPidAndName()
    {
        Assert.Equal(1234, Ok("--while-process", "1234").Activation!.ProcessId);
        var named = Ok("--while-process", "Build.EXE").Activation!;
        Assert.Equal("Build", named.ProcessName);
        Assert.Null(named.ProcessId);
    }

    [Fact]
    public void SlashAliasAndCaseInsensitive()
    {
        Assert.Equal(CliVerb.Stop, Ok("/STOP").Verb);
        Assert.Equal(CliVerb.Diagnose, Ok("--Diagnose").Verb);
        Assert.Equal(CliVerb.Help, Ok("-?").Verb);
        Assert.Equal(CliVerb.Help, Ok("/?").Verb);
    }

    [Fact]
    public void EqualsSyntax() => Assert.Equal(TimeSpan.FromHours(8), Ok("--duration=8h").Activation!.Duration);

    [Fact]
    public void ReasonIsSanitized()
    {
        var c = Ok("--start", "--reason", "Build\u0007 server\r\nrun");
        Assert.Equal("Build serverrun", c.Reason);
        var long120 = Ok("--reason", new string('x', 300)).Reason!;
        Assert.Equal(120, long120.Length);
    }

    [Fact]
    public void GuardsList()
    {
        var c = Ok("--start", "--guards", "PowerPlan, screensaver");
        Assert.Equal(new[] { GuardId.PowerPlan, GuardId.ScreenSaver }, c.GuardOverride);
        Assert.Contains("Unknown guard", Err("--guards", "Nope"));
    }

    [Fact]
    public void OutWithDiagnoseAndRestore()
    {
        Assert.Equal("x.txt", Ok("--diagnose", "--out", "x.txt").OutPath);
        Assert.Equal(CliVerb.Restore, Ok("--restore", "--out", "r.txt").Verb);
    }

    [Fact]
    public void InternalSwitches()
    {
        var c = Ok("--handoff", "--restored-after-restart", "--minimized");
        Assert.True(c.Handoff);
        Assert.True(c.RestoredAfterRestart);
        Assert.True(c.Minimized);
    }

    [Theory]
    [InlineData("--duration", "1h", "--until", "18:00")]
    [InlineData("--duration", "1h", "--while-process", "x")]
    [InlineData("--stop", "--start")]
    [InlineData("--stop", "--duration", "1h")]
    [InlineData("--diagnose", "--start")]
    [InlineData("--restore", "--stop")]
    [InlineData("--diagnose", "--restore")]
    [InlineData("--out", "x.txt")]
    [InlineData("--start", "--out", "x.txt")]
    public void Conflicts(params string[] args) => Assert.False(CliParser.Parse(args, Now).Ok);

    [Theory]
    [InlineData("--bogus")]
    [InlineData("positional")]
    [InlineData("--duration")]
    [InlineData("--duration", "banana")]
    [InlineData("--until", "25:99")]
    [InlineData("--reason")]
    [InlineData("--while-process")]
    public void Errors(params string[] args) => Assert.False(CliParser.Parse(args, Now).Ok);

    [Fact]
    public void UnknownSwitchMessageNamesIt() => Assert.Contains("--bogus", Err("--bogus"));

    [Fact]
    public void ExitCodes()
    {
        Assert.Equal(0, CliParser.ExitOk);
        Assert.Equal(1, CliParser.ExitPartialFailure);
        Assert.Equal(2, CliParser.ExitUsage);
        Assert.Equal(3, CliParser.ExitForwardFailed);
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    public void QuoteOne(string input, string expected) => Assert.Equal(expected, CliParser.QuoteOne(input));

    [Fact]
    public void QuoteRoundTripsThroughParser()
    {
        var args = new[] { "--start", "--reason", "My \"big\" job" };
        var quoted = CliParser.Quote(args);
        Assert.Contains("--reason", quoted);
        Assert.Contains("\\\"big\\\"", quoted);
    }
}

public class DurationParserTests
{
    [Theory]
    [InlineData("8h", 480)]
    [InlineData("1h30m", 90)]
    [InlineData("90m", 90)]
    [InlineData("1.5h", 90)]
    [InlineData("2d", 2880)]
    [InlineData("01:30", 90)]
    [InlineData("0:01", 1)]
    [InlineData("24:00", 1440)]
    [InlineData("1d12h", 2160)]
    [InlineData("30d", 43200)]
    [InlineData("1H30M", 90)]
    public void Valid(string text, int minutes)
    {
        Assert.True(DurationParser.TryParse(text, out var t, out var err), err);
        Assert.Equal(TimeSpan.FromMinutes(minutes), t);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("h")]
    [InlineData("10")]
    [InlineData("8x")]
    [InlineData("1:99")]
    [InlineData("-5m")]
    [InlineData("45s")]      // below the 1 minute minimum
    [InlineData("0m")]
    [InlineData("31d")]      // above the 30 day maximum
    [InlineData("721h")]
    public void Invalid(string text)
    {
        Assert.False(DurationParser.TryParse(text, out _, out var err));
        Assert.NotEmpty(err);
    }

    [Fact]
    public void SecondsCombineWithMinutes()
    {
        Assert.True(DurationParser.TryParse("1m30s", out var t, out _));
        Assert.Equal(TimeSpan.FromSeconds(90), t);
    }

    [Fact]
    public void BoundsAreInclusive()
    {
        Assert.True(DurationParser.TryParse("1m", out _, out _));
        Assert.True(DurationParser.TryParse("720h", out _, out _));
    }
}

public class UntilParserTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 14, 0, 0);

    [Fact]
    public void FutureTimeToday()
    {
        Assert.True(UntilParser.TryParse("18:30", Now, out var t));
        Assert.Equal(new DateTime(2026, 11, 12, 18, 30, 0), t);
    }

    [Fact]
    public void PastTimeMeansTomorrow()
    {
        Assert.True(UntilParser.TryParse("08:00", Now, out var t));
        Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 0), t);
    }

    [Fact]
    public void ExactlyNowMeansTomorrow()
    {
        Assert.True(UntilParser.TryParse("14:00", Now, out var t));
        Assert.Equal(new DateTime(2026, 11, 13, 14, 0, 0), t);
    }

    [Fact]
    public void IsoFormat()
    {
        Assert.True(UntilParser.TryParse("2026-11-14T06:15", Now, out var t));
        Assert.Equal(new DateTime(2026, 11, 14, 6, 15, 0), t);
    }

    [Theory]
    [InlineData("2026-11-11T06:15")]  // past
    [InlineData("2027-01-01T00:00")]  // more than 30 days away
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("noon")]
    [InlineData("")]
    public void Invalid(string text) => Assert.False(UntilParser.TryParse(text, Now, out _));

    [Fact]
    public void DstGapIsMovedForward()
    {
        // Europe/Berlin: 2026-03-29 02:30 does not exist (clocks jump 02:00 -> 03:00).
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
        var now = new DateTime(2026, 3, 28, 22, 0, 0);
        Assert.True(UntilParser.TryParse("2026-03-29T02:30", now, out var t, tz));
        Assert.Equal(new DateTime(2026, 3, 29, 3, 30, 0), t);
        Assert.False(tz.IsInvalidTime(t));
    }

    [Fact]
    public void NormalTimeUnchangedWithTimeZone()
    {
        Assert.True(UntilParser.TryParse("18:00", Now, out var t, TimeZoneInfo.Utc));
        Assert.Equal(new DateTime(2026, 11, 12, 18, 0, 0), t);
    }
}
