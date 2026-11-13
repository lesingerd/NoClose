using NoClose.Core.Diagnostics;
using NoClose.Core.Guards;
using NoClose.Core.Journal;
using NoClose.Core.Time;
using NoClose.Journal;
using NoClose.Logging;

namespace NoClose.Guards;

/// <summary>
/// Opt-in, administrator. Sets the same user-facing values as Settings &gt; Windows Update &gt; Active hours (max 18 h) so Windows avoids
/// automatic restarts for updates while the window is open. It cannot override update deadlines or forced restarts.
/// </summary>
internal sealed class ActiveHoursGuard : GuardBase
{
    private static readonly string[] GpoValues = { "SetActiveHours", "ActiveHoursStart", "ActiveHoursEnd", "ActiveHoursMaxRange" };
    private static readonly string[] CspValues = { "ActiveHoursStart", "ActiveHoursEnd", "ActiveHoursMaxRange" };
    private const string GpoKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string CspKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\Update";

    private DateTime _lastShiftCheckUtc = DateTime.MinValue;

    public override GuardId Id => GuardId.ActiveHours;
    public override string DisplayName => "Windows Update active hours (18 h)";
    public override string Description =>
        "Sets the Windows Update active hours (the same setting as Settings > Windows Update > Active hours) to an 18 hour window starting now, so " +
        "Windows avoids automatic restarts for updates during that time. The original values are restored when NoClose stops. " +
        "This does not prevent restarts after an update deadline set by your organisation, and cannot cover 24 hours.";
    public override bool IsAdminRequired => true;
    public override bool ChangesSettings => true;

    public override string ConfirmationText =>
        "While NoClose is active this guard will change these Windows Update values under HKLM\\SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings:" + Environment.NewLine +
        " - ActiveHoursStart, ActiveHoursEnd (an 18 hour window starting at the current hour)" + Environment.NewLine +
        " - SmartActiveHoursState (set to 0 = manual active hours)" + Environment.NewLine + Environment.NewLine +
        "The original values are restored when NoClose stops or exits (or at the next start if it crashed). " +
        "Nothing under any Policies key is touched.";

    /// <summary>True when an hourly check found that the window ends before the activation does.</summary>
    public bool ShiftSuggested { get; private set; }

    public override GuardAvailability CheckAvailability(GuardContext ctx)
    {
        if (!ctx.IsElevated) return NeedsElevation("Changing the active hours");
        var found = new List<string>();
        foreach (var v in GpoValues)
            if (ctx.Reader.ReadRegistry(RegHive.HKLM, GpoKey, v) != null) found.Add("Group Policy " + v);
        foreach (var v in CspValues)
            if (ctx.Reader.ReadRegistry(RegHive.HKLM, CspKey, v) != null) found.Add("MDM Update/" + v);
        if (found.Count > 0)
            return new GuardAvailability(false, GuardStatus.BlockedByPolicy,
                "Your organisation sets the Windows Update active hours (" + string.Join(", ", found) +
                "; policy 'Turn off auto-restart for updates during active hours' / CSP Update/ActiveHoursStart). NoClose will not change it. Contact your IT administrator.");
        return GuardAvailability.Ok;
    }

    private (JournalEntry Entry, bool Ok) WriteValue(GuardContext ctx, string name, uint value)
    {
        var original = ctx.Registry.Capture(RegHive.HKLM, RegistryWriteGuard.ActiveHoursKey, name);
        var applied = JournalValue.DWord(value);
        var entry = new JournalEntry
        {
            Guard = GuardId.ActiveHours,
            Kind = JournalKind.RegistryValue,
            Target = new JournalTarget { Hive = "HKLM", View = "64", Key = RegistryWriteGuard.ActiveHoursKey, Name = name },
            Original = original,
            Applied = applied,
        };
        ctx.Journal.Record(entry); // journal BEFORE writing
        ctx.Registry.Apply(RegHive.HKLM, RegistryWriteGuard.ActiveHoursKey, name, applied);
        var verify = ctx.Registry.Capture(RegHive.HKLM, RegistryWriteGuard.ActiveHoursKey, name);
        if (!verify.ValueEquals(applied)) return (entry, false);
        ctx.Journal.MarkApplied(entry.Id);
        ctx.Log.Info(LogCategory.Guard, $"Active hours value {name}: {(original.IsAbsent ? "absent" : original.Data)} -> {value}");
        return (entry, true);
    }

