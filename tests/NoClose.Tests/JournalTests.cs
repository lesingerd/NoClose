using NoClose.Core.Diagnostics;
using NoClose.Core.Guards;
using NoClose.Core.Journal;
using NoClose.Journal;
using NoClose.Logging;

namespace NoClose.Tests;

public class JournalSerializerTests
{
    private static RestoreJournal Sample()
    {
        var j = new RestoreJournal
        {
            SessionId = 1, ProcessId = 12345, ProcessStartUtc = new DateTime(2026, 11, 12, 13, 2, 11, 123, DateTimeKind.Utc),
            Machine = "PC-NAME", UserSid = "S-1-5-21-1", CreatedUtc = new DateTime(2026, 11, 12, 13, 2, 11, 200, DateTimeKind.Utc),
        };
        j.Entries.Add(new JournalEntry
        {
            Id = "e1", Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue, RecordedUtc = new DateTime(2026, 11, 12, 13, 3, 0, DateTimeKind.Utc),
            Target = new JournalTarget { Hive = "HKLM", View = "64", Key = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", Name = "ActiveHoursStart" },
            Original = JournalValue.DWord(8), Applied = JournalValue.DWord(14), Status = JournalEntryStatus.Applied,
        });
        j.Entries.Add(new JournalEntry
        {
            Id = "e2", Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue,
            Target = new JournalTarget { Hive = "HKLM", View = "64", Key = "K", Name = "Absent" },
            Original = JournalValue.Absent(), Status = JournalEntryStatus.Pending,
        });
        j.Entries.Add(new JournalEntry
        {
            Id = "e3", Guard = GuardId.PowerPlan, Kind = JournalKind.PowerActiveScheme,
            Target = new JournalTarget { OriginalScheme = "381b4222-f694-41f0-9685-ff5bb260df2e", TempScheme = "a1b2c3d4-0000-0000-0000-000000000001", TempName = "NoClose temporary (from Balanced)" },
            Status = JournalEntryStatus.Applied,
        });
        j.Entries.Add(new JournalEntry
        {
            Id = "e4", Guard = GuardId.ScreenSaver, Kind = JournalKind.SpiScreenSaveActive,
            Target = new JournalTarget { SessionId = 1 }, Original = JournalValue.Bool(true), Status = JournalEntryStatus.Applied,
        });
        j.Entries.Add(new JournalEntry
        {
            Id = "e5", Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue,
            Target = new JournalTarget { Hive = "HKCU", View = "64", Key = "K", Name = "Types" },
            Original = new JournalValue { State = JournalValueState.Present, Type = JournalValueType.MultiString, DataList = new List<string> { "a", "b c" } },
            Applied = new JournalValue { State = JournalValueState.Present, Type = JournalValueType.Binary, Data = Convert.ToBase64String(new byte[] { 1, 2, 3 }) },
        });
        return j;
    }

    [Fact]
    public void RoundTripAllKindsAndTypes()
    {
        var json = JournalSerializer.Serialize(Sample());
        var back = JournalSerializer.Deserialize(json);
        Assert.Equal(12345, back.ProcessId);
        Assert.Equal("PC-NAME", back.Machine);
        Assert.Equal(5, back.Entries.Count);

        var e1 = back.Entries[0];
        Assert.Equal(GuardId.ActiveHours, e1.Guard);
        Assert.Equal(JournalKind.RegistryValue, e1.Kind);
        Assert.Equal("ActiveHoursStart", e1.Target.Name);
        Assert.Equal(8u, e1.Original!.AsDWord());
        Assert.Equal(14u, e1.Applied!.AsDWord());
        Assert.Equal(JournalEntryStatus.Applied, e1.Status);

        Assert.True(back.Entries[1].Original!.IsAbsent);
        Assert.Equal(JournalEntryStatus.Pending, back.Entries[1].Status);

        Assert.Equal("a1b2c3d4-0000-0000-0000-000000000001", back.Entries[2].Target.TempScheme);
        Assert.Equal(1, back.Entries[3].Target.SessionId);
        Assert.True(back.Entries[3].Original!.AsBool());
        Assert.Equal(new[] { "a", "b c" }, back.Entries[4].Original!.DataList);
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(back.Entries[4].Applied!.Data!));
    }

