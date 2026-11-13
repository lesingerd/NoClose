using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using NoClose.Core.Activation;
using NoClose.Core.Cli;
using NoClose.Core.Diagnostics;
using NoClose.Core.Diagnostics.Rules;
using NoClose.Core.Guards;
using NoClose.Core.Journal;
using NoClose.Core.Settings;
using NoClose.Engine;
using NoClose.Guards;
using NoClose.Journal;
using NoClose.Logging;
using NoClose.Platform;

namespace NoClose.Tests;

/// <summary>Tests for the review findings. Nothing here touches the real registry, power plans or screen saver.</summary>
internal sealed class CtxFakeRegistry : RegistryAccessor
{
    public Dictionary<string, JournalValue> Current { get; } = new();
    public List<string> Writes { get; } = new();
    public override JournalValue Capture(RegHive hive, string key, string name, RegView view = RegView.Registry64) =>
        Current.TryGetValue(name, out var v) ? v : JournalValue.Absent();
    public override void Apply(RegHive hive, string key, string name, JournalValue value, RegView view = RegView.Registry64)
    {
        Writes.Add("apply " + name);
        Current[name] = value;
    }
    public override void DeleteValue(RegHive hive, string key, string name, RegView view = RegView.Registry64)
    {
        Writes.Add("delete " + name);
        Current.Remove(name);
    }
}

internal sealed class CtxFakeScreenSaver : ScreenSaverAccessor
{
    public bool Active { get; set; } = true;
    public int SetCalls { get; private set; }
    public override bool GetActive() => Active;
    public override void SetActive(bool active) { SetCalls++; Active = active; }
}

internal sealed class CtxFakePower : PowerSchemeAccessor
{
    public Guid Active { get; set; }
    public Dictionary<Guid, string> Names { get; } = new();
    public List<string> Calls { get; } = new();
    public override Guid GetActiveScheme() => Active;
    public override Guid? TryGetActiveScheme() => Active;
    public override void SetActiveScheme(Guid scheme) { Calls.Add("activate " + scheme); Active = scheme; }
    public override Guid DuplicateScheme(Guid source)
    {
        var g = Guid.NewGuid();
        Calls.Add("duplicate " + source);
        Names[g] = "copy";
        return g;
    }
    public override void DeleteScheme(Guid scheme) { Calls.Add("delete " + scheme); Names.Remove(scheme); }
    public override void WriteValue(Guid scheme, Guid sub, Guid setting, bool ac, uint value) { }
    public override void WriteFriendlyName(Guid scheme, string name) => Names[scheme] = name;
    public override string? TryReadFriendlyName(Guid scheme) => Names.TryGetValue(scheme, out var n) ? n : null;
}

public class GuardTestBase : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "noclose-fix-" + Guid.NewGuid().ToString("N"));
    internal readonly MemoryLogSink Log = new();
    internal readonly CtxFakeRegistry Reg = new();
    internal readonly CtxFakeScreenSaver Ss = new();
    internal readonly CtxFakePower Power = new();
    internal readonly FakeMachineReader Reader = new();
    internal readonly JournalStore Journal;

    protected GuardTestBase()
    {
        Directory.CreateDirectory(Dir);
        Journal = new JournalStore(Path.Combine(Dir, "journal.json"), Log);
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }

    internal GuardContext Ctx(bool elevated = true) => new()
    {
        Journal = Journal, Log = Log, IsElevated = elevated, Reader = Reader, Power = Power, Registry = Reg, ScreenSaver = Ss,
        Restore = new RestoreService(Reg, Power, Ss, Log, new RestoreContext(System.Diagnostics.Process.GetCurrentProcess().SessionId, Elevation.CurrentUserSid, elevated)),
        Engine = new KeepAwakeEngine(Log, Reader, () => 45, () => 15, () => null, _ => { }),
        Clock = new FakeClock(DateTime.UtcNow), Settings = AppSettings.CreateDefault(),
    };
}

