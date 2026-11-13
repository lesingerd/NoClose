using NoClose.Core.Guards;
using NoClose.Logging;

namespace NoClose.Guards;

/// <summary>Ordered set of guards. All calls happen on the UI thread.</summary>
internal sealed class GuardManager
{
    private readonly GuardContext _ctx;
    private readonly List<IGuard> _guards;
    private readonly Dictionary<GuardId, DateTime> _lastReapply = new();
    private HashSet<GuardId>? _override;

    public GuardManager(GuardContext ctx)
    {
        _ctx = ctx;
        _guards = new List<IGuard>
        {
            new ExecutionStateGuard(), new SilentMediaGuard(), new ScreenSaverGuard(), new PowerPlanGuard(), new ActiveHoursGuard(), new EndSessionGuard(),
        };
    }

    public IReadOnlyList<IGuard> Guards => _guards;
    public bool Running { get; private set; }

    /// <summary>Raised after any guard changed status.</summary>
    public event Action? Changed;

    /// <summary>Raises <see cref="Changed"/> (UI thread) after a guard changed state on its own, e.g. the silent media stream.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    public IGuard Get(GuardId id) => _guards.First(g => g.Id == id);
    public T Get<T>() where T : class, IGuard => _guards.OfType<T>().First();

    public IReadOnlyList<GuardState> States() =>
        _guards.Select(g => new GuardState(g.Id, g.Status, g.Message)).ToList();

    /// <summary>Per-run override of the guard selection (CLI --guards). null clears it.</summary>
    public void SetOverride(IReadOnlyList<GuardId>? ids) => _override = ids == null ? null : new HashSet<GuardId>(ids);

    public bool IsSelected(IGuard g)
    {
        var s = _ctx.Settings.GetGuard(g.Id);
        bool wanted = _override != null ? _override.Contains(g.Id) : s.Enabled;
        if (!wanted) return false;
        if (g.ChangesSettings && !s.Acknowledged)
        {
            _ctx.Log.Warn(LogCategory.Guard, $"{g.DisplayName} is not acknowledged yet; confirm it once in the status window. Ignored.");
            return false;
        }
        return true;
    }

    public void ApplyAll()
    {
        Running = true;
        foreach (var g in _guards)
            ApplyGuard(g);
        if (Get(GuardId.ExecutionState).Status == GuardStatus.Off)
            _ctx.Log.Warn(LogCategory.Guard, "The keep-awake (power requests) guard is not selected: NoClose is running but NOT asking Windows to stay awake.");
    }

    public void ApplyGuard(GuardId id) => ApplyGuard(Get(id));

    private void ApplyGuard(IGuard g)
    {
        try
        {
            if (!IsSelected(g)) return;
            var avail = g.CheckAvailability(_ctx);
            if (!avail.CanApply)
            {
                _ctx.Log.Info(LogCategory.Guard, $"{g.DisplayName}: {avail.StatusIfNot}: {avail.Reason}");
                SetStatusFromAvailability(g, avail);
                return;
            }
            var r = g.Apply(_ctx);
            _lastReapply[g.Id] = DateTime.UtcNow;
            _ctx.Log.Info(LogCategory.Guard, $"{g.DisplayName}: {r.Status}: {r.Message}");
        }
        catch (Exception ex)
        {
            _ctx.Log.Error(LogCategory.Guard, $"{g.DisplayName} apply failed: {ex}");
        }
        finally { Changed?.Invoke(); }
    }

    private static void SetStatusFromAvailability(IGuard g, GuardAvailability a)
    {
        // Guards expose Status read-only; the manager keeps unavailable states in an overlay.
        (g as GuardBase)?.ForceStatus(a.StatusIfNot, a.Reason);
    }

    /// <summary>Restore in reverse order; each guard in its own try/catch.</summary>
    public void RestoreAll()
    {
        for (int i = _guards.Count - 1; i >= 0; i--)
            RestoreGuard(_guards[i]);
        Running = false;
        _override = null;
        Changed?.Invoke();
    }

    public void RestoreGuard(GuardId id) => RestoreGuard(Get(id));

    private void RestoreGuard(IGuard g)
    {
        try
        {
            bool needs = g.Status is GuardStatus.Applied or GuardStatus.Reverted or GuardStatus.Error or GuardStatus.Degraded
                         || _ctx.Journal.OpenEntries(g.Id).Count > 0;
            if (!needs)
            {
                (g as GuardBase)?.ForceStatus(GuardStatus.Off, "");
                return;
            }
            var r = g.Restore(_ctx);
            _ctx.Log.Info(LogCategory.Guard, $"{g.DisplayName}: restore -> {r.Status}: {r.Message}");
        }
        catch (Exception ex)
        {
            _ctx.Log.Error(LogCategory.Guard, $"{g.DisplayName} restore failed: {ex}");
        }
    }

    /// <summary>Re-checks every applied guard every GuardReapplyMinutes (default 5).</summary>
    public void ReapplyDue()
    {
        if (!Running) return;
        var interval = TimeSpan.FromMinutes(Math.Max(1, _ctx.Settings.GuardReapplyMinutes));
        bool any = false;
        foreach (var g in _guards)
        {
            if (g.Status is GuardStatus.Off or GuardStatus.Unavailable or GuardStatus.BlockedByPolicy or GuardStatus.NeedsElevation) continue;
            if (_lastReapply.TryGetValue(g.Id, out var last) && DateTime.UtcNow - last < interval) continue;
            _lastReapply[g.Id] = DateTime.UtcNow;
            try
            {
                var before = g.Status;
                var r = g.Reapply(_ctx);
                if (r.Status != before) any = true;
            }
            catch (Exception ex)
            {
                _ctx.Log.Error(LogCategory.Guard, $"{g.DisplayName} reapply failed: {ex.Message}");
            }
        }
        if (any) Changed?.Invoke();
    }

    /// <summary>Immediate reapply of every running guard (resume / unlock / settings change).</summary>
    public void ReapplyNow()
    {
        _lastReapply.Clear();
        ReapplyDue();
    }
}
