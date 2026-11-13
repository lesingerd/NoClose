using System.ComponentModel;
using System.Runtime.InteropServices;
using NoClose.Core.Guards;
using NoClose.Core.Text;
using NoClose.Logging;
using NoClose.Platform.Interop;

namespace NoClose.Guards;

/// <summary>
/// Opt-in, no administrator, changes no setting. Registers a shutdown block reason on the visible status window and answers
/// WM_QUERYENDSESSION with FALSE for ordinary logoff/shutdown/restart requests so the user can decide. Best effort and always
/// user-overridable; forced restarts, RDP limits and administrator logoffs cannot be blocked.
/// </summary>
internal sealed class EndSessionGuard : GuardBase
{
    private IntPtr _hwnd;
    private volatile bool _applied;

    public override GuardId Id => GuardId.EndSession;
    public override string DisplayName => "Ask before logoff/shutdown";
    public override string Description =>
        "Asks Windows to wait before logoff/shutdown. You can always choose 'Shut down anyway'. Forced restarts (e.g. after an update deadline), " +
        "remote-desktop time limits and administrator logoffs cannot be blocked. While this is on, the status window stays visible (minimized).";

    /// <summary>Read by the status window's WndProc.</summary>
    public bool IsApplied => _applied;

    public override GuardAvailability CheckAvailability(GuardContext ctx) =>
        ctx.StatusWindowHandle == IntPtr.Zero
            ? new GuardAvailability(false, GuardStatus.Unavailable, "The status window is not available.")
            : GuardAvailability.Ok;

    public override GuardResult Apply(GuardContext ctx)
    {
        var avail = CheckAvailability(ctx);
        if (!avail.CanApply) return Set(avail.StatusIfNot, avail.Reason);
        ctx.EnsureStatusWindowVisibleMinimized();
        _hwnd = ctx.StatusWindowHandle;
        if (!User32.ShutdownBlockReasonCreate(_hwnd, ReasonText.ShutdownBlockReason(ctx.Reason)))
        {
            var err = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            ctx.Log.Error(LogCategory.EndSession, "ShutdownBlockReasonCreate failed: " + err);
            return Set(GuardStatus.Error, "Windows refused the shutdown block reason: " + err);
        }
        _applied = true;
        ctx.Log.Info(LogCategory.EndSession, "Shutdown block reason registered");
        return Set(GuardStatus.Applied, "Windows will ask before ordinary logoff/shutdown.");
    }

    public override GuardResult Reapply(GuardContext ctx)
    {
        if (!_applied) return new GuardResult(Status, Message);
        ctx.EnsureStatusWindowVisibleMinimized();
        // Re-creating is idempotent and keeps the text current.
        User32.ShutdownBlockReasonCreate(_hwnd, ReasonText.ShutdownBlockReason(ctx.Reason));
        return new GuardResult(Status, Message);
    }

    public override GuardResult Restore(GuardContext ctx)
    {
        _applied = false;
        if (_hwnd != IntPtr.Zero)
        {
            User32.ShutdownBlockReasonDestroy(_hwnd);
            ctx.Log.Info(LogCategory.EndSession, "Shutdown block reason removed");
            _hwnd = IntPtr.Zero;
        }
        return Set(GuardStatus.Off, "Not blocking logoff/shutdown");
    }
}
