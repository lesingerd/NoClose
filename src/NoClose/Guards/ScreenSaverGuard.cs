using System.ComponentModel;
using NoClose.Core.Diagnostics;
using NoClose.Core.Guards;
using NoClose.Core.Journal;
using NoClose.Core.Diagnostics.Rules;
using NoClose.Logging;
using NoClose.Platform;

namespace NoClose.Guards;

/// <summary>Switches the screen saver off for the running session only (SystemParametersInfo without profile update).</summary>
internal sealed class ScreenSaverGuard : GuardBase
{
    private static readonly string[] PolicyValues = { "ScreenSaveActive", "ScreenSaverIsSecure", "ScreenSaveTimeOut", "SCRNSAVE.EXE" };
    private DateTime _lastReassertLogUtc = DateTime.MinValue;
    private bool _retryOnUnlock;

    public override GuardId Id => GuardId.ScreenSaver;
    public override string DisplayName => "Screen saver off (this session)";
    public override string Description =>
        "Turns the screen saver off for the current logon session only; it is not written to your profile and your own setting returns " +
        "when NoClose stops. If your organisation sets the screen saver by policy NoClose does not touch it.";
    public override bool ChangesSettings => true; // session-only runtime flag, not persisted; journaled for crash recovery; opt-in with confirmation

    public override string ConfirmationText =>
        "While NoClose is active this guard will switch the screen saver off for your current logon session only," + Environment.NewLine +
        "using SystemParametersInfo (SPI_SETSCREENSAVEACTIVE) without writing it to your profile. The screen saver is often what locks" + Environment.NewLine +
        "an unattended session, so only enable this if that is what you want." + Environment.NewLine + Environment.NewLine +
        "Your own setting returns when NoClose stops or exits (or at the next start if it crashed). If your organisation sets the screen" + Environment.NewLine +
        "saver by policy NoClose does not touch it. NoClose's display request already keeps the screen saver from starting automatically.";

    public const string PolicyMessage =
        "Your organisation sets the screen saver through Group Policy ('Enable screen saver' / 'Password protect the screen saver' / " +
        "'Screen saver timeout' under User Configuration > Administrative Templates > Control Panel > Personalization). " +
        "NoClose will not change it. NoClose's display request normally keeps the screen saver from starting automatically while active; " +
        "if your organisation requires the screen to lock, ask the machine owner or IT administrator.";

    private static bool PolicyPresent(GuardContext ctx) =>
        PolicyValues.Any(v => ctx.Reader.ReadRegistry(RegHive.HKCU, ScreenSaverPolicyRule.Key, v) != null);

    public override GuardAvailability CheckAvailability(GuardContext ctx) =>
        PolicyPresent(ctx) ? new GuardAvailability(false, GuardStatus.BlockedByPolicy, PolicyMessage) : GuardAvailability.Ok;

    public override GuardResult Apply(GuardContext ctx)
    {
        _retryOnUnlock = false;
        if (PolicyPresent(ctx)) return Set(GuardStatus.BlockedByPolicy, PolicyMessage);

        bool original;
        try { original = ctx.ScreenSaver.GetActive(); }
        catch (Win32Exception ex) { return Set(GuardStatus.Error, "Cannot read screen saver state: " + ex.Message); }

        if (!original)
        {
            ctx.Log.Info(LogCategory.Guard, "Screen saver already off; nothing to change");
            return Set(GuardStatus.Applied, "Screen saver is already off");
        }

        var entry = new JournalEntry
        {
            Guard = GuardId.ScreenSaver,
            Kind = JournalKind.SpiScreenSaveActive,
            Target = new JournalTarget { SessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId },
            Original = JournalValue.Bool(true),
            Applied = JournalValue.Bool(false),
        };
        ctx.Journal.Record(entry); // journal BEFORE the change
        try
        {
            ctx.ScreenSaver.SetActive(false);
        }
        catch (Win32Exception ex) when (ScreenSaverAccessor.IsOperationInProgress(ex))
        {
            _retryOnUnlock = true;
            ctx.Log.Warn(LogCategory.Guard, "Screen saver change refused (session locked or power saving); will retry on next unlock");
            return Set(GuardStatus.Error, "Could not switch the screen saver off yet; will retry after unlock.");
        }
        catch (Win32Exception ex)
        {
            ctx.Journal.MarkRestored(entry.Id); // nothing was changed
            return Set(GuardStatus.Error, "Could not switch the screen saver off: " + ex.Message);
        }

        bool now;
        try { now = ctx.ScreenSaver.GetActive(); } catch (Win32Exception) { now = false; }
        if (now)
        {
            RestoreFromJournal(ctx);
            return Set(GuardStatus.Error, "Windows did not accept the screen saver change.");
        }
        ctx.Journal.MarkApplied(entry.Id);
        ctx.Log.Info(LogCategory.Guard, "Screen saver active flag: true -> false (this session only)");
        return Set(GuardStatus.Applied, "Screen saver is off for this session");
    }

    public override GuardResult Reapply(GuardContext ctx)
    {
        if (Status is GuardStatus.Off or GuardStatus.Reverted) return new GuardResult(Status, Message);
        var open = ctx.Journal.OpenEntries(GuardId.ScreenSaver);
        if (PolicyPresent(ctx))
        {
            // A policy appeared while we were applied: undo our journaled change first so the organisation's setting wins, then stand down.
            if (open.Count > 0) RestoreFromJournal(ctx, "Screen saver setting restored (policy appeared)");
            return Set(GuardStatus.BlockedByPolicy, PolicyMessage);
        }
        if (_retryOnUnlock) return new GuardResult(Status, Message); // the change was refused; the unlock handler retries it
        try
        {
            // Contract: detect reverts, never fight them. If the screen saver is on again (the user, a management agent, or a Windows
            // reset) NoClose reports it once and leaves it alone; it never opens a new journal entry from here.
            if (ctx.ScreenSaver.GetActive())
            {
                ctx.Log.Info(LogCategory.Guard, "Screen saver was switched on again by someone else; NoClose will not change it again");
                return Set(GuardStatus.Reverted, "The screen saver was switched on again by you or by management; NoClose will not switch it off again.");
            }
            return Set(GuardStatus.Applied, Message.Length > 0 && Status == GuardStatus.Applied ? Message : "Screen saver is off for this session");
        }
        catch (Win32Exception ex)
        {
            return Set(GuardStatus.Error, "Screen saver check failed: " + ex.Message);
        }
    }

    /// <summary>Called when the session unlocks so a refused change (error 329) is retried.</summary>
    public GuardResult? RetryIfPending(GuardContext ctx) => _retryOnUnlock ? Apply(ctx) : null;

    public override GuardResult Restore(GuardContext ctx)
    {
        _retryOnUnlock = false;
        // Decide by the journal, not by Status: an open entry always means NoClose changed something that must be put back.
        if (ctx.Journal.OpenEntries(GuardId.ScreenSaver).Count > 0)
            return RestoreFromJournal(ctx, "Screen saver setting restored");
        return Set(GuardStatus.Off, "Nothing was changed");
    }
}
