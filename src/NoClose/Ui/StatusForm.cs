using NoClose.Core.Time;
using System.ComponentModel;
using System.Runtime.InteropServices;
using NoClose.Core.Activation;
using NoClose.Core.Cli;
using NoClose.Core.Guards;
using NoClose.Core.Text;
using NoClose.Engine;
using NoClose.Guards;
using NoClose.Logging;
using NoClose.Platform.Interop;

namespace NoClose.Ui;

/// <summary>
/// The status window. It is always created (it owns the window handle used for session/power notifications and for the
/// shutdown block reason) and is never made invisible while the end-session guard is applied (minimized instead).
/// </summary>
internal sealed class StatusForm : Form
{
    private readonly AppHost _host;
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 300 };
    private readonly Label _lblState = new();
    private readonly Label _lblActivation = new();
    private readonly Label _lblCountdown = new();
    private readonly TextBox _txtReason = new();
    private readonly ComboBox _cmbMode = new();
    private readonly TextBox _txtParam = new();
    private readonly Button _btnStartStop = new();
    private readonly Panel _warnPanel = new();
    private readonly Label _lblWarn = new();
    private readonly LinkLabel _lnkDetails = new();
    private readonly LinkLabel _lnkElevate = new();
    private readonly LinkLabel _lnkLog = new();
    private readonly Label _lblElev = new();
    private readonly Dictionary<GuardId, GuardRow> _rows = new();
    private readonly List<IntPtr> _powerHandles = new();
    private IntPtr _resumeHandle;
    private bool _updating;
    private bool _firstShowDone;

    private sealed class GuardRow
    {
        public required CheckBox Check { get; init; }
        public required Label Chip { get; init; }
        public Button? Extra { get; set; }
    }

    private static readonly Guid[] PowerNotifications =
    {
        NativeConstants.GUID_CONSOLE_DISPLAY_STATE, NativeConstants.GUID_SESSION_DISPLAY_STATUS, NativeConstants.GUID_SESSION_USER_PRESENCE,
        NativeConstants.GUID_ACDC_POWER_SOURCE, NativeConstants.GUID_SYSTEM_AWAYMODE, NativeConstants.GUID_LIDSWITCH_STATE_CHANGE,
    };

    public StatusForm(AppHost host)
    {
        _host = host;
        Text = "NoClose";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 520);
        Icon = Properties.Icons.Load("noclose-active");
        BuildLayout();
        host.ViewChanged += () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(RefreshView)); };
        RefreshView();
    }

    // ---- layout ---------------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12), AutoScroll = true,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 1. header
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _lblState.Font = new Font(Font.FontFamily, 20f, FontStyle.Bold);
        _lblState.AutoSize = true;
        _lblActivation.AutoSize = true;
        _lblActivation.Margin = new Padding(12, 14, 0, 0);
        _lblCountdown.AutoSize = true;
        header.Controls.Add(_lblState, 0, 0);
        header.Controls.Add(_lblActivation, 1, 0);
        header.Controls.Add(_lblCountdown, 0, 1);
        header.SetColumnSpan(_lblCountdown, 2);
        var reasonRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        reasonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        reasonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        reasonRow.Controls.Add(new Label { Text = "Reason:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        _txtReason.Dock = DockStyle.Fill;
        _txtReason.MaxLength = ReasonText.MaxLength;
        reasonRow.Controls.Add(_txtReason, 1, 0);
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        top.Controls.Add(header);
        top.Controls.Add(reasonRow);
        root.Controls.Add(top);

        // 2. start/stop + mode
        var modeRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 8, 0, 0) };
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMode.Items.AddRange(new object[] { "Indefinite", "Duration", "Until", "While process" });
        _cmbMode.SelectedIndex = 0;
        _cmbMode.SelectedIndexChanged += (_, _) => UpdateModeHint();
        _txtParam.Dock = DockStyle.Fill;
        _btnStartStop.AutoSize = true;
        _btnStartStop.MinimumSize = new Size(80, 0);
        _btnStartStop.Click += (_, _) => OnStartStop();
        modeRow.Controls.Add(_cmbMode, 0, 0);
        modeRow.Controls.Add(_txtParam, 1, 0);
        modeRow.Controls.Add(_btnStartStop, 2, 0);
        root.Controls.Add(modeRow);

        // 3. warnings
        _warnPanel.BackColor = Color.FromArgb(255, 236, 179);
        _warnPanel.Dock = DockStyle.Fill;
        _warnPanel.AutoSize = true;
        _warnPanel.Padding = new Padding(8);
        _warnPanel.Margin = new Padding(0, 8, 0, 0);
        _lblWarn.AutoSize = true;
        _lblWarn.MaximumSize = new Size(370, 0);
        _lblWarn.ForeColor = Color.FromArgb(70, 45, 0);
        _lnkDetails.Text = "Details...";
        _lnkDetails.AutoSize = true;
        _lnkDetails.Dock = DockStyle.Bottom;
        _lnkDetails.LinkClicked += (_, _) => _host.ShowDiagnostics();
        _warnPanel.Controls.Add(_lblWarn);
        _warnPanel.Controls.Add(_lnkDetails);
        root.Controls.Add(_warnPanel);

        // 4. guards
        var guards = new GroupBox { Text = "Guards", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), Margin = new Padding(0, 8, 0, 0) };
        var list = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;
        foreach (var g in _host.Guards.Guards)
        {
            var cb = new CheckBox { Text = g.DisplayName, AutoSize = true, Anchor = AnchorStyles.Left };
            var chip = new Label { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(6, 6, 6, 0), ForeColor = SystemColors.GrayText };
            var help = new Button { Text = "?", Width = 26, Height = 24, Margin = new Padding(0) };
            var captured = g;
            help.Click += (_, _) => MessageBox.Show(this, captured.Description + (captured.IsAdminRequired ? Environment.NewLine + Environment.NewLine + "Requires administrator rights." : ""),
                captured.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            cb.CheckedChanged += (_, _) => OnGuardToggled(captured, cb);
            _tips.SetToolTip(chip, g.Description);
            list.Controls.Add(cb, 0, row);
            list.Controls.Add(chip, 1, row);
            list.Controls.Add(help, 2, row);
            var r = new GuardRow { Check = cb, Chip = chip };
            if (g.Id == GuardId.PowerPlan)
            {
                var items = new Button { Text = "Items...", AutoSize = true, Margin = new Padding(2, 0, 0, 0) };
                items.Click += (_, _) => EditPowerItems();
                list.Controls.Add(items, 3, row);
                r.Extra = items;
            }
            else if (g.Id == GuardId.ActiveHours)
            {
                var shift = new Button { Text = "Shift forward", AutoSize = true, Margin = new Padding(2, 0, 0, 0), Visible = false };
                shift.Click += (_, _) => { _host.Guards.Get<ActiveHoursGuard>().ShiftForward(_host.GuardContext); _host.RaiseViewChanged(); };
                list.Controls.Add(shift, 3, row);
                r.Extra = shift;
            }
            _rows[g.Id] = r;
            row++;
        }
        guards.Controls.Add(list);
        root.Controls.Add(guards);

        // 5. footer
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0), WrapContents = true };
        _lnkElevate.Text = "Restart elevated";
        _lnkElevate.AutoSize = true;
        _lnkElevate.LinkClicked += (_, _) => _host.RestartElevated();
        _lnkLog.Text = "Open log";
        _lnkLog.AutoSize = true;
        _lnkLog.Margin = new Padding(12, 0, 0, 0);
        _lnkLog.LinkClicked += (_, _) => _host.OpenLogFolder();
        _lblElev.AutoSize = true;
        _lblElev.Margin = new Padding(12, 0, 0, 0);
        _lblElev.ForeColor = SystemColors.GrayText;
        var shield = new PictureBox { Size = new Size(16, 16), SizeMode = PictureBoxSizeMode.StretchImage, Image = SystemIcons.Shield.ToBitmap(), Margin = new Padding(0, 0, 4, 0) };
        shield.Visible = _host.CanRestartElevated;
        _lnkElevate.Visible = _host.CanRestartElevated;
        if (_host.CanRestartElevated) footer.Controls.Add(shield);
        footer.Controls.Add(_lnkElevate);
        footer.Controls.Add(_lnkLog);
        footer.Controls.Add(_lblElev);
        root.Controls.Add(footer);

        Controls.Add(root);
        UpdateModeHint();
    }

    private void UpdateModeHint()
    {
        _txtParam.Enabled = _cmbMode.SelectedIndex != 0 && !_host.IsActive;
        _tips.SetToolTip(_txtParam, _cmbMode.SelectedIndex switch
        {
            1 => "Duration, e.g. 8h, 90m, 1h30m, 2d, 01:30",
            2 => "End time, e.g. 18:00 or 2026-11-13T08:00",
            3 => "Process image name (build.exe) or PID",
            _ => "No parameter needed"
        });
        if (_cmbMode.SelectedIndex == 0) _txtParam.Text = "";
    }

    // ---- actions --------------------------------------------------------------------------------------------

    private void OnStartStop()
    {
        if (_host.IsActive)
        {
            _host.Deactivate("stopped by user");
            return;
        }
        if (!TryBuildSpec(out var spec, out var error))
        {
            MessageBox.Show(this, error, "NoClose", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_host.Activate(spec, _txtReason.Text, null, out var msg))
            MessageBox.Show(this, msg, "NoClose", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private bool TryBuildSpec(out ActivationSpec spec, out string error)
    {
        spec = ActivationSpec.Indefinite;
        error = "";
        var p = _txtParam.Text.Trim();
        switch (_cmbMode.SelectedIndex)
        {
            case 1:
                if (!DurationParser.TryParse(p, out var d, out error)) return false;
                spec = ActivationSpec.ForDuration(d);
                return true;
            case 2:
                if (!UntilParser.TryParse(p, _host.Clock.LocalNow(), out var t, _host.Clock.TimeZone))
                { error = "Enter HH:mm or yyyy-MM-ddTHH:mm (in the future, within 30 days)."; return false; }
                spec = ActivationSpec.ForUntil(t);
                return true;
            case 3:
                if (p.Length == 0) { error = "Enter a process name or PID."; return false; }
                spec = ActivationSpec.ParseProcess(p);
                return true;
            default:
                return true;
        }
    }

    private void OnGuardToggled(IGuard g, CheckBox cb)
    {
        if (_updating) return;
        var gs = _host.Settings.GetGuard(g.Id);
        if (cb.Checked)
        {
            if (g.ChangesSettings && !gs.Acknowledged)
            {
                var text = g.ConfirmationText;
                if (g.Id == GuardId.PowerPlan)
                    text += Environment.NewLine + Environment.NewLine + "Selected items:" + Environment.NewLine +
                            string.Join(Environment.NewLine, PowerPlanItems.All.Where(i => gs.Items != null && gs.Items.TryGetValue(i.Key, out var on) && on).Select(i => " - " + i.DisplayName));
                var r = MessageBox.Show(this, text, "Enable \"" + g.DisplayName + "\"?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK)
                {
                    _updating = true; cb.Checked = false; _updating = false;
                    return;
                }
                gs.Acknowledged = true;
            }
            gs.Enabled = true;
            _host.SaveSettings();
            if (_host.IsActive) _host.Guards.ApplyGuard(g.Id);
        }
        else
        {
            gs.Enabled = false;
            _host.SaveSettings();
            if (_host.IsActive) _host.Guards.RestoreGuard(g.Id);
        }
        RefreshView();
    }

    private void EditPowerItems()
    {
        var gs = _host.Settings.GetGuard(GuardId.PowerPlan);
        gs.Items ??= new Dictionary<string, bool>();
        using var dlg = new Form
        {
            Text = "Power plan items", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(400, 300),
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96),
        };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
        var boxes = new List<(PowerPlanItem Item, CheckBox Box)>();
        foreach (var item in PowerPlanItems.All)
        {
            var on = gs.Items.TryGetValue(item.Key, out var v) ? v : item.DefaultOn;
            var cb = new CheckBox { Text = item.DisplayName, Checked = on, AutoSize = true };
            boxes.Add((item, cb));
            flow.Controls.Add(cb);
        }
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        dlg.Controls.Add(flow);
        dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var (item, box) in boxes) gs.Items[item.Key] = box.Checked;
        _host.SaveSettings();
    }

    // ---- view -----------------------------------------------------------------------------------------------

    public void RefreshView()
    {
        if (IsDisposed) return;
        _updating = true;
        try
        {
            var es = _host.EngineStatus;
            bool active = _host.IsActive;
            var overall = _host.Overall;
            _lblState.Text = StatusSummary.Label(overall);
            _lblState.ForeColor = overall switch
            {
                OverallState.Idle => SystemColors.GrayText,
                OverallState.Active => Color.ForestGreen,
                _ => Color.DarkOrange
            };
            _lblActivation.Text = active ? _host.Policy.Spec.Describe() : "";
            UpdateCountdown();
            _txtReason.ReadOnly = active;
            if (!_txtReason.Focused || !active) _txtReason.Text = active ? _host.Reason : (_txtReason.Text.Length == 0 ? _host.Reason : _txtReason.Text);
            _cmbMode.Enabled = !active;
            _txtParam.Enabled = !active && _cmbMode.SelectedIndex != 0;
            _btnStartStop.Text = active ? "Stop" : "Start";

            var lines = new List<string>();
            if (_host.Banner != null) lines.Add(_host.Banner);
            if (es.State == EngineState.Degraded && es.DegradedReason != null) lines.Add("Keep-awake degraded: " + es.DegradedReason);
            if (overall == OverallState.NotKeepingAwake) lines.Add("NoClose is running but is NOT asking Windows to stay awake (the keep-awake power requests are off).");
            foreach (var f in _host.Findings.Where(f => f.Severity == Core.Diagnostics.Severity.High))
                lines.Add(f.Title);
            _warnPanel.Visible = lines.Count > 0;
            _lblWarn.Text = string.Join(Environment.NewLine, lines.Select(l => "⚠ " + l));

            foreach (var g in _host.Guards.Guards)
            {
                var row = _rows[g.Id];
                var gs = _host.Settings.GetGuard(g.Id);
                bool adminBlocked = g.IsAdminRequired && !_host.IsElevated;
                row.Check.Checked = gs.Enabled && !(g.ChangesSettings && !gs.Acknowledged);
                row.Check.Enabled = !adminBlocked;
                _tips.SetToolTip(row.Check, adminBlocked ? (_host.CanRestartElevated ? "Requires administrator — use Restart elevated" : "Requires an administrator account") : g.Description);
                var chip = adminBlocked && !active ? new GuardState(g.Id, GuardStatus.NeedsElevation, "").ChipText
                    : new GuardState(g.Id, g.Status, g.Message).ChipText;
                row.Chip.Text = chip;
                _tips.SetToolTip(row.Chip, string.IsNullOrEmpty(g.Message) ? g.Description : g.Message);
                row.Chip.ForeColor = g.Status switch
                {
                    GuardStatus.Applied => Color.ForestGreen,
                    GuardStatus.Error or GuardStatus.Reverted => Color.Firebrick,
                    GuardStatus.Degraded => Color.DarkOrange,
                    GuardStatus.BlockedByPolicy or GuardStatus.NeedsElevation => Color.DarkOrange,
                    _ => SystemColors.GrayText
                };
                if (g.Id == GuardId.PowerPlan && row.Extra != null) row.Extra.Enabled = !adminBlocked && !(active && g.Status == GuardStatus.Applied);
                if (g.Id == GuardId.ActiveHours && row.Extra != null)
                    row.Extra.Visible = _host.Guards.Get<ActiveHoursGuard>().ShiftSuggested;
            }
            _lblElev.Text = _host.IsElevated ? "Running as administrator" : _host.CanRestartElevated ? "Not elevated" : "Standard user (administrator-only guards unavailable)";
            _btnStartStop.Enabled = true;
        }
        finally { _updating = false; }
    }

    public void UpdateCountdown()
    {
        if (IsDisposed) return;
        var rem = _host.Policy.Remaining(DateTime.UtcNow);
        if (!_host.IsActive) _lblCountdown.Text = "";
        else if (rem is { } r) _lblCountdown.Text = "Time left: " + ActivationSpec.FormatSpan(r);
        else _lblCountdown.Text = "Running for " + ActivationSpec.FormatSpan(DateTime.UtcNow - _host.Policy.StartedUtc);
    }

    // ---- visibility -------------------------------------------------------------------------------------------

    public void ShowMinimizedFirst()
    {
        RestoreBoundsIfAny();
        WindowState = FormWindowState.Minimized;
        Show();
        _firstShowDone = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_firstShowDone) { RestoreBoundsIfAny(); _firstShowDone = true; }
    }

    private void RestoreBoundsIfAny()
    {
        if (_host.Settings.LastWindowBounds is not { } b) return;
        var rect = new Rectangle(b.X, b.Y, ClientSize.Width, 40);
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect)))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(b.X, b.Y);
        }
    }

    /// <summary>Shown minimized (never hidden) so the shutdown block reason has a visible owner window.</summary>
    public void EnsureVisibleMinimized()
    {
        if (IsDisposed) return;
        if (!Visible) { WindowState = FormWindowState.Minimized; Show(); }
    }

    public void BringToFrontRestored()
    {
        if (IsDisposed) return;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    /// <summary>Close button / tray toggle: minimize to the taskbar if the end-session guard needs a visible window, else hide.</summary>
    public void CloseToTray()
    {
        if (_host.EndSession.IsApplied) WindowState = FormWindowState.Minimized;
        else Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_host.Exiting)
        {
            e.Cancel = true;
            CloseToTray();
            return;
        }
        if (e.CloseReason == CloseReason.WindowsShutDown) _host.EmergencyRestore(); // safety net; WndProc normally ran first
        base.OnFormClosing(e);
    }

    // ---- notifications ------------------------------------------------------------------------------------------

    private bool _wtsRegistered;
    private bool _wtsRetryable;
    private bool _wtsFailureLogged;

    /// <summary>
    /// WTSRegisterSessionNotification can fail with RPC_S_INVALID_BINDING when Remote Desktop Services is not ready yet (a logon task starts
    /// early). It is retried from the host's 1 second UI timer until it succeeds; only a successful registration is unregistered.
    /// </summary>
    private void TryRegisterWts()
    {
        if (_wtsRegistered || !IsHandleCreated) return;
        if (Wtsapi32.WTSRegisterSessionNotification(Handle, Wtsapi32.NOTIFY_FOR_THIS_SESSION))
        {
            _wtsRegistered = true;
            _wtsRetryable = false;
            if (_wtsFailureLogged) _host.Log.Info(LogCategory.Session, "WTSRegisterSessionNotification succeeded after a retry");
            return;
        }
        int err = Marshal.GetLastPInvokeError();
        _wtsRetryable = err == (int)NativeConstants.ERROR_INVALID_BINDING_RPC;
        if (!_wtsFailureLogged)
        {
            _wtsFailureLogged = true;
            _host.Log.Warn(LogCategory.Session,
                $"WTSRegisterSessionNotification failed (error {err}: {new Win32Exception(err).Message}){(_wtsRetryable ? "; will retry until Remote Desktop Services is ready" : "")}");
        }
    }

    /// <summary>Called every second by the host: retries the session notification registration while it is retryable.</summary>
    public void RetryRegistrations()
    {
        if (!_wtsRegistered && _wtsRetryable) TryRegisterWts();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryRegisterWts();
        foreach (var g in PowerNotifications)
        {
            var h = User32.RegisterPowerSettingNotification(Handle, in g, NativeConstants.DEVICE_NOTIFY_WINDOW_HANDLE);
            if (h != IntPtr.Zero) _powerHandles.Add(h);
            else _host.Log.Warn(LogCategory.Power, $"RegisterPowerSettingNotification {g} failed, error {Marshal.GetLastPInvokeError()}");
        }
        _resumeHandle = User32.RegisterSuspendResumeNotification(Handle, NativeConstants.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_resumeHandle == IntPtr.Zero) _host.Log.Warn(LogCategory.Power, $"RegisterSuspendResumeNotification failed, error {Marshal.GetLastPInvokeError()}");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        try
        {
            if (_wtsRegistered) { Wtsapi32.WTSUnRegisterSessionNotification(Handle); _wtsRegistered = false; }
            foreach (var h in _powerHandles) User32.UnregisterPowerSettingNotification(h);
            _powerHandles.Clear();
            if (_resumeHandle != IntPtr.Zero) User32.UnregisterSuspendResumeNotification(_resumeHandle);
            _resumeHandle = IntPtr.Zero;
        }
        catch (Exception ex) { _host.Log.Warn(LogCategory.Session, "Unregistering notifications failed: " + ex.Message); }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeConstants.WM_WTSSESSION_CHANGE:
                _host.OnSessionChange(m.WParam.ToInt32(), m.LParam.ToInt32());
                break;

            case NativeConstants.WM_POWERBROADCAST:
                _host.OnPowerBroadcast(unchecked((int)(m.WParam.ToInt64() & 0xFFFFFFFF)), m.LParam);
                m.Result = (IntPtr)1; // TRUE
                return;

            case NativeConstants.WM_SETTINGCHANGE:
                _host.OnSettingChange(m.LParam != IntPtr.Zero ? SafeString(m.LParam) : null);
                break;

            case NativeConstants.WM_QUERYENDSESSION:
                {
                    long flags = m.LParam.ToInt64() & 0xFFFFFFFFL;
                    bool allow = _host.OnQueryEndSession(flags);
                    m.Result = allow ? (IntPtr)1 : IntPtr.Zero;
                    return; // never show dialogs here and never call base
                }

            case NativeConstants.WM_ENDSESSION:
                _host.OnEndSession(m.WParam != IntPtr.Zero, m.LParam.ToInt64() & 0xFFFFFFFFL);
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    private static string? SafeString(IntPtr p)
    {
        try { return Marshal.PtrToStringUni(p); } catch { return null; }
    }
}
