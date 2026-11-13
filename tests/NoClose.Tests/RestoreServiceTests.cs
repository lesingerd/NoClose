using System.Diagnostics;
using NoClose.Core.Diagnostics;
using NoClose.Core.Guards;
using NoClose.Core.Journal;
using NoClose.Journal;
using NoClose.Logging;
using NoClose.Platform;

namespace NoClose.Tests;

/// <summary>RestoreService executed against fakes: nothing here touches the registry, power plans or SPI.</summary>
public class RestoreServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noclose-restore-" + Guid.NewGuid().ToString("N"));

    public RestoreServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeRegistry : RegistryAccessor
    {
        public List<string> Calls { get; } = new();
        public Dictionary<string, JournalValue> Current { get; } = new();
        public override JournalValue Capture(RegHive hive, string key, string name, RegView view = RegView.Registry64) =>
            Current.TryGetValue(name, out var v) ? v : JournalValue.Absent();
        public override void Apply(RegHive hive, string key, string name, JournalValue value, RegView view = RegView.Registry64) =>
            Calls.Add($"apply {hive}\\{key}[{name}]={value.Data}");
        public override void SetValue(RegHive hive, string key, string name, object data, Microsoft.Win32.RegistryValueKind kind, RegView view = RegView.Registry64) =>
            throw new InvalidOperationException("must not be called");
        public override void DeleteValue(RegHive hive, string key, string name, RegView view = RegView.Registry64) =>
            Calls.Add($"delete {hive}\\{key}[{name}]");
    }

    private sealed class FakePower : PowerSchemeAccessor
    {
        public Guid Active { get; set; }
        public HashSet<Guid> Existing { get; } = new();
        public List<string> Calls { get; } = new();
        public override Guid GetActiveScheme() => Active;
        public override Guid? TryGetActiveScheme() => Active;
        public override void SetActiveScheme(Guid scheme) { Calls.Add("activate " + scheme); Active = scheme; }
        public override void DeleteScheme(Guid scheme) { Calls.Add("delete " + scheme); Existing.Remove(scheme); }
        public override string? TryReadFriendlyName(Guid scheme) => Existing.Contains(scheme) ? "name" : null;
    }

    private sealed class FakeScreenSaver : ScreenSaverAccessor
    {
        public bool? Value { get; private set; }
        public override bool GetActive() => Value ?? false;
        public override void SetActive(bool active) => Value = active;
    }

    private (RestoreService Svc, JournalStore Store, FakeRegistry Reg, FakePower Power, FakeScreenSaver Ss) Create(bool? elevated = null)
    {
        var reg = new FakeRegistry();
        var power = new FakePower();
        var ss = new FakeScreenSaver();
        var log = new MemoryLogSink();
        var store = new JournalStore(Path.Combine(_dir, "journal.json"), log);
        var ctx = elevated is { } e ? new RestoreContext(Process.GetCurrentProcess().SessionId, Elevation.CurrentUserSid, e) : null;
        return (new RestoreService(reg, power, ss, log, ctx), store, reg, power, ss);
    }

    private static JournalEntry Hkcu(string name, bool absent) => new()
    {
        Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue,
        Target = new JournalTarget { Hive = "HKCU", View = "64", Key = @"Software\Test", Name = name },
        Original = absent ? JournalValue.Absent() : JournalValue.DWord(5),
    };

    [Fact]
    public void RegistryEntriesRestoredNewestFirstThroughTheAccessor()
    {
        var (svc, store, reg, _, _) = Create();
        store.Record(Hkcu("one", absent: false));
        Thread.Sleep(5);
        store.Record(Hkcu("two", absent: true));

        var summary = svc.RestoreStore(store);

        Assert.Equal(2, summary.Restored);
        Assert.False(summary.AnyFailure);
        Assert.Equal(new[] { @"delete HKCU\Software\Test[two]", @"apply HKCU\Software\Test[one]=5" }, reg.Calls);
        Assert.False(File.Exists(store.Path)); // everything restored: journal removed
    }

    [Fact]
    public void RestoreIsIdempotent()
    {
        var (svc, store, reg, _, _) = Create();
        store.Record(Hkcu("one", absent: false));
        svc.RestoreStore(store);
        var second = svc.RestoreStore(store);
        Assert.Equal(0, second.Restored);
        Assert.Single(reg.Calls);
    }

    [Fact]
    public void ScreenSaverRestoredInSameSession()
    {
        var (svc, store, _, _, ss) = Create();
        store.Record(new JournalEntry
        {
            Guard = GuardId.ScreenSaver, Kind = JournalKind.SpiScreenSaveActive,
            Target = new JournalTarget { SessionId = Process.GetCurrentProcess().SessionId }, Original = JournalValue.Bool(true),
        });
        var summary = svc.RestoreStore(store, GuardId.ScreenSaver);
        Assert.Equal(1, summary.Restored);
        Assert.True(ss.Value);
    }

    [Fact]
    public void ScreenSaverEntryFromOtherSessionIsDiscardedWithoutTouchingSpi()
    {
        var (svc, store, _, _, ss) = Create();
        store.Record(new JournalEntry
        {
            Guard = GuardId.ScreenSaver, Kind = JournalKind.SpiScreenSaveActive,
            Target = new JournalTarget { SessionId = Process.GetCurrentProcess().SessionId + 40 }, Original = JournalValue.Bool(true),
        });
        var summary = svc.RestoreStore(store);
        Assert.Null(ss.Value);
        Assert.Equal(1, summary.Skipped);
        Assert.False(store.HasOpenEntries);
    }

    [Fact]
    public void RestoreCanBeLimitedToOneGuard()
    {
        var (svc, store, reg, _, ss) = Create();
        store.Record(Hkcu("one", absent: false));
        store.Record(new JournalEntry
        {
            Guard = GuardId.ScreenSaver, Kind = JournalKind.SpiScreenSaveActive,
            Target = new JournalTarget { SessionId = Process.GetCurrentProcess().SessionId }, Original = JournalValue.Bool(true),
        });
        svc.RestoreStore(store, GuardId.ScreenSaver);
        Assert.True(ss.Value);
        Assert.Empty(reg.Calls);
        Assert.Single(store.OpenEntries());
    }

    [Fact]
    public void PowerSchemeReactivatedAndTempDeletedOnlyIfTempStillActive()
    {
        var (svc, store, _, power, _) = Create(elevated: true); // injected: both elevation branches always run
        var original = Guid.NewGuid();
        var temp = Guid.NewGuid();
        power.Existing.Add(original);
        power.Existing.Add(temp);
        power.Active = temp;
        store.Record(new JournalEntry
        {
            Guard = GuardId.PowerPlan, Kind = JournalKind.PowerActiveScheme,
            Target = new JournalTarget { OriginalScheme = original.ToString(), TempScheme = temp.ToString() },
        });
        svc.RestoreStore(store);
        Assert.Equal(original, power.Active);
        Assert.Equal(new[] { "activate " + original, "delete " + temp }, power.Calls);
    }

    [Fact]
    public void ElevatedOnlyEntriesAreKeptWhenNotElevated()
    {
        var (svc, store, reg, _, _) = Create(elevated: false); // injected: independent of how the tests are run
        store.Record(new JournalEntry
        {
            Guard = GuardId.ActiveHours, Kind = JournalKind.RegistryValue,
            Target = new JournalTarget { Hive = "HKLM", View = "64", Key = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", Name = "ActiveHoursStart" },
            Original = JournalValue.DWord(8),
        });
        var summary = svc.RestoreStore(store);
        Assert.True(summary.NeedsElevation);
        Assert.Empty(reg.Calls);
        Assert.True(store.HasOpenEntries);
        Assert.True(File.Exists(store.Path));
    }

    [Fact]
    public void StaleJournalOnDiskIsRestoredAndRemoved()
    {
        var (svc, _, reg, _, _) = Create();
        var path = Path.Combine(_dir, "stale.json");
        var j = new RestoreJournal
        {
            ProcessId = 0x7ffffff0, // not a live process
            ProcessStartUtc = DateTime.UtcNow.AddDays(-2),
            SessionId = Process.GetCurrentProcess().SessionId,
            UserSid = Elevation.CurrentUserSid,
        };
        var e = Hkcu("stale", absent: true);
        e.Status = JournalEntryStatus.Pending;
        j.Entries.Add(e);
        File.WriteAllText(path, JournalSerializer.Serialize(j));

        var summary = svc.RestoreFromDisk(new[] { path, Path.Combine(_dir, "missing.json") });

        Assert.Equal(1, summary.Restored);
        Assert.Equal(new[] { @"delete HKCU\Software\Test[stale]" }, reg.Calls);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void JournalOwnedByLiveProcessIsLeftAlone()
    {
        var (svc, _, reg, _, _) = Create();
        var path = Path.Combine(_dir, "live.json");
        // A real second process that this test owns (a short ping), so the branch always runs and never touches anything else.
        using var other = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
        var j = new RestoreJournal { ProcessId = other.Id, ProcessStartUtc = SafeStart(other)!.Value.ToUniversalTime() };
        j.Entries.Add(Hkcu("live", absent: true));
        File.WriteAllText(path, JournalSerializer.Serialize(j));

        var summary = svc.RestoreFromDisk(new[] { path });

        Assert.Equal(0, summary.Restored);
        Assert.Empty(reg.Calls);
        Assert.True(File.Exists(path));
        }
        finally
        {
            try { other.Kill(); } catch { }
        }
    }

    private static DateTime? SafeStart(Process p)
    {
        try { return p.StartTime; } catch { return null; }
    }

    [Fact]
    public void UnreadableJournalIsReportedAsFailure()
    {
        var (svc, _, _, _, _) = Create();
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{{{");
        var summary = svc.RestoreFromDisk(new[] { path });
        Assert.True(summary.AnyFailure);
        Assert.True(File.Exists(path)); // never delete what we could not understand
    }
}
