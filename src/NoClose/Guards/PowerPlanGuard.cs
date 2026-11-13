using NoClose.Core.Diagnostics;
using NoClose.Core.Guards;
using NoClose.Core.Journal;
using NoClose.Logging;
using NoClose.Platform;

namespace NoClose.Guards;

/// <summary>
/// Opt-in, administrator. Works on a DUPLICATE of the active power plan so the user's own plan is never edited; the duplicate
/// is activated while NoClose runs and deleted again on restore.
/// </summary>
internal sealed class PowerPlanGuard : GuardBase
{
    private Guid? _tempScheme;
    private string? _entryId;

    public override GuardId Id => GuardId.PowerPlan;
    public override string DisplayName => "Power plan (temporary copy)";
    public override string Description =>
        "Creates a temporary copy of your active power plan with sleep, hibernate and display timeouts set to never (and the \"allow apps " +
        "to keep the PC awake\" settings on), activates it while NoClose runs, then switches back to your original plan and deletes the copy. " +
        "Your own plan is never edited. Settings enforced by Group Policy are skipped.";
    public override bool IsAdminRequired => true;
    public override bool ChangesSettings => true;

    public override string ConfirmationText =>
        "While NoClose is active this guard will:" + Environment.NewLine +
        " - create a temporary duplicate of your active power plan named \"NoClose temporary (from ...)\"" + Environment.NewLine +
        " - change the selected items in that copy (see the item list) and make the copy the active plan" + Environment.NewLine + Environment.NewLine +
        "When NoClose stops or exits (or at the next start if it crashed) your original plan is re-activated and the copy is deleted. " +
        "Your original plan is not modified. Items controlled by policy are not changed.";

    private const string PolicyActiveSchemeKey = PowerGuids.PolicyPowerSettingsRoot;

    private static bool ActiveSchemePolicy(GuardContext ctx) =>
        ctx.Reader.ReadRegistry(RegHive.HKLM, PolicyActiveSchemeKey, "ActivePowerScheme") != null;

    private static bool ItemBlockedByPolicy(GuardContext ctx, PowerPlanItem item)
    {
        var key = PowerGuids.PolicyKeyFor(item.Setting);
        return ctx.Reader.ReadRegistry(RegHive.HKLM, key, "ACSettingIndex") != null
               || ctx.Reader.ReadRegistry(RegHive.HKLM, key, "DCSettingIndex") != null;
    }

    public override GuardAvailability CheckAvailability(GuardContext ctx)
    {
        if (!ctx.IsElevated) return NeedsElevation("Changing the power plan");
        if (ActiveSchemePolicy(ctx))
            return new GuardAvailability(false, GuardStatus.BlockedByPolicy,
                "Your organisation selects the active power plan ('Select an active power plan' under Computer Configuration > Administrative Templates > System > Power Management). NoClose will not change it. Ask the machine owner or IT administrator.");
        return GuardAvailability.Ok;
    }

    private IEnumerable<PowerPlanItem> SelectedItems(GuardContext ctx)
    {
        var items = ctx.Settings.GetGuard(GuardId.PowerPlan).Items;
        return PowerPlanItems.All.Where(i => items != null && items.TryGetValue(i.Key, out var on) ? on : i.DefaultOn);
    }

