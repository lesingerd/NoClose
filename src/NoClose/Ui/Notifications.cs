using NoClose.Core.Time;

namespace NoClose.Ui;

/// <summary>Balloon tips; identical messages are shown at most once per hour.</summary>
internal sealed class Notifications
{
    private readonly NotifyIcon _icon;
    private readonly Throttle _throttle;

    public Notifications(NotifyIcon icon, IClock clock)
    {
        _icon = icon;
        _throttle = new Throttle(TimeSpan.FromHours(1), clock);
    }

    public void Show(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, bool throttle = true)
    {
        if (throttle && !_throttle.TryPass(title + "|" + text)) return;
        try
        {
            _icon.ShowBalloonTip(8000, title, text.Length > 250 ? text[..250] + "..." : text, icon);
        }
        catch (Exception) { /* a missing notification area must not break the app */ }
    }
}
