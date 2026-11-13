using NoClose.Core.Time;
using NoClose.Core.Activation;
using NoClose.Core.Cli;
using NoClose.Core.Guards;
using NoClose.Engine;
using NoClose.Properties;

namespace NoClose.Ui;

/// <summary>Tray icon, tooltip and context menu.</summary>
internal sealed class TrayController : ApplicationContext
{
    private readonly AppHost _host;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _startMenu = new("Start");
    private readonly ToolStripMenuItem _stopItem = new("Stop");
    private readonly ToolStripMenuItem _logonItem = new("Start at logon") { CheckOnClick = false };
    private readonly ToolStripMenuItem _elevateItem = new("Restart elevated");
    private readonly System.Windows.Forms.Timer _clickTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private string _lastIconName = "";

    public Notifications Notify { get; }

    public TrayController(AppHost host)
    {
        _host = host;
        _icon = new NotifyIcon { Visible = false, ContextMenuStrip = _menu, Text = "NoClose" };
        Notify = new Notifications(_icon, host.Clock);

        BuildMenu();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) { _clickTimer.Stop(); _clickTimer.Start(); }
        };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); _host.ToggleStatusWindow(); };
        _icon.DoubleClick += (_, _) =>
        {
            _clickTimer.Stop();
            if (_host.IsActive) _host.Deactivate("stopped from the tray icon");
            else _host.Activate(ActivationSpec.Indefinite, null, null, out _);
        };
        _menu.Opening += (_, _) => UpdateMenu();
        _host.ViewChanged += Refresh;
        Refresh();
        _icon.Visible = true;

        _ = Task.Run(() =>
        {
            bool exists = _host.Startup.Exists();
            _host.PostUi(() => { _host.Settings.StartAtLogon = exists; });
        });
    }

    private void BuildMenu()
    {
        void Add(string text, ActivationSpec spec) =>
            _startMenu.DropDownItems.Add(text, null, (_, _) => StartWith(spec));
        Add("Indefinitely", ActivationSpec.Indefinite);
        _startMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var h in new[] { 1, 2, 4, 8, 24 })
            Add($"For {h} h", ActivationSpec.ForDuration(TimeSpan.FromHours(h)));
        _startMenu.DropDownItems.Add(new ToolStripSeparator());
        _startMenu.DropDownItems.Add("Custom...", null, (_, _) => StartCustomDuration());
        _startMenu.DropDownItems.Add("Until...", null, (_, _) => StartUntil());
        _startMenu.DropDownItems.Add("While process...", null, (_, _) => StartWhileProcess());

        _stopItem.Click += (_, _) => _host.Deactivate("stopped from the tray menu");
        _logonItem.Click += (_, _) => ToggleLogon();
        _elevateItem.Image = SystemIcons.Shield.ToBitmap();
        _elevateItem.Click += (_, _) => _host.RestartElevated();

        _menu.Items.Add(_startMenu);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Status window", null, (_, _) => _host.ShowStatusWindow());
        _menu.Items.Add("Diagnostics...", null, (_, _) => _host.ShowDiagnostics());
        _menu.Items.Add("Open log folder", null, (_, _) => _host.OpenLogFolder());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_elevateItem);
        _menu.Items.Add(_logonItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => _host.Exit());
    }

    private void UpdateMenu()
    {
        _stopItem.Enabled = _host.IsActive;
        _elevateItem.Visible = _host.CanRestartElevated;
        _logonItem.Checked = _host.Settings.StartAtLogon;
    }

    private void StartWith(ActivationSpec spec)
    {
        if (!_host.Activate(spec, null, null, out var msg))
            MessageBox.Show(msg, "NoClose", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void StartCustomDuration()
    {
        var text = PromptForm.Ask(null, "Start for a duration", "Duration (e.g. 90m, 8h, 1h30m, 2d, 01:30):", "8h");
        if (text == null) return;
        if (!DurationParser.TryParse(text, out var d, out var err)) { MessageBox.Show(err, "NoClose", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        StartWith(ActivationSpec.ForDuration(d));
    }

    private void StartUntil()
    {
        var text = PromptForm.Ask(null, "Start until", "End time (HH:mm, or yyyy-MM-ddTHH:mm):", "18:00");
        if (text == null) return;
        if (!UntilParser.TryParse(text, _host.Clock.LocalNow(), out var t, _host.Clock.TimeZone))
        { MessageBox.Show("Enter HH:mm or yyyy-MM-ddTHH:mm (in the future, within 30 days).", "NoClose", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        StartWith(ActivationSpec.ForUntil(t));
    }

    private void StartWhileProcess()
    {
        var text = PromptForm.Ask(null, "Start while a process runs", "Process image name (e.g. build.exe) or PID:");
        if (string.IsNullOrWhiteSpace(text)) return;
        StartWith(ActivationSpec.ParseProcess(text));
    }

    private void ToggleLogon()
    {
        bool want = !_host.Settings.StartAtLogon;
        using var _ = new WaitCursor();
        bool ok = want ? _host.Startup.Create() : _host.Startup.Delete();
        if (ok) { _host.Settings.StartAtLogon = want; _host.SaveSettings(); }
        else Notify.Show("NoClose", "Could not " + (want ? "create" : "remove") + " the start-at-logon task (administrator approval is needed).", ToolTipIcon.Warning, throttle: false);
    }

    private sealed class WaitCursor : IDisposable
    {
        private readonly Cursor _old = Cursor.Current ?? Cursors.Default;
        public WaitCursor() => Cursor.Current = Cursors.WaitCursor;
        public void Dispose() => Cursor.Current = _old;
    }

    public void Refresh()
    {
        var es = _host.EngineStatus;
        bool warn = _host.HasHighFinding || _host.Overall is OverallState.Degraded or OverallState.NotKeepingAwake;
        var name = warn ? "noclose-warn" : _host.IsActive ? "noclose-active" : "noclose-idle";
        if (name != _lastIconName)
        {
            _icon.Icon = Icons.Load(name);
            _lastIconName = name;
        }
        UpdateTooltip();
    }

    public void UpdateTooltip()
    {
        string text;
        if (!_host.IsActive) text = "NoClose — Idle";
        else
        {
            var rem = _host.Policy.Remaining(DateTime.UtcNow);
            var degraded = StatusSummary.Label(_host.Overall);
            text = rem is { } r ? $"NoClose — {degraded} ({ActivationSpec.FormatSpan(r)} left)" : $"NoClose — {degraded}";
        }
        if (text.Length > 127) text = text[..127];
        if (_icon.Text != text) _icon.Text = text;
    }

    public void DisposeIcon()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _clickTimer.Dispose();
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        base.ExitThreadCore();
    }
}