    [Fact]
    public void FormatMatchesSpecification()
    {
        var json = JournalSerializer.Serialize(Sample());
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"guard\": \"ActiveHours\"", json);
        Assert.Contains("\"kind\": \"RegistryValue\"", json);
        Assert.Contains("\"state\": \"present\"", json);
        Assert.Contains("\"state\": \"absent\"", json);
        Assert.Contains("\"type\": \"DWord\"", json);
        Assert.Contains("\"status\": \"Applied\"", json);
        Assert.Contains("\"originalScheme\"", json);
        Assert.Contains("\"dataList\"", json);
    }

    [Fact]
    public void UnknownFieldsTolerated()
    {
        const string json = "{\"schemaVersion\":1,\"processId\":7,\"futureField\":{\"x\":1},\"entries\":[{\"id\":\"a\",\"guard\":\"PowerPlan\",\"kind\":\"PowerActiveScheme\",\"extra\":5,\"target\":{\"originalScheme\":\"x\",\"unknown\":true},\"status\":\"Pending\"}]}";
        var j = JournalSerializer.Deserialize(json);
        Assert.Equal(7, j.ProcessId);
        Assert.Single(j.Entries);
        Assert.Equal("x", j.Entries[0].Target.OriginalScheme);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":0,\"entries\":[]}")]
    [InlineData("not json")]
    [InlineData("null")]
    public void BadInputThrows(string json) => Assert.Throws<JournalFormatException>(() => JournalSerializer.Deserialize(json));

    [Fact]
    public void ValueEquality()
    {
        Assert.True(JournalValue.DWord(5).ValueEquals(JournalValue.DWord(5)));
        Assert.False(JournalValue.DWord(5).ValueEquals(JournalValue.DWord(6)));
        Assert.True(JournalValue.Absent().ValueEquals(JournalValue.Absent()));
        Assert.False(JournalValue.Absent().ValueEquals(JournalValue.DWord(0)));
    }
}

public class RestorePlannerTests
{
    private static readonly RestoreContext Admin = new(1, "S-1", true);
    private static readonly RestoreContext User = new(1, "S-1", false);