public class ScreenSaverGuardTests : GuardTestBase
{
    [Fact]
    public void PolicyAppearingWhileAppliedUndoesTheChangeBeforeStandingDown()
    {
        var g = new ScreenSaverGuard();
        var ctx = Ctx();
        Assert.Equal(GuardStatus.Applied, g.Apply(ctx).Status);
        Assert.False(Ss.Active);

        Reader.Set(RegHive.HKCU, ScreenSaverPolicyRule.Key, "ScreenSaveActive", "1");
        var r = g.Reapply(ctx);

        Assert.Equal(GuardStatus.BlockedByPolicy, r.Status);
        Assert.True(Ss.Active); // the organisation's setting wins again
        Assert.Empty(Journal.OpenEntries(GuardId.ScreenSaver));
    }

    [Fact]
    public void RestoreRunsWheneverOpenEntriesExistEvenIfStatusSaysBlocked()
    {
        var g = new ScreenSaverGuard();
        var ctx = Ctx();
        g.Apply(ctx);
        g.ForceStatus(GuardStatus.BlockedByPolicy, "x"); // what the old Reapply left behind
        var r = g.Restore(ctx);
        Assert.Equal(GuardStatus.Off, r.Status);
        Assert.True(Ss.Active);
        Assert.Empty(Journal.OpenEntries(GuardId.ScreenSaver));
    }

    [Fact]
    public void RestoreWithoutEntriesChangesNothing()
    {
        var g = new ScreenSaverGuard();
        var r = g.Restore(Ctx());
        Assert.Equal("Nothing was changed", r.Message);
        Assert.Equal(0, Ss.SetCalls);
    }

    [Fact]
    public void ScreenSaverSwitchedOnAgainIsReportedNotFought()
    {
        var g = new ScreenSaverGuard();
        var ctx = Ctx();
        g.Apply(ctx);
        int calls = Ss.SetCalls;
        Ss.Active = true; // user or management turned it on
        var r = g.Reapply(ctx);
        Assert.Equal(GuardStatus.Reverted, r.Status);
        Assert.Equal(calls, Ss.SetCalls);
        Assert.Equal(GuardStatus.Reverted, g.Reapply(ctx).Status); // stays reverted, still no write
        Assert.Equal(calls, Ss.SetCalls);
    }

    [Fact]
    public void ReapplyNeverOpensANewJournalEntryWhenItWasAlreadyOff()
    {
        Ss.Active = false;
        var g = new ScreenSaverGuard();
        var ctx = Ctx();
        Assert.Equal(GuardStatus.Applied, g.Apply(ctx).Status);
        Ss.Active = true; // someone switches it on later
        var r = g.Reapply(ctx);
        Assert.Equal(GuardStatus.Reverted, r.Status);
        Assert.Empty(Journal.Entries(GuardId.ScreenSaver));
        Assert.Equal(0, Ss.SetCalls);
    }

    [Fact]
    public void GuardIsOptInWithAConfirmationText()
    {
        var g = new ScreenSaverGuard();
        Assert.True(g.ChangesSettings);
        Assert.False(string.IsNullOrWhiteSpace(g.ConfirmationText));
        var s = AppSettings.DefaultGuard(GuardId.ScreenSaver);
        Assert.False(s.Enabled);
        Assert.False(s.Acknowledged);
    }
}

public class ActiveHoursGuardTests : GuardTestBase
{
    private JournalEntry Entry(string name, uint applied) => new()
    {
        Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue,
        Target = new JournalTarget { Hive = "HKLM", View = "64", Key = RegistryWriteGuard.ActiveHoursKey, Name = name },
        Original = JournalValue.DWord(1), Applied = JournalValue.DWord(applied),
    };

    [Fact]
    public void OnlyTheNewestOpenEntryPerValueIsComparedAndTheGuardStaysApplied()
    {
        var g = new ActiveHoursGuard();
        g.ForceStatus(GuardStatus.Applied, "");
        Journal.Record(Entry("ActiveHoursStart", 5)); // stale entry from a failed restore
        Journal.Record(Entry("ActiveHoursStart", 9));
        Reg.Current["ActiveHoursStart"] = JournalValue.DWord(9);

        Assert.Equal(GuardStatus.Applied, g.Reapply(Ctx()).Status);
    }

