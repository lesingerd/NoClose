using System.ComponentModel;
using System.Diagnostics;
using NoClose.Core.Diagnostics;
using NoClose.Core.Guards;
using NoClose.Core.Journal;
using NoClose.Logging;
using NoClose.Platform;

namespace NoClose.Journal;

internal sealed record RestoreSummary(int Restored, int Skipped, int Failed, bool NeedsElevation, IReadOnlyList<string> Messages)
{
    public static readonly RestoreSummary Empty = new(0, 0, 0, false, Array.Empty<string>());
    public bool AnyFailure => Failed > 0;
    public RestoreSummary Add(RestoreSummary o) =>
        new(Restored + o.Restored, Skipped + o.Skipped, Failed + o.Failed, NeedsElevation || o.NeedsElevation, Messages.Concat(o.Messages).ToList());
    public string ToText() =>
        $"Restored {Restored}, skipped {Skipped}, failed {Failed}." + (NeedsElevation ? " Some entries need an elevated run." : "")
        + (Messages.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, Messages) : "");
}

/// <summary>Executes <see cref="RestorePlan"/>s against the real machine.</summary>
internal sealed class RestoreService
{
    private readonly RegistryAccessor _registry;
    private readonly PowerSchemeAccessor _power;
    private readonly ScreenSaverAccessor _screenSaver;
    private readonly ILogSink _log;
    private readonly RestoreContext? _contextOverride;

    /// <param name="contextOverride">Tests inject the elevation state / session so that both branches always run.</param>
    public RestoreService(RegistryAccessor registry, PowerSchemeAccessor power, ScreenSaverAccessor screenSaver, ILogSink log,
        RestoreContext? contextOverride = null)
    {
        _contextOverride = contextOverride;
        _registry = registry;
        _power = power;
        _screenSaver = screenSaver;
        _log = log;
    }

    public static RestoreContext CurrentContext() =>
        new(Process.GetCurrentProcess().SessionId, Elevation.CurrentUserSid, Elevation.IsElevated);

    /// <summary>Restores the open entries of the live store (optionally only one guard's). Idempotent.</summary>
    public RestoreSummary RestoreStore(JournalStore store, GuardId? only = null, TimeSpan? budget = null)
    {
        var journal = store.Snapshot();
        var open = store.OpenEntries(only).ToList();
        if (open.Count == 0) { store.DeleteIfEmpty(); return RestoreSummary.Empty; }

        var sub = new RestoreJournal
        {
            SessionId = journal.SessionId, ProcessId = journal.ProcessId, ProcessStartUtc = journal.ProcessStartUtc,
            Machine = journal.Machine, UserSid = journal.UserSid, CreatedUtc = journal.CreatedUtc, Entries = open
        };
        var summary = Execute(sub, _contextOverride ?? CurrentContext(), store, budget ?? TimeSpan.FromSeconds(30));
        store.DeleteIfEmpty();
        return summary;
    }