    public override GuardResult Apply(GuardContext ctx)
    {
        var avail = CheckAvailability(ctx);
        if (!avail.CanApply) return Set(avail.StatusIfNot, avail.Reason);

        var plan = ActiveHoursPlanner.Plan(ctx.Clock.LocalNow(), ActiveHoursPlanner.DefaultMaxRange);
        try
        {
            foreach (var (name, value) in new (string, uint)[]
                     { ("SmartActiveHoursState", 0), ("ActiveHoursStart", (uint)plan.StartHour), ("ActiveHoursEnd", (uint)plan.EndHour) })
            {
                var (_, ok) = WriteValue(ctx, name, value);
                if (!ok)
                {
                    RestoreFromJournal(ctx);
                    return Set(GuardStatus.Error, $"Windows did not keep the value {name}; restored.");
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Log.Error(LogCategory.Guard, "Active hours apply failed, rolling back: " + ex.Message);
            RestoreFromJournal(ctx);
            return Set(ex is UnauthorizedAccessException ? GuardStatus.NeedsElevation : GuardStatus.Error, "Active hours change failed: " + ex.Message);
        }

        ShiftSuggested = false;
        _lastShiftCheckUtc = DateTime.UtcNow;
        return Set(GuardStatus.Applied,
            $"Active hours {plan.StartHour:00}:00 - {plan.EndHour:00}:00. Windows avoids automatic restarts during active hours (max 18 h). " +
            "This does not prevent restarts after an update deadline set by your organisation, and cannot cover 24 hours.");
    }

    /// <summary>Older open entries for the same value (failed restores, adopted stale entries) carry outdated Applied values: only the newest counts.</summary>
    private static IEnumerable<JournalEntry> NewestPerValue(GuardContext ctx) =>
        ctx.Journal.OpenEntries(GuardId.ActiveHours).Where(e => e.Target.Name != null).GroupBy(e => e.Target.Name!).Select(g => g.Last());

    private static JournalEntry? Newest(GuardContext ctx, string name) =>
        ctx.Journal.OpenEntries(GuardId.ActiveHours).LastOrDefault(e => e.Target.Name == name);

    public override GuardResult Reapply(GuardContext ctx)
    {
        if (Status is GuardStatus.Off or GuardStatus.BlockedByPolicy or GuardStatus.NeedsElevation) return new GuardResult(Status, Message);

        foreach (var e in NewestPerValue(ctx))
        {
            if (e.Applied == null || e.Target.Name == null) continue;
            var now = ctx.Registry.Capture(RegHive.HKLM, RegistryWriteGuard.ActiveHoursKey, e.Target.Name);
            if (!now.ValueEquals(e.Applied))
            {
                if (Status != GuardStatus.Reverted)
                    ctx.Log.Warn(LogCategory.Guard, $"Value changed externally (possibly by policy/management or by the user); NoClose will not re-apply ({e.Target.Name})");
                ShiftSuggested = false;
                return Set(GuardStatus.Reverted, "Active hours were changed by you or by management; NoClose will not change them again.");
            }
        }

        // Hourly: offer (never automatic) to shift the window forward if the activation will outlast it.
        if (DateTime.UtcNow - _lastShiftCheckUtc >= TimeSpan.FromHours(1))
        {
            _lastShiftCheckUtc = DateTime.UtcNow;
            var startE = Newest(ctx, "ActiveHoursStart");
            var endE = Newest(ctx, "ActiveHoursEnd");
            var remaining = ctx.RemainingActivation();
            if (startE?.Applied?.AsDWord() is { } s && endE?.Applied?.AsDWord() is { } en)
            {
                var left = ActiveHoursPlanner.HoursLeft((int)s, (int)en, ctx.Clock.LocalNow());
                bool open = remaining == null || remaining.Value.TotalHours > left;
                if (open && !ShiftSuggested)
                    ctx.Log.Info(LogCategory.Guard, $"Active hours window ends in {left:0.#} h but the activation continues; shift forward is offered");
                ShiftSuggested = open;
            }
        }
        return new GuardResult(Status, Message);
    }

    /// <summary>User confirmed "Shift active hours forward": re-plans using the same journal entries (originals unchanged).</summary>
    public GuardResult ShiftForward(GuardContext ctx)
    {
        if (Status != GuardStatus.Applied) return new GuardResult(Status, Message);
        var plan = ActiveHoursPlanner.Plan(ctx.Clock.LocalNow(), ActiveHoursPlanner.DefaultMaxRange);
        try
        {
            foreach (var (name, value) in new (string, uint)[] { ("ActiveHoursStart", (uint)plan.StartHour), ("ActiveHoursEnd", (uint)plan.EndHour) })
            {
                var e = Newest(ctx, name);
                if (e == null) continue;
                var applied = JournalValue.DWord(value);
                ctx.Journal.Update(e.Id, x => x.Applied = applied); // journal first
                ctx.Registry.Apply(RegHive.HKLM, RegistryWriteGuard.ActiveHoursKey, name, applied);
            }
        }
        catch (Exception ex)
        {
            return Set(GuardStatus.Error, "Shifting active hours failed: " + ex.Message);
        }
        ShiftSuggested = false;
        ctx.Log.Info(LogCategory.Guard, $"Active hours shifted to {plan.StartHour:00}:00 - {plan.EndHour:00}:00");
        return Set(GuardStatus.Applied, $"Active hours {plan.StartHour:00}:00 - {plan.EndHour:00}:00 (shifted).");
    }

    public override GuardResult Restore(GuardContext ctx)
    {
        ShiftSuggested = false;
        return RestoreFromJournal(ctx, "Active hours restored");
    }
}