    [Fact]
    public void ExternalChangeIsRevertedAndClearsTheShiftSuggestion()
    {
        var g = new ActiveHoursGuard();
        g.ForceStatus(GuardStatus.Applied, "");
        Journal.Record(Entry("ActiveHoursStart", 9));
        Reg.Current["ActiveHoursStart"] = JournalValue.DWord(10);
        var r = g.Reapply(Ctx());
        Assert.Equal(GuardStatus.Reverted, r.Status);
        Assert.False(g.ShiftSuggested);
        // a reverted guard must not overwrite the external value
        g.ShiftForward(Ctx());
        Assert.Empty(Reg.Writes);
    }
}

public class PowerPlanGuardTests : GuardTestBase
{
    [Fact]
    public void RefusesToDuplicateALeftoverNoCloseTemporaryPlan()
    {
        var g = new PowerPlanGuard();
        var leftover = Guid.NewGuid();
        Power.Active = leftover;
        Power.Names[leftover] = "NoClose temporary (from Balanced)";

        var r = g.Apply(Ctx());

        Assert.Equal(GuardStatus.Error, r.Status);
        Assert.DoesNotContain(Power.Calls, c => c.StartsWith("duplicate"));
        Assert.DoesNotContain(Journal.Entries(), e => e.Guard == GuardId.PowerPlan);
    }

    [Fact]
    public void ChainsOnTheRealOriginalWhenAnOpenEntryKnowsIt()
    {
        var real = Guid.NewGuid();
        var temp1 = Guid.NewGuid();
        Power.Active = temp1;
        Power.Names[real] = "My plan";
        Power.Names[temp1] = "NoClose temporary (from My plan)";
        Journal.Record(new JournalEntry
        {
            Guard = GuardId.PowerPlan, Kind = JournalKind.PowerActiveScheme,
            Target = new JournalTarget { OriginalScheme = real.ToString("D"), TempScheme = temp1.ToString("D") },
        });

        var g = new PowerPlanGuard();
        var r = g.Apply(Ctx());

        Assert.Equal(GuardStatus.Applied, r.Status);
        var newest = Journal.OpenEntries(GuardId.PowerPlan).Last();
        Assert.Equal(real.ToString("D"), newest.Target.OriginalScheme); // never the temp copy
        Assert.Contains("duplicate " + temp1, Power.Calls);
    }
}

