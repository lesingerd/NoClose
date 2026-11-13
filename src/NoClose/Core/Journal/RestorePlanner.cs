namespace NoClose.Core.Journal;

public sealed record RestoreContext(int CurrentSessionId, string UserSid, bool IsElevated);

public abstract record RestoreAction(string EntryId);

/// <summary>
/// Write <see cref="Original"/> back. When <see cref="OnlyIfCurrentEquals"/> is set (NoClose's own applied value) the write is only made
/// if the current value still equals it (or already equals the original): a value somebody else changed is left alone.
/// </summary>
public sealed record RestoreRegistryValueAction(string EntryId, JournalTarget Target, JournalValue Original, JournalValue? OnlyIfCurrentEquals = null) : RestoreAction(EntryId);

/// <summary>Delete the value (it was absent originally); same <see cref="OnlyIfCurrentEquals"/> rule as the restore action.</summary>
public sealed record DeleteRegistryValueAction(string EntryId, JournalTarget Target, JournalValue? OnlyIfCurrentEquals = null) : RestoreAction(EntryId);

/// <summary>
/// Make <see cref="Original"/> the active power scheme. If <see cref="OnlyIfActiveIs"/> is set the switch is only made when
/// that scheme is currently active (so we never override a plan the user chose after the crash).
/// </summary>
public sealed record ActivateSchemeAction(string EntryId, Guid Original, Guid? OnlyIfActiveIs) : RestoreAction(EntryId);

public sealed record DeleteSchemeAction(string EntryId, Guid Temp) : RestoreAction(EntryId);

public sealed record SetSpiScreenSaveActiveAction(string EntryId, bool Active) : RestoreAction(EntryId);

/// <summary>Nothing to do. If <see cref="KeepEntry"/> the entry stays in the journal for a later attempt.</summary>
public sealed record SkipAction(string EntryId, string Reason, bool KeepEntry) : RestoreAction(EntryId);

public sealed class RestorePlan
{
    public List<RestoreAction> Actions { get; } = new();
}

/// <summary>Pure: turns a journal into an ordered list of restore actions (newest change first).</summary>
public static class RestorePlanner
{
    public static RestorePlan Plan(RestoreJournal journal, RestoreContext ctx)
    {
        var plan = new RestorePlan();
        var ordered = journal.Entries
            .Select((e, i) => (e, i))
            .Where(x => x.e.Status != JournalEntryStatus.Restored)
            // Entries are only ever appended (adopted ones come before new ones), so list order is the true change order.
            // RecordedUtc is wall clock time and can jump backwards (NTP, manual change): it is for display only.
            .OrderByDescending(x => x.i)
            .Select(x => x.e);

        foreach (var e in ordered)
        {
            if (e.RequiresAdmin && !ctx.IsElevated)
            {
                plan.Actions.Add(new SkipAction(e.Id, "requires elevation", KeepEntry: true));
                continue;
            }

            switch (e.Kind)
            {
                case JournalKind.RegistryValue:
                    if (e.Original == null || string.IsNullOrEmpty(e.Target.Key) || string.IsNullOrEmpty(e.Target.Hive) || string.IsNullOrEmpty(e.Target.Name))
                    {
                        plan.Actions.Add(new SkipAction(e.Id, "registry entry is incomplete", KeepEntry: false));
                        break;
                    }
                    if (e.Original.IsAbsent) plan.Actions.Add(new DeleteRegistryValueAction(e.Id, e.Target, e.Applied));
                    else plan.Actions.Add(new RestoreRegistryValueAction(e.Id, e.Target, e.Original, e.Applied));
                    break;

                case JournalKind.PowerActiveScheme:
                    if (!Guid.TryParse(e.Target.OriginalScheme, out var original))
                    {
                        plan.Actions.Add(new SkipAction(e.Id, "original power scheme unknown", KeepEntry: false));
                        break;
                    }
                    if (Guid.TryParse(e.Target.TempScheme, out var temp))
                    {
                        plan.Actions.Add(new ActivateSchemeAction(e.Id, original, temp));
                        plan.Actions.Add(new DeleteSchemeAction(e.Id, temp));
                    }
                    else
                    {
                        // The journal was written but the duplicate was never created: the active plan was never switched.
                        plan.Actions.Add(new SkipAction(e.Id, "temporary power scheme was never created; nothing to restore", KeepEntry: false));
                    }
                    break;

                case JournalKind.SpiScreenSaveActive:
                    if (e.Target.SessionId != ctx.CurrentSessionId || !string.Equals(journal.UserSid, ctx.UserSid, StringComparison.OrdinalIgnoreCase))
                    {
                        plan.Actions.Add(new SkipAction(e.Id, "screen saver runtime state belongs to another logon session; already reset", KeepEntry: false));
                        break;
                    }
                    if (e.Original?.AsBool() is { } active) plan.Actions.Add(new SetSpiScreenSaveActiveAction(e.Id, active));
                    else plan.Actions.Add(new SkipAction(e.Id, "no original screen saver state recorded", KeepEntry: false));
                    break;
            }
        }
        return plan;
    }
}

/// <summary>Pure rule for registry restores: never overwrite a value somebody else changed after NoClose applied its own.</summary>
public static class RegistryRestoreRule
{
    /// <summary>
    /// True when the restore should write the original back. <paramref name="applied"/> null (unknown) means unconditional.
    /// Writes when the current value is NoClose's applied value; when it already equals the original the write is harmless.
    /// </summary>
    public static bool ShouldRestore(JournalValue current, JournalValue original, JournalValue? applied) =>
        applied == null || current.ValueEquals(applied) || current.ValueEquals(original);

    /// <summary>True if nothing needs writing at all (the value already is the original).</summary>
    public static bool AlreadyOriginal(JournalValue current, JournalValue original) => current.ValueEquals(original);
}
