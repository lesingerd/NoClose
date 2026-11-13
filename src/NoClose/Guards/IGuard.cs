using NoClose.Core.Guards;

namespace NoClose.Guards;

internal sealed record GuardResult(GuardStatus Status, string Message);

internal sealed record GuardAvailability(bool CanApply, GuardStatus StatusIfNot, string Reason)
{
    public static readonly GuardAvailability Ok = new(true, GuardStatus.Off, "");
}

internal interface IGuard
{
    GuardId Id { get; }
    string DisplayName { get; }
    /// <summary>Plain-language description shown in the UI.</summary>
    string Description { get; }
    bool IsAdminRequired { get; }
    /// <summary>True: changes user/machine settings, so it is journaled and needs one-time acknowledgement.</summary>
    bool ChangesSettings { get; }
    bool RequiresUiThread { get; }
    GuardAvailability CheckAvailability(GuardContext ctx);
    /// <summary>Journals BEFORE changing anything.</summary>
    GuardResult Apply(GuardContext ctx);
    /// <summary>Idempotent; detects reverts but never fights them.</summary>
    GuardResult Reapply(GuardContext ctx);
    /// <summary>Restores from the journal entries owned by this guard.</summary>
    GuardResult Restore(GuardContext ctx);
    GuardStatus Status { get; }
    string Message { get; }
    /// <summary>Text of the confirmation dialog shown before the first enable (settings-changing guards only).</summary>
    string ConfirmationText { get; }
}