    /// <summary>
    /// Startup / --restore: processes the journals at the given paths. A journal whose owning process is still alive is left alone.
    /// </summary>
    public RestoreSummary RestoreFromDisk(IEnumerable<string> journalPaths, TimeSpan? budget = null)
    {
        var total = RestoreSummary.Empty;
        var ctx = _contextOverride ?? CurrentContext();
        foreach (var path in journalPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                total = total.Add(RestoreOne(path, ctx, budget));
            }
            catch (Exception ex)
            {
                // One broken journal must never stop the others from being processed.
                _log.Error(LogCategory.Journal, $"Restoring journal {path} failed: {ex.Message}");
                bool denied = ex is UnauthorizedAccessException;
                total = total.Add(new RestoreSummary(0, 0, denied ? 0 : 1, denied, new[] { $"Journal {path} could not be processed: {ex.Message}" }));
            }
        }
        return total;
    }

    private RestoreSummary RestoreOne(string path, RestoreContext ctx, TimeSpan? budget)
    {
        var journal = JournalStore.TryLoad(path, out var error, out var accessDenied);
        if (journal == null)
        {
            if (error != null)
            {
                _log.Warn(LogCategory.Journal, $"Journal {path} unreadable: {error}");
                // A journal we are not allowed to read (written by an elevated run) is "present, needs elevation", not "absent".
                bool denied = accessDenied;
                return new RestoreSummary(0, 0, denied ? 0 : 1, denied, new[] { $"Journal {path} unreadable: {error}" });
            }
            if (JournalStore.ExistsButDenied(path))
            {
                _log.Warn(LogCategory.Journal, $"Journal {path} exists but cannot be read without administrator rights.");
                return new RestoreSummary(0, 0, 0, true, new[] { $"Journal {path} exists but needs an elevated run to be read." });
            }
            return RestoreSummary.Empty;
        }
        if (journal.ProcessId != Environment.ProcessId && IsLive(journal.ProcessId, journal.ProcessStartUtc))
        {
            _log.Info(LogCategory.Journal, $"Journal {path} belongs to running NoClose process {journal.ProcessId}; leaving it.");
            return new RestoreSummary(0, 1, 0, false, new[] { $"Journal {path} belongs to a running NoClose (pid {journal.ProcessId})." });
        }
        _log.Warn(LogCategory.Journal, $"Stale journal found at {path} (pid {journal.ProcessId}, {journal.Entries.Count} entries); restoring.");
        var store = new JournalStore(path, _log);
        store.Adopt(journal);
        var s = Execute(journal, ctx, store, budget ?? TimeSpan.FromSeconds(30));
        store.DeleteIfEmpty();
        return s;
    }

    /// <summary>
    /// Elevated start: deletes inactive "NoClose temporary (...)" power plans that no open journal entry refers to (crash orphans), and
    /// reports an active one instead of deleting it. Only plans NoClose itself created (by name) are ever touched.
    /// </summary>
    public IReadOnlyList<string> CleanupOrphanSchemes(IEnumerable<string> journalPaths, JournalStore? liveStore)
    {
        var messages = new List<string>();
        try
        {
            var referenced = new HashSet<Guid>();
            void Collect(IEnumerable<JournalEntry> entries)
            {
                foreach (var e in entries)
                    if (e.Status != JournalEntryStatus.Restored && Guid.TryParse(e.Target.TempScheme, out var g)) referenced.Add(g);
            }
            if (liveStore != null) Collect(liveStore.OpenEntries());
            foreach (var p in journalPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                if (JournalStore.TryLoad(p, out _) is { } j) Collect(j.Entries);

            var plan = OrphanSchemePlanner.Plan(_power.EnumerateSchemes(), _power.TryGetActiveScheme(), referenced);
            foreach (var g in plan.Delete)
            {
                try
                {
                    _power.DeleteScheme(g);
                    _log.Info(LogCategory.Journal, $"Deleted orphaned NoClose temporary power plan {g}");
                    messages.Add($"Deleted orphaned temporary power plan {g}.");
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Journal, $"Could not delete orphaned power plan {g}: {ex.Message}");
                }
            }
            foreach (var g in plan.ActiveOrphans)
            {
                var text = $"The active power plan {g} is a leftover NoClose temporary plan. Select your own plan in Power Options; NoClose will not change it.";
                _log.Warn(LogCategory.Journal, text);
                messages.Add(text);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(LogCategory.Journal, "Orphaned power plan check failed: " + ex.Message);
        }
        return messages;
    }

    public static bool IsLive(int pid, DateTime startUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return Math.Abs((p.StartTime.ToUniversalTime() - startUtc).TotalSeconds) <= 1;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false; // no such process: the owner is gone
        }
        catch (Win32Exception)
        {
            return true; // cannot tell (query denied): treat as live and leave the journal alone
        }
    }

    private RestoreSummary Execute(RestoreJournal journal, RestoreContext ctx, JournalStore store, TimeSpan budget)
    {
        var plan = RestorePlanner.Plan(journal, ctx);
        var sw = Stopwatch.StartNew();
        int restored = 0, skipped = 0, failed = 0;
        bool needsElevation = false;
        var messages = new List<string>();
        var failedEntries = new HashSet<string>();
        var doneEntries = new List<string>();
        var keptEntries = new HashSet<string>();

        foreach (var action in plan.Actions)
        {
            if (sw.Elapsed > budget)
            {
                _log.Warn(LogCategory.Journal, "Restore time budget exhausted; remaining entries kept in the journal.");
                keptEntries.Add(action.EntryId);
                skipped++;
                continue;
            }
            try
            {
                switch (action)
                {
                    case SkipAction s:
                        _log.Info(LogCategory.Journal, "Skip " + s.EntryId[..8] + ": " + s.Reason);
                        messages.Add("Skipped: " + s.Reason);
                        skipped++;
                        if (s.KeepEntry) { keptEntries.Add(s.EntryId); needsElevation |= s.Reason.Contains("elevation"); }
                        else doneEntries.Add(s.EntryId);
                        break;

                    case RestoreRegistryValueAction r:
                        {
                            var hive = RegistryAccessor.ParseHive(r.Target.Hive);
                            var view = RegistryAccessor.ParseView(r.Target.View);
                            var current = _registry.Capture(hive, r.Target.Key!, r.Target.Name!, view);
                            if (!RegistryRestoreRule.ShouldRestore(current, r.Original, r.OnlyIfCurrentEquals))
                            {
                                LeaveAlone(r.EntryId, r.Target, messages, doneEntries);
                                skipped++;
                                break;
                            }
                            _registry.Apply(hive, r.Target.Key!, r.Target.Name!, r.Original, view);
                            _log.Info(LogCategory.Journal, $"Registry value restored: {r.Target.Hive}\\{r.Target.Key} [{r.Target.Name}]");
                            doneEntries.Add(r.EntryId);
                            break;
                        }

                    case DeleteRegistryValueAction d:
                        {
                            var hive = RegistryAccessor.ParseHive(d.Target.Hive);
                            var view = RegistryAccessor.ParseView(d.Target.View);
                            var current = _registry.Capture(hive, d.Target.Key!, d.Target.Name!, view);
                            if (!RegistryRestoreRule.ShouldRestore(current, JournalValue.Absent(), d.OnlyIfCurrentEquals))
                            {
                                LeaveAlone(d.EntryId, d.Target, messages, doneEntries);
                                skipped++;
                                break;
                            }
                            _registry.DeleteValue(hive, d.Target.Key!, d.Target.Name!, view);
                            _log.Info(LogCategory.Journal, $"Registry value removed (was absent): {d.Target.Hive}\\{d.Target.Key} [{d.Target.Name}]");
                            doneEntries.Add(d.EntryId);
                            break;
                        }

                    case ActivateSchemeAction a:
                        ActivateScheme(a);
                        doneEntries.Add(a.EntryId);
                        break;

                    case DeleteSchemeAction ds:
                        DeleteScheme(ds);
                        doneEntries.Add(ds.EntryId);
                        break;

                    case SetSpiScreenSaveActiveAction sp:
                        _screenSaver.SetActive(sp.Active);
                        _log.Info(LogCategory.Journal, $"Screen saver active flag restored to {sp.Active}");
                        doneEntries.Add(sp.EntryId);
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                failedEntries.Add(action.EntryId);
                _log.Error(LogCategory.Journal, $"Restore action {action.GetType().Name} for {action.EntryId[..8]} failed: {ex.Message}");
                messages.Add($"Failed: {action.GetType().Name}: {ex.Message}");
                if (ex is PowerApiException { IsAccessDenied: true }) needsElevation = true;
            }
        }

        foreach (var id in doneEntries.Distinct())
        {
            if (failedEntries.Contains(id) || keptEntries.Contains(id)) continue;
            try
            {
                store.MarkRestored(id);
                restored++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // The change was undone but the journal file cannot be updated (e.g. an admin-owned file, not elevated): keep it.
                _log.Warn(LogCategory.Journal, $"Could not update the journal after restoring {id[..8]}: {ex.Message}");
                messages.Add("Journal could not be updated: " + ex.Message);
                needsElevation = true;
            }
        }
        foreach (var id in failedEntries)
        {
            try { store.MarkFailed(id); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _log.Warn(LogCategory.Journal, $"Could not mark {id[..8]} as failed: {ex.Message}");
                needsElevation = true;
            }
        }
        return new RestoreSummary(restored, skipped, failed, needsElevation, messages);
    }

    private void LeaveAlone(string entryId, JournalTarget target, List<string> messages, List<string> doneEntries)
    {
        var text = $"{target.Hive}\\{target.Key} [{target.Name}] was changed externally since NoClose set it; left alone.";
        _log.Warn(LogCategory.Journal, text);
        messages.Add("Skipped: " + text);
        doneEntries.Add(entryId); // nothing of ours is left to restore: close the entry
    }

    private void ActivateScheme(ActivateSchemeAction a)
    {
        if (a.OnlyIfActiveIs is { } temp)
        {
            var active = _power.TryGetActiveScheme();
            if (active != temp)
            {
                _log.Info(LogCategory.Journal, "Active power scheme is no longer NoClose's temporary scheme; leaving the current plan alone.");
                return;
            }
        }
        if (_power.SchemeExists(a.Original))
        {
            _power.SetActiveScheme(a.Original);
            _log.Info(LogCategory.Journal, $"Original power scheme {a.Original} re-activated");
        }
        else
        {
            _power.SetActiveScheme(PowerGuids.Balanced);
            _log.Warn(LogCategory.Journal, $"Original power scheme {a.Original} no longer exists; activated Balanced instead.");
        }
    }

    private void DeleteScheme(DeleteSchemeAction d)
    {
        if (!_power.SchemeExists(d.Temp)) return; // already gone
        _power.DeleteScheme(d.Temp);
        _log.Info(LogCategory.Journal, $"Temporary power scheme {d.Temp} deleted");
    }
}
