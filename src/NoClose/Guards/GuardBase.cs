using NoClose.Core.Guards;
using NoClose.Logging;

namespace NoClose.Guards;

internal abstract class GuardBase : IGuard
{
    public abstract GuardId Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public virtual bool IsAdminRequired => false;
    public virtual bool ChangesSettings => false;
    public virtual bool RequiresUiThread => true;
    public virtual string ConfirmationText => "";

    public GuardStatus Status { get; protected set; } = GuardStatus.Off;
    public string Message { get; protected set; } = "";

    public abstract GuardAvailability CheckAvailability(GuardContext ctx);
    public abstract GuardResult Apply(GuardContext ctx);
    public abstract GuardResult Reapply(GuardContext ctx);
    public abstract GuardResult Restore(GuardContext ctx);

    /// <summary>Used by the manager to show Unavailable / NeedsElevation / BlockedByPolicy without applying.</summary>
    public void ForceStatus(GuardStatus status, string message)
    {
        Status = status;
        Message = message;
    }

    protected GuardResult Set(GuardStatus status, string message)
    {
        Status = status;
        Message = message;
        return new GuardResult(status, message);
    }

    /// <summary>Journal-driven restore of everything this guard recorded.</summary>
    protected GuardResult RestoreFromJournal(GuardContext ctx, string okMessage = "Restored")
    {
        var summary = ctx.Restore.RestoreStore(ctx.Journal, Id);
        if (summary.Failed > 0)
        {
            ctx.Log.Error(LogCategory.Guard, $"{DisplayName}: restore failed. {summary.ToText()}");
            return Set(GuardStatus.Error, "Restore failed: " + string.Join(" ", summary.Messages));
        }
        if (summary.NeedsElevation)
            return Set(GuardStatus.NeedsElevation, "Restore needs an elevated run (journal kept).");
        ctx.Log.Info(LogCategory.Guard, $"{DisplayName}: {okMessage} (restored {summary.Restored})");
        return Set(GuardStatus.Off, okMessage);
    }

    protected static GuardAvailability NeedsElevation(string what) =>
        new(false, GuardStatus.NeedsElevation, $"{what} requires administrator rights. Use \"Restart elevated\".");
}
