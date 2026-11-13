using System.Text;
using NoClose.Core.Diagnostics;

namespace NoClose.Ui;

/// <summary>Read-only diagnostics list; the scan runs on a background task.</summary>
internal sealed class DiagnosticsForm : Form
{
    private readonly AppHost _host;
    private readonly ListView _list = new();
    private readonly TextBox _detail = new();
    private readonly Label _status = new();
    private readonly Button _refresh = new();
    private IReadOnlyList<DiagnosticFinding> _findings = Array.Empty<DiagnosticFinding>();
    private string _reportText = "";

    public DiagnosticsForm(AppHost host)
    {
        _host = host;
        Text = "NoClose diagnostics";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 560);
        Icon = Properties.Icons.Load("noclose-idle");

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Columns.Add("Severity", 80);
        _list.Columns.Add("Rule", 120);
        _list.Columns.Add("Title", 360);
        _list.Columns.Add("Addressed by", 150);
        _list.Columns.Add("Organisation setting", 120);
        _list.SelectedIndexChanged += (_, _) => ShowDetail();

        _detail.Dock = DockStyle.Fill;
        _detail.Multiline = true;
        _detail.ReadOnly = true;
        _detail.ScrollBars = ScrollBars.Vertical;
        _detail.BackColor = SystemColors.Window;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 6, 8, 6) };
        _refresh.Text = "Refresh";
        _refresh.AutoSize = true;
        _refresh.Click += async (_, _) => await RefreshAsync();
        var copy = new Button { Text = "Copy report", AutoSize = true };
        copy.Click += (_, _) => { try { Clipboard.SetText(_reportText); } catch (Exception) { } };
        var save = new Button { Text = "Save report...", AutoSize = true };
        save.Click += (_, _) => SaveReport();
        _status.AutoSize = true;
        _status.Margin = new Padding(12, 8, 0, 0);
        buttons.Controls.AddRange(new Control[] { _refresh, copy, save, _status });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_detail);
        Controls.Add(split);
        Controls.Add(buttons);

        Shown += async (_, _) => await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _refresh.Enabled = false;
        _status.Text = "Scanning (read-only)...";
        try
        {
            var (findings, text) = await _host.RunDiagnosticsAsync();
            if (IsDisposed) return;
            _findings = findings;
            _reportText = text;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var f in findings)
            {
                var item = new ListViewItem(f.Severity.ToString().ToUpperInvariant()) { Tag = f };
                item.SubItems.Add(f.RuleId);
                item.SubItems.Add(f.Title);
                item.SubItems.Add(f.AddressedBy is { } g ? DiagnosticsTextFormatter.GuardName(g) : "—");
                item.SubItems.Add(f.OrganisationControlled ? "yes" : "no");
                item.ForeColor = f.Severity switch
                {
                    Severity.High => Color.Firebrick,
                    Severity.Medium => Color.DarkOrange,
                    Severity.Info => SystemColors.GrayText,
                    _ => SystemColors.WindowText
                };
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            _status.Text = $"{findings.Count} finding(s) at {DateTime.Now:HH:mm:ss}";
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            else _detail.Text = "No findings.";
        }
        catch (Exception ex)
        {
            _status.Text = "Scan failed: " + ex.Message;
        }
        finally { if (!IsDisposed) _refresh.Enabled = true; }
    }

    private void ShowDetail()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not DiagnosticFinding f) return;
        var sb = new StringBuilder();
        sb.AppendLine(f.Title).AppendLine().AppendLine(f.Detail).AppendLine();
        if (f.PolicyName != null) sb.AppendLine("Policy: " + f.PolicyName).AppendLine();
        sb.AppendLine("Advice: " + f.Advice);
        _detail.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private void SaveReport()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt", FileName = "noclose-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dlg.FileName, _reportText, new UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "NoClose", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
