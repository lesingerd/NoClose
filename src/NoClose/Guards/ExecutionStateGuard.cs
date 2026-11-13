using NoClose.Core.Guards;
using NoClose.Logging;

namespace NoClose.Guards;

/// <summary>The core mechanism: system/display/execution power requests plus execution state on the engine thread.</summary>
internal sealed class ExecutionStateGuard : GuardBase
{
    public override GuardId Id => GuardId.ExecutionState;
    public override string DisplayName => "Keep awake (power requests)";
    public override string Description =>
        "Asks Windows not to sleep, turn the display off or start the screen saver while NoClose is active, using documented " +
        "power requests (execution state and power request objects). Nothing is changed on your system and no keyboard or mouse input is simulated.";

    public override GuardAvailability CheckAvailability(GuardContext ctx) => GuardAvailability.Ok;

    public override GuardResult Apply(GuardContext ctx)
    {
        ctx.Engine.Start(ctx.Reason, ctx.Settings.AwayMode);
        ctx.Log.Info(LogCategory.Guard, "Keep-awake guard applied");
        return Set(GuardStatus.Applied, "Power requests active");
    }

    public override GuardResult Reapply(GuardContext ctx)
    {
        ctx.Engine.Reassert("guard");
        var s = ctx.Engine.Status;
        if (s.State == NoClose.Engine.EngineState.Degraded)
            return Set(GuardStatus.Error, s.DegradedReason ?? "Degraded");
        if (s.State == NoClose.Engine.EngineState.Idle)
        {
            // An applied guard with an idle engine means the engine stopped unexpectedly: restart it and say so loudly.
            ctx.Log.Error(LogCategory.Guard, "Keep-awake engine was idle while the guard is applied; restarting it");
            ctx.Engine.Start(ctx.Reason, ctx.Settings.AwayMode);
            return Set(GuardStatus.Error, "The keep-awake engine had stopped; it was restarted.");
        }
        return Set(GuardStatus.Applied, "Power requests active");
    }

    public override GuardResult Restore(GuardContext ctx)
    {
        ctx.Engine.Stop("guard restore");
        return Set(GuardStatus.Off, "Power requests released");
    }
}