    private static JournalEntry Reg(string id, int minute, bool absent, JournalEntryStatus status = JournalEntryStatus.Applied, string hive = "HKLM") => new()
    {
        Id = id, Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue, RecordedUtc = new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc),
        Target = new JournalTarget { Hive = hive, View = "64", Key = "K", Name = id },
        Original = absent ? JournalValue.Absent() : JournalValue.DWord(7), Status = status,
    };

    private static RestoreJournal J(params JournalEntry[] entries)
    {
        var j = new RestoreJournal { SessionId = 1, UserSid = "S-1" };
        j.Entries.AddRange(entries);
        return j;
    }

    [Fact]
    public void ReverseRecordedOrder()
    {
        var plan = RestorePlanner.Plan(J(Reg("a", 1, false), Reg("b", 2, true), Reg("c", 3, false)), Admin);
        Assert.Equal(new[] { "c", "b", "a" }, plan.Actions.Select(a => a.EntryId));
        Assert.IsType<RestoreRegistryValueAction>(plan.Actions[0]);
        Assert.IsType<DeleteRegistryValueAction>(plan.Actions[1]);
    }

    [Fact]
    public void EqualTimestampsFallBackToReverseListOrder()
    {
        var plan = RestorePlanner.Plan(J(Reg("a", 1, false), Reg("b", 1, false)), Admin);
        Assert.Equal(new[] { "b", "a" }, plan.Actions.Select(a => a.EntryId));
    }

    [Fact]
    public void PendingEntriesAreRestoredRestoredAreNot()
    {
        var plan = RestorePlanner.Plan(J(Reg("p", 1, false, JournalEntryStatus.Pending), Reg("r", 2, false, JournalEntryStatus.Restored), Reg("f", 3, false, JournalEntryStatus.Failed)), Admin);
        Assert.Equal(new[] { "f", "p" }, plan.Actions.Select(a => a.EntryId));
    }

    [Fact]
    public void AdminEntriesSkippedAndKeptWhenNotElevated()
    {
        var plan = RestorePlanner.Plan(J(Reg("hklm", 1, false), Reg("hkcu", 2, false, hive: "HKCU")), User);
        var skip = Assert.IsType<SkipAction>(plan.Actions.Single(a => a.EntryId == "hklm"));
        Assert.True(skip.KeepEntry);
        Assert.Equal("requires elevation", skip.Reason);
        Assert.IsType<RestoreRegistryValueAction>(plan.Actions.Single(a => a.EntryId == "hkcu"));
    }

    [Fact]
    public void PowerSchemeActivatesOriginalThenDeletesTemp()
    {
        var orig = Guid.NewGuid();
        var temp = Guid.NewGuid();
        var e = new JournalEntry { Id = "p", Guard = GuardId.PowerPlan, Kind = JournalKind.PowerActiveScheme, Target = new JournalTarget { OriginalScheme = orig.ToString(), TempScheme = temp.ToString() } };
        var plan = RestorePlanner.Plan(J(e), Admin);
        var act = Assert.IsType<ActivateSchemeAction>(plan.Actions[0]);
        Assert.Equal(orig, act.Original);
        Assert.Equal(temp, act.OnlyIfActiveIs);
        Assert.Equal(temp, Assert.IsType<DeleteSchemeAction>(plan.Actions[1]).Temp);
    }

    [Fact]
    public void NullTempSchemeNeedsNoRestoreButEntryIsCleared()
    {
        var e = new JournalEntry { Id = "p", Guard = GuardId.PowerPlan, Kind = JournalKind.PowerActiveScheme, Target = new JournalTarget { OriginalScheme = Guid.NewGuid().ToString(), TempScheme = null } };
        var plan = RestorePlanner.Plan(J(e), Admin);
        var skip = Assert.IsType<SkipAction>(Assert.Single(plan.Actions));
        Assert.False(skip.KeepEntry);
    }

    private static JournalEntry Spi(int session, bool original) => new()
    {
        Id = "spi", Guard = GuardId.ScreenSaver, Kind = JournalKind.SpiScreenSaveActive,
        Target = new JournalTarget { SessionId = session }, Original = JournalValue.Bool(original),
    };

    [Fact]
    public void SpiRestoredOnlyInSameSessionAndUser()
    {
        var same = RestorePlanner.Plan(J(Spi(1, true)), User);
        Assert.True(Assert.IsType<SetSpiScreenSaveActiveAction>(Assert.Single(same.Actions)).Active);

        var otherSession = RestorePlanner.Plan(J(Spi(5, true)), User);
        Assert.False(Assert.IsType<SkipAction>(Assert.Single(otherSession.Actions)).KeepEntry);

        var otherUser = RestorePlanner.Plan(J(Spi(1, true)), new RestoreContext(1, "S-2", false));
        Assert.IsType<SkipAction>(Assert.Single(otherUser.Actions));
    }

    [Fact]
    public void IncompleteRegistryEntryIsDiscarded()
    {
        var e = Reg("x", 1, false);
        e.Target.Key = null;
        Assert.IsType<SkipAction>(Assert.Single(RestorePlanner.Plan(J(e), Admin).Actions));
    }
}