    public override GuardResult Apply(GuardContext ctx)
    {
        var avail = CheckAvailability(ctx);
        if (!avail.CanApply) return Set(avail.StatusIfNot, avail.Reason);

        Guid original;
        Guid baseScheme;
        try { original = ctx.Power.GetActiveScheme(); }
        catch (PowerApiException ex) { return Set(ex.IsAccessDenied ? GuardStatus.NeedsElevation : GuardStatus.Error, ex.Message); }

        var originalName = ctx.Power.TryReadFriendlyName(original) ?? PowerGuids.FriendlySchemeName(original);

        // Never duplicate one of our own leftover temporary plans as if it were the user's plan: restore would then "restore" the temp copy.
        // If an open journal entry knows the real original, chain on it; otherwise refuse.
        if (OrphanSchemePlanner.IsNoCloseTemporaryName(originalName))
        {
            var known = ctx.Journal.OpenEntries(GuardId.PowerPlan)
                .LastOrDefault(e => string.Equals(e.Target.TempScheme, original.ToString("D"), StringComparison.OrdinalIgnoreCase)
                                    && Guid.TryParse(e.Target.OriginalScheme, out _));
            if (known == null)
                return Set(GuardStatus.Error,
                    "A previous NoClose temporary power plan is still active. Run \"NoClose --restore\" or select your own plan in Power Options, then start again.");
            original = Guid.Parse(known.Target.OriginalScheme!);
            originalName = ctx.Power.TryReadFriendlyName(original) ?? PowerGuids.FriendlySchemeName(original);
            baseScheme = Guid.Parse(known.Target.TempScheme!);
        }
        else baseScheme = original;
        var tempName = $"NoClose temporary (from {originalName})";
        var entry = new JournalEntry
        {
            Guard = GuardId.PowerPlan,
            Kind = JournalKind.PowerActiveScheme,
            Target = new JournalTarget { OriginalScheme = original.ToString("D"), TempScheme = null, TempName = tempName },
        };
        ctx.Journal.Record(entry); // FIRST, with tempScheme = null
        _entryId = entry.Id;
        try
        {
            var temp = ctx.Power.DuplicateScheme(baseScheme);
            // Name first (makes a crash orphan identifiable), then record the GUID.
            try { ctx.Power.WriteFriendlyName(temp, tempName); } catch (PowerApiException ex) { ctx.Log.Warn(LogCategory.Guard, "Could not name the temporary plan: " + ex.Message); }
            ctx.Journal.Update(entry.Id, e => e.Target.TempScheme = temp.ToString("D"));
            _tempScheme = temp;

            var skipped = new List<string>();
            var changed = new List<string>();
            foreach (var item in SelectedItems(ctx))
            {
                if (ItemBlockedByPolicy(ctx, item))
                {
                    skipped.Add(item.DisplayName);
                    ctx.Log.Info(LogCategory.Guard, $"Power plan item '{item.DisplayName}' is set by policy; skipped");
                    continue;
                }
                foreach (bool ac in new[] { true, false })
                {
                    try { ctx.Power.WriteValue(temp, item.Subgroup, item.Setting, ac, item.NewValue); }
                    catch (PowerApiException ex) when (!ex.IsAccessDenied)
                    {
                        // Not every machine has every setting (e.g. no DC for desktops); not fatal.
                        ctx.Log.Warn(LogCategory.Guard, $"Power plan item '{item.DisplayName}' ({(ac ? "AC" : "DC")}) not written: {ex.Message}");
                    }
                }
                changed.Add(item.DisplayName);
            }

            ctx.Power.SetActiveScheme(temp);
            if (ctx.Power.TryGetActiveScheme() != temp)
                throw new InvalidOperationException("Windows did not activate the temporary power plan.");
            ctx.Journal.MarkApplied(entry.Id);
            ctx.Log.Info(LogCategory.Guard, $"Power plan: {original} -> temporary {temp}; changed: {string.Join(", ", changed)}; skipped by policy: {string.Join(", ", skipped)}");
            var msg = "Temporary plan active" + (skipped.Count > 0 ? $" ({skipped.Count} item(s) set by your organisation were skipped)" : "");
            return Set(GuardStatus.Applied, msg);
        }
        catch (Exception ex)
        {
            ctx.Log.Error(LogCategory.Guard, "Power plan apply failed, rolling back: " + ex.Message);
            RestoreFromJournal(ctx);
            return Set(ex is PowerApiException { IsAccessDenied: true } ? GuardStatus.NeedsElevation : GuardStatus.Error, "Power plan change failed: " + ex.Message);
        }
    }

    public override GuardResult Reapply(GuardContext ctx)
    {
        if (Status is GuardStatus.Off or GuardStatus.BlockedByPolicy or GuardStatus.NeedsElevation) return new GuardResult(Status, Message);
        if (_tempScheme is not { } temp) return new GuardResult(Status, Message);
        var active = ctx.Power.TryGetActiveScheme();
        if (active != temp)
        {
            if (Status != GuardStatus.Reverted)
                ctx.Log.Warn(LogCategory.Guard, "Value changed externally (possibly by policy/management or by the user); NoClose will not re-apply (active power plan is no longer NoClose's temporary plan)");
            return Set(GuardStatus.Reverted, "The active power plan was changed by you or by management; NoClose will not switch it back.");
        }
        return Set(GuardStatus.Applied, Message.Length > 0 && Status == GuardStatus.Applied ? Message : "Temporary plan active");
    }

    public override GuardResult Restore(GuardContext ctx)
    {
        var r = RestoreFromJournal(ctx, "Original power plan restored");
        if (r.Status == GuardStatus.Off) { _tempScheme = null; _entryId = null; }
        return r;
    }
}