public class RegistryRestoreRuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noclose-rr-" + Guid.NewGuid().ToString("N"));
    public RegistryRestoreRuleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void RuleDecisions()
    {
        var orig = JournalValue.DWord(7);
        var applied = JournalValue.DWord(9);
        Assert.True(RegistryRestoreRule.ShouldRestore(JournalValue.DWord(9), orig, applied));
        Assert.True(RegistryRestoreRule.ShouldRestore(JournalValue.DWord(7), orig, applied));
        Assert.False(RegistryRestoreRule.ShouldRestore(JournalValue.DWord(11), orig, applied));
        Assert.True(RegistryRestoreRule.ShouldRestore(JournalValue.DWord(11), orig, null)); // unknown applied: unconditional
    }

    private static JournalEntry Hkcu(string name, JournalValue original, JournalValue applied) => new()
    {
        Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue,
        Target = new JournalTarget { Hive = "HKCU", View = "64", Key = @"Software\Test", Name = name },
        Original = original, Applied = applied,
    };

    [Fact]
    public void ExternallyChangedValueIsLeftAloneAndTheEntryClosed()
    {
        var reg = new CtxFakeRegistry();
        var log = new MemoryLogSink();
        var store = new JournalStore(Path.Combine(_dir, "j.json"), log);
        var svc = new RestoreService(reg, new CtxFakePower(), new CtxFakeScreenSaver(), log, new RestoreContext(1, "S-1", true));
        store.Record(Hkcu("v", JournalValue.DWord(7), JournalValue.DWord(9)));
        reg.Current["v"] = JournalValue.DWord(42); // somebody else changed it

        var summary = svc.RestoreStore(store);

        Assert.Empty(reg.Writes);
        Assert.Contains(summary.Messages, m => m.Contains("left alone"));
        Assert.False(store.HasOpenEntries);
    }

    [Fact]
    public void OurOwnAppliedValueIsRestored()
    {
        var reg = new CtxFakeRegistry();
        var log = new MemoryLogSink();
        var store = new JournalStore(Path.Combine(_dir, "j.json"), log);
        var svc = new RestoreService(reg, new CtxFakePower(), new CtxFakeScreenSaver(), log, new RestoreContext(1, "S-1", true));
        store.Record(Hkcu("v", JournalValue.DWord(7), JournalValue.DWord(9)));
        store.Record(Hkcu("gone", JournalValue.Absent(), JournalValue.DWord(1)));
        reg.Current["v"] = JournalValue.DWord(9);
        reg.Current["gone"] = JournalValue.DWord(1);

        svc.RestoreStore(store);

        Assert.Equal(7u, reg.Current["v"].AsDWord());
        Assert.False(reg.Current.ContainsKey("gone"));
    }

    [Fact]
    public void RestoreOrderFollowsAppendOrderEvenWhenTheClockWentBackwards()
    {
        var older = new JournalEntry { Id = "first", Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue,
            RecordedUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Target = new JournalTarget { Hive = "HKCU", Key = "K", Name = "a" }, Original = JournalValue.DWord(1) };
        var newer = new JournalEntry { Id = "second", Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue,
            RecordedUtc = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc), // NTP stepped the clock back
            Target = new JournalTarget { Hive = "HKCU", Key = "K", Name = "a" }, Original = JournalValue.DWord(2) };
        var j = new RestoreJournal { SessionId = 1, UserSid = "S-1" };
        j.Entries.Add(older);
        j.Entries.Add(newer);
        var plan = RestorePlanner.Plan(j, new RestoreContext(1, "S-1", true));
        Assert.Equal(new[] { "second", "first" }, plan.Actions.Select(a => a.EntryId));
    }

    [Fact]
    public void PlannerCarriesTheAppliedValueIntoTheRegistryActions()
    {
        var j = new RestoreJournal { SessionId = 1, UserSid = "S-1" };
        j.Entries.Add(Hkcu("a", JournalValue.DWord(1), JournalValue.DWord(5)));
        j.Entries.Add(Hkcu("b", JournalValue.Absent(), JournalValue.DWord(6)));
        var plan = RestorePlanner.Plan(j, new RestoreContext(1, "S-1", true));
        Assert.Equal(6u, Assert.IsType<DeleteRegistryValueAction>(plan.Actions[0]).OnlyIfCurrentEquals!.AsDWord());
        Assert.Equal(5u, Assert.IsType<RestoreRegistryValueAction>(plan.Actions[1]).OnlyIfCurrentEquals!.AsDWord());
    }

    [Fact]
    public void RestoreFromDiskContinuesPastABrokenJournalAndKeepsGoing()
    {
        var reg = new CtxFakeRegistry();
        var log = new MemoryLogSink();
        var svc = new RestoreService(reg, new CtxFakePower(), new CtxFakeScreenSaver(), log, new RestoreContext(1, "S-1", true));
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{{{");
        var good = Path.Combine(_dir, "good.json");
        var j = new RestoreJournal { ProcessId = 0x7ffffff0, ProcessStartUtc = DateTime.UtcNow.AddDays(-1), SessionId = 1, UserSid = "S-1" };
        j.Entries.Add(Hkcu("g", JournalValue.Absent(), JournalValue.DWord(1)));
        File.WriteAllText(good, JournalSerializer.Serialize(j));

        var summary = svc.RestoreFromDisk(new[] { bad, good });

        Assert.True(summary.AnyFailure);
        Assert.Equal(1, summary.Restored);
    }
}

public class JournalDurabilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noclose-jd-" + Guid.NewGuid().ToString("N"));
    public JournalDurabilityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static RestoreJournal J(long seq, string name)
    {
        var j = new RestoreJournal { Sequence = seq, SessionId = 1, UserSid = "S-1" };
        j.Entries.Add(new JournalEntry { Id = name, Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue, Target = new JournalTarget { Hive = "HKCU", Key = "K", Name = name } });
        return j;
    }

    [Fact]
    public void LoadPrefersAnewerCompleteTmpWhenTheRenameWasLost()
    {
        var path = Path.Combine(_dir, "j.json");
        File.WriteAllText(path, JournalSerializer.Serialize(J(3, "old")));
        File.WriteAllText(path + ".tmp", JournalSerializer.Serialize(J(5, "new")));
        Assert.Equal("new", JournalStore.TryLoad(path, out _)!.Entries[0].Id);
    }

    [Fact]
    public void LoadUsesTmpWhenTheMainFileIsMissing()
    {
        var path = Path.Combine(_dir, "only-tmp.json");
        File.WriteAllText(path + ".tmp", JournalSerializer.Serialize(J(1, "t")));
        Assert.Equal("t", JournalStore.TryLoad(path, out var error)!.Entries[0].Id);
        Assert.Null(error);
    }

    [Fact]
    public void LoadIgnoresATruncatedTmpAndAnOlderTmp()
    {
        var path = Path.Combine(_dir, "j2.json");
        File.WriteAllText(path, JournalSerializer.Serialize(J(4, "main")));
        File.WriteAllText(path + ".tmp", "{\"schemaVersion\":1,\"ent");
        Assert.Equal("main", JournalStore.TryLoad(path, out _)!.Entries[0].Id);
        File.WriteAllText(path + ".tmp", JournalSerializer.Serialize(J(2, "stale")));
        Assert.Equal("main", JournalStore.TryLoad(path, out _)!.Entries[0].Id);
    }

    [Fact]
    public void EverySaveIncrementsTheSequenceAndLeavesNoTmp()
    {
        var path = Path.Combine(_dir, "seq.json");
        var store = new JournalStore(path, NullLogSink.Instance);
        store.Record(new JournalEntry { Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue, Target = new JournalTarget { Hive = "HKCU", Key = "K", Name = "n" }, Original = JournalValue.DWord(1) });
        var first = JournalStore.TryLoad(path, out _)!.Sequence;
        store.MarkApplied(store.Entries()[0].Id);
        Assert.True(JournalStore.TryLoad(path, out _)!.Sequence > first);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ProtectedDirectoryGrantsUsersReadOnlyAndAdminsFull()
    {
        var sec = JournalStore.BuildProtectedSecurity();
        var rules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var u = Assert.Single(rules, r => r.IdentityReference == users);
        Assert.Equal(FileSystemRights.ReadAndExecute, u.FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.Equal(AccessControlType.Allow, u.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, Assert.Single(rules, r => r.IdentityReference == admins).FileSystemRights);
        Assert.DoesNotContain(rules, r => r.IdentityReference == users && (r.FileSystemRights & FileSystemRights.Write) != 0);
        Assert.True(sec.AreAccessRulesProtected);
    }

    [Fact]
    public void MissingFileIsNotReportedAsDenied() => Assert.False(JournalStore.ExistsButDenied(Path.Combine(_dir, "nope.json")));

    [Fact]
    public void DirectoryFlushWorksOnARealDirectory() => Assert.True(DirectoryFlush.Flush(_dir));
}

public class ElevationAndStatusTests
{
    [Theory]
    [InlineData(false, 3, true)]   // administrator with a filtered token
    [InlineData(false, 1, false)]  // standard user (default token): over-the-shoulder prompt, a different account
    [InlineData(false, 0, false)]
    [InlineData(true, 2, false)]   // already elevated
    public void RestartElevatedIsOnlyForAFilteredAdministratorToken(bool elevated, int tokenType, bool expected) =>
        Assert.Equal(expected, ElevationRules.CanRestartElevated(elevated, tokenType));

    [Fact]
    public void HandoffUserMustMatch()
    {
        Assert.True(ElevationRules.SameUser("S-1-5-21-1", "s-1-5-21-1"));
        Assert.False(ElevationRules.SameUser("S-1-5-21-1", "S-1-5-21-2"));
        Assert.False(ElevationRules.SameUser("", ""));
        Assert.False(ElevationRules.SameUser(null, "S-1"));
    }

    [Theory]
    [InlineData(false, true, false, OverallState.Idle)]
    [InlineData(true, false, false, OverallState.Active)]
    [InlineData(true, false, true, OverallState.Degraded)]
    [InlineData(true, true, false, OverallState.NotKeepingAwake)]
    public void RunningWithAnIdleEngineIsNotShownAsActive(bool running, bool engineIdle, bool degraded, OverallState expected)
    {
        Assert.Equal(expected, StatusSummary.Compute(running, engineIdle, degraded));
        Assert.NotEqual("Active", StatusSummary.Label(OverallState.NotKeepingAwake));
    }

    private static DiagnosticFinding F(string title) =>
        new("R-ALLOW", Severity.High, title, "d", null, "f", null, false);

    [Fact]
    public void OnlyTheCurrentPowerSourceCounts()
    {
        var ac = F("Power plan ignores system required requests (plugged in)");
        var dc = F("Power plan ignores system required requests (on battery)");
        var both = F("Something that applies everywhere");
        Assert.Equal(new[] { ac, both }, PowerSourceFilter.For(new[] { ac, dc, both }, onAc: true));
        Assert.Equal(new[] { dc, both }, PowerSourceFilter.For(new[] { ac, dc, both }, onAc: false));
    }

    [Fact]
    public void RealAllowRuleFindingsAreTaggedWithTheirPowerSource()
    {
        var reader = new FakeMachineReader()
            .Power(PowerGuids.SubSleep, PowerGuids.AllowSystemRequired, ac: 1, dc: 0);
        var findings = new Core.Diagnostics.Rules.AllowRequestsRule().Evaluate(reader).Where(f => f.Severity == Severity.High).ToList();
        Assert.NotEmpty(findings);
        Assert.Empty(PowerSourceFilter.For(findings, onAc: true));  // DC-only problem does not degrade an AC machine
        Assert.NotEmpty(PowerSourceFilter.For(findings, onAc: false));
    }

    [Fact]
    public void OrphanPlannerDeletesInactiveUnreferencedTempPlansOnly()
    {
        var keep = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var active = Guid.NewGuid();
        var user = Guid.NewGuid();
        var plan = OrphanSchemePlanner.Plan(new (Guid, string?)[]
        {
            (keep, "NoClose temporary (from A)"), (orphan, "NoClose temporary (from B)"),
            (active, "NoClose temporary (from C)"), (user, "My own plan"), (Guid.NewGuid(), null),
        }, active, new HashSet<Guid> { keep });
        Assert.Equal(new[] { orphan }, plan.Delete);
        Assert.Equal(new[] { active }, plan.ActiveOrphans);
    }
}

public class ArgsAndResumeTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 14, 0, 0);
    private const string Sid = "S-1-5-21-1-2-3-1001";

    public static IEnumerable<object[]> Specs() => new[]
    {
        new object[] { ActivationSpec.Indefinite },
        new object[] { ActivationSpec.ForDuration(TimeSpan.FromMinutes(125)) },
        new object[] { ActivationSpec.ForUntil(new DateTime(2026, 11, 13, 6, 30, 0)) },
        new object[] { ActivationSpec.ParseProcess("build.exe") },
        new object[] { ActivationSpec.ParseProcess("4242") },
    };

    [Theory]
    [MemberData(nameof(Specs))]
    public void ElevatedArgsRoundTripThroughTheParser(ActivationSpec spec)
    {
        var remaining = spec.Kind == ActivationKind.Duration ? TimeSpan.FromMinutes(125) : (TimeSpan?)null;
        var args = ActivationArgs.ForElevatedCopy(true, true, spec, "Nightly build", remaining,
            new[] { GuardId.ExecutionState, GuardId.PowerPlan }, Sid);

        var r = CliParser.Parse(args.ToArray(), Now);
        Assert.True(r.Ok, r.Error);
        var c = r.Command!;
        Assert.Equal(CliVerb.Start, c.Verb);
        Assert.Equal(spec, c.Activation);
        Assert.Equal("Nightly build", c.Reason);
        Assert.True(c.Minimized);
        Assert.True(c.Handoff);
        Assert.Equal(Sid, c.HandoffSid);
        Assert.Equal(new[] { GuardId.ExecutionState, GuardId.PowerPlan }, c.GuardOverride);
        Assert.Single(args, a => a == "--start"); // never two mode switches (the old code produced "--duration 8h --start --duration 480m")
    }

    [Fact]
    public void ADurationPassesTheRemainingTimeNotTheOriginalLength()
    {
        var args = ActivationArgs.ForElevatedCopy(false, true, ActivationSpec.ForDuration(TimeSpan.FromHours(8)), "r", TimeSpan.FromMinutes(90), null, Sid);
        var c = CliParser.Parse(args.ToArray(), Now).Command!;
        Assert.Equal(TimeSpan.FromMinutes(90), c.Activation!.Duration);
        Assert.False(c.Minimized);
        Assert.Null(c.GuardOverride);
    }

    [Fact]
    public void IdleElevationOnlyCarriesMinimizedAndHandoff()
    {
        var args = ActivationArgs.ForElevatedCopy(false, false, ActivationSpec.Indefinite, "r", null, null, Sid);
        var c = CliParser.Parse(args.ToArray(), Now).Command!;
        Assert.Equal(CliVerb.None, c.Verb);
        Assert.Equal(Sid, c.HandoffSid);
    }

    private static string[] Saved(ActivationSpec spec) => ActivationArgs.FromSpec(spec, "Task").ToArray();

    [Fact]
    public void NoSavedArgsMeansNoSavedState() =>
        Assert.Equal(ResumeOutcome.NoSavedState, ResumeDecision.Decide(null, null, Now, Now, TimeZoneInfo.Utc).Outcome);

    [Fact]
    public void UnreadableSavedArgsAreNotResumedAsIndefinite()
    {
        var d = ResumeDecision.Decide(new[] { "--duration", "garbage" }, null, Now, Now, TimeZoneInfo.Utc);
        Assert.Equal(ResumeOutcome.Skip, d.Outcome);
        Assert.Null(d.Spec);
    }

    [Fact]
    public void ExpiredUntilIsSkippedNotTurnedIndefinite()
    {
        var spec = ActivationSpec.ForUntil(Now.AddHours(-1));
        var d = ResumeDecision.Decide(Saved(spec), Now.AddHours(-1), Now, Now, TimeZoneInfo.Utc);
        Assert.Equal(ResumeOutcome.Skip, d.Outcome);
    }

    [Fact]
    public void FutureUntilResumes()
    {
        var spec = ActivationSpec.ForUntil(Now.AddHours(3));
        var d = ResumeDecision.Decide(Saved(spec), Now.AddHours(3), Now, Now, TimeZoneInfo.Utc);
        Assert.Equal(ResumeOutcome.Resume, d.Outcome);
        Assert.Equal(spec.UntilLocal, d.Spec!.UntilLocal);
        Assert.Equal("Task", d.Reason);
    }

    [Fact]
    public void DurationResumesWithTheRemainingTimeOnly()
    {
        var d = ResumeDecision.Decide(Saved(ActivationSpec.ForDuration(TimeSpan.FromHours(8))), Now.AddMinutes(30), Now, Now, TimeZoneInfo.Utc);
        Assert.Equal(ResumeOutcome.Resume, d.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(30), d.Spec!.Duration);
    }

    [Fact]
    public void ElapsedOrUnknownDurationIsSkipped()
    {
        var args = Saved(ActivationSpec.ForDuration(TimeSpan.FromHours(8)));
        Assert.Equal(ResumeOutcome.Skip, ResumeDecision.Decide(args, Now.AddMinutes(-1), Now, Now, TimeZoneInfo.Utc).Outcome);
        Assert.Equal(ResumeOutcome.Skip, ResumeDecision.Decide(args, null, Now, Now, TimeZoneInfo.Utc).Outcome);
    }

    [Fact]
    public void IndefiniteAndProcessActivationsResume()
    {
        Assert.Equal(ResumeOutcome.Resume, ResumeDecision.Decide(Saved(ActivationSpec.Indefinite), null, Now, Now, TimeZoneInfo.Utc).Outcome);
        Assert.Equal(ResumeOutcome.Resume, ResumeDecision.Decide(Saved(ActivationSpec.ParseProcess("a.exe")), null, Now, Now, TimeZoneInfo.Utc).Outcome);
    }

    [Fact]
    public void ASwitchIsNeverConsumedAsTheValueOfThePreviousSwitch()
    {
        var r = CliParser.Parse(new[] { "--reason", "--stop" }, Now);
        Assert.False(r.Ok);
        Assert.Contains("--reason needs a value", r.Error);
        Assert.False(CliParser.Parse(new[] { "--reason", "--start" }, Now).Ok);
        Assert.False(CliParser.Parse(new[] { "--reason", "/stop" }, Now).Ok);
        // ordinary values, including paths that merely start with a slash, still work
        Assert.Equal("a note", CliParser.Parse(new[] { "--reason", "a note" }, Now).Command!.Reason);
        Assert.Equal("/tmp/x.txt", CliParser.Parse(new[] { "--diagnose", "--out", "/tmp/x.txt" }, Now).Command!.OutPath);
    }

    [Fact]
    public void HandoffTakesAnOptionalSid()
    {
        var withSid = CliParser.Parse(new[] { "--handoff", Sid, "--minimized" }, Now).Command!;
        Assert.True(withSid.Handoff);
        Assert.Equal(Sid, withSid.HandoffSid);
        Assert.True(withSid.Minimized);
        var bare = CliParser.Parse(new[] { "--handoff", "--minimized" }, Now).Command!;
        Assert.True(bare.Handoff);
        Assert.Null(bare.HandoffSid);
    }
}

public class PlatformFixTests
{
    [Fact]
    public void WatcherSeesOwnProcessAndNotAMissingOne()
    {
        Assert.True(ProcessWatcher.IsRunning(ActivationSpec.ParseProcess(Environment.ProcessId.ToString())));
        Assert.False(ProcessWatcher.IsRunning(ActivationSpec.ParseProcess("2147483000")));
        Assert.Equal(ProcessProbe.Alive, ProcessWatcher.ProbePid(Environment.ProcessId));
        Assert.Equal(ProcessProbe.Dead, ProcessWatcher.ProbePid(2147483000));
    }

    [Fact]
    public void ProcessThatCannotBeQueriedIsNeverReportedAsEnded()
    {
        // PID 4 (System) cannot be opened with query rights by a normal process: it must count as running, not as ended.
        Assert.NotEqual(ProcessProbe.Dead, ProcessWatcher.ProbePid(4));
        Assert.True(ProcessWatcher.IsRunning(ActivationSpec.ParseProcess("4")));
    }

    [Fact]
    public void WatcherByNameUsesOnlyThisSession()
    {
        var name = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        Assert.True(ProcessWatcher.IsRunning(ActivationSpec.ParseProcess(name)));
    }

    [Theory]
    [InlineData(true, 3u, true)]  // redirected to a pipe
    [InlineData(true, 1u, true)]  // redirected to a file
    [InlineData(true, 2u, true)]
    [InlineData(true, 0u, false)]
    [InlineData(false, 0u, false)]
    public void InheritedStdoutIsUsedBeforeAttachingAConsole(bool valid, uint type, bool inherited) =>
        Assert.Equal(inherited ? ConsoleAttach.Plan.UseInherited : ConsoleAttach.Plan.AttachParent, ConsoleAttach.Decide(valid, type));

    [Fact]
    public void EngineShutdownReturnsAtOnceWhenNoThreadIsRunning()
    {
        using var engine = new KeepAwakeEngine(NullLogSink.Instance, new FakeMachineReader(), () => 45, () => 15, () => null, _ => { });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(engine.Shutdown(TimeSpan.FromSeconds(10)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        // commands after shutdown are dropped instead of piling up unprocessed
        engine.Reassert("late");
        Assert.True(engine.Shutdown(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task PipeRoundTripAndAHungClientDoesNotBlockOthers()
    {
        var name = "NoClose.Test." + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(req => Task.FromResult(new PipeResponse(true, "got " + req.Verb)), _ => { }, name);
        server.Start();

        // a client that connects and never writes
        using var hung = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
        hung.Connect(3000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await Task.Run(() => PipeClient.Send(new PipeRequest("show", Array.Empty<string>()), out _, 3000, name, TimeSpan.FromSeconds(10)));
        Assert.NotNull(response);
        Assert.True(response!.Ok);
        Assert.Equal("got show", response.Message);
        Assert.True(sw.Elapsed < PipeServer.ReadTimeout, "the hung connection blocked the next client");
    }

    [Fact]
    public void ClientGivesUpOnAServerThatNeverAnswers()
    {
        var name = "NoClose.Test." + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(_ => new TaskCompletionSource<PipeResponse>().Task, _ => { }, name);
        server.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = PipeClient.Send(new PipeRequest("show", Array.Empty<string>()), out _, 3000, name, TimeSpan.FromMilliseconds(500));
        Assert.Null(response);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

}

public class PipeCreateTests
{
    [Fact]
    public void PipeCanBeCreatedWithTheUserOnlySecurity()
    {
        using var p = PipeServer.Create("NoClose.Test." + Guid.NewGuid().ToString("N"));
        Assert.NotNull(p);
    }
}