public class RegistryWriteGuardTests
{
    private const string UxKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    [Theory]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")]
    [InlineData(@"SOFTWARE\Policies")]
    [InlineData(@"software\policies\microsoft\power")]
    [InlineData(@"Policies\Microsoft")]
    [InlineData(@"Software\Policies\Microsoft\Windows\Control Panel\Desktop")]
    [InlineData(@"SOFTWARE\WOW6432Node\Policies\Foo")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System")]
    [InlineData(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update")]
    [InlineData(@"SOFTWARE\Microsoft\PolicyManager")]
    [InlineData(@"SOFTWARE\Microsoft\Enrollments\ABC")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Power\PowerRequestOverride\Process")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Terminal Server")]
    [InlineData(@"\SOFTWARE\Policies\X\")]
    public void DeniedKeysAreRejectedForBothHives(string key)
    {
        Assert.True(RegistryWriteGuard.IsDenied(key));
        Assert.Throws<InvalidOperationException>(() => RegistryWriteGuard.EnsureAllowed(RegHive.HKLM, key, "ActiveHoursStart"));
        Assert.Throws<InvalidOperationException>(() => RegistryWriteGuard.EnsureAllowed(RegHive.HKCU, key, "ScreenSaveActive"));
    }

    [Theory]
    [InlineData("ActiveHoursStart")]
    [InlineData("ActiveHoursEnd")]
    [InlineData("SmartActiveHoursState")]
    [InlineData("activehoursstart")]
    public void AllowListAccepted(string name)
    {
        RegistryWriteGuard.EnsureAllowed(RegHive.HKLM, UxKey, name);
        RegistryWriteGuard.EnsureAllowed(RegHive.HKLM, @"\" + UxKey + @"\", name);
        RegistryWriteGuard.EnsureAllowed(RegHive.HKLM, UxKey.ToLowerInvariant(), name);
    }

    [Theory]
    [InlineData("PauseUpdatesExpiryTime")]
    [InlineData("")]
    [InlineData("ActiveHoursMaxRange")]
    public void OtherValuesInAllowedKeyRejected(string name) =>
        Assert.Throws<InvalidOperationException>(() => RegistryWriteGuard.EnsureAllowed(RegHive.HKLM, UxKey, name));

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"SOFTWARE\Microsoft\WindowsUpdate\UX")]
    [InlineData(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings\Sub")]
    [InlineData(@"Control Panel\Desktop")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Foo")]
    public void EverythingElseRejected(string key) =>
        Assert.Throws<InvalidOperationException>(() => RegistryWriteGuard.EnsureAllowed(RegHive.HKLM, key, "ActiveHoursStart"));

    [Fact]
    public void AllowedKeyInWrongHiveRejected() =>
        Assert.Throws<InvalidOperationException>(() => RegistryWriteGuard.EnsureAllowed(RegHive.HKCU, UxKey, "ActiveHoursStart"));
}

public class JournalStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noclose-journal-" + Guid.NewGuid().ToString("N"));

    public JournalStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private JournalStore Store() => new(Path.Combine(_dir, "restore-journal.json"), NullLogSink.Instance);

    private static JournalEntry Entry(string name) => new()
    {
        Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue,
        Target = new JournalTarget { Hive = "HKLM", View = "64", Key = "K", Name = name }, Original = JournalValue.DWord(1),
    };

    [Fact]
    public void RecordWritesBeforeAnyChangeAndMarksPending()
    {
        var store = Store();
        var e = Entry("a");
        store.Record(e);
        Assert.True(File.Exists(store.Path));
        var onDisk = JournalStore.TryLoad(store.Path, out var err)!;
        Assert.Null(err);
        Assert.Equal(JournalEntryStatus.Pending, Assert.Single(onDisk.Entries).Status);
        Assert.Equal(Environment.ProcessId, onDisk.ProcessId);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void MarkAppliedAndUpdatePersist()
    {
        var store = Store();
        var e = Entry("a");
        store.Record(e);
        store.Update(e.Id, x => x.Target.TempScheme = "t");
        store.MarkApplied(e.Id, JournalValue.DWord(9));
        var onDisk = JournalStore.TryLoad(store.Path, out _)!.Entries.Single();
        Assert.Equal(JournalEntryStatus.Applied, onDisk.Status);
        Assert.Equal("t", onDisk.Target.TempScheme);
        Assert.Equal(9u, onDisk.Applied!.AsDWord());
    }

    [Fact]
    public void FileDeletedWhenEverythingRestored()
    {
        var store = Store();
        var a = Entry("a"); var b = Entry("b");
        store.Record(a); store.Record(b);
        store.MarkRestored(a.Id);
        Assert.True(File.Exists(store.Path));
        Assert.Single(store.OpenEntries());
        store.MarkRestored(b.Id);
        Assert.False(File.Exists(store.Path));
        Assert.False(store.HasOpenEntries);
    }

    [Fact]
    public void CorruptFileReportedNotThrown()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{{{");
        Assert.Null(JournalStore.TryLoad(path, out var err));
        Assert.NotNull(err);
        Assert.Null(JournalStore.TryLoad(Path.Combine(_dir, "missing.json"), out var err2));
        Assert.Null(err2);
    }

    [Fact]
    public void OpenEntriesCanBeFilteredByGuard()
    {
        var store = Store();
        store.Record(Entry("a"));
        store.Record(new JournalEntry { Guard = GuardId.ScreenSaver, Kind = JournalKind.SpiScreenSaveActive, Target = new JournalTarget { SessionId = 1 }, Original = JournalValue.Bool(true) });
        Assert.Single(store.OpenEntries(GuardId.ScreenSaver));
        Assert.Equal(2, store.OpenEntries().Count);
    }

    [Fact]
    public void DefaultPathsDifferByElevation()
    {
        var admin = JournalStore.DefaultPath(true);
        var user = JournalStore.DefaultPath(false);
        Assert.NotEqual(admin, user);
        Assert.EndsWith(Path.Combine("NoClose", "restore-journal.json"), admin);
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), admin);
    }
}
