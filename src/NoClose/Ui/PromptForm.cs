namespace NoClose.Ui;

/// <summary>Tiny modal "enter a value" dialog.</summary>
internal sealed class PromptForm : Form
{
    private readonly TextBox _text = new() { Dock = DockStyle.Top };

    private PromptForm(string title, string prompt, string defaultText)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(380, 130);
        Padding = new Padding(12);

        var label = new Label { Text = prompt, Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(350, 0) };
        _text.Text = defaultText;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 36 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(_text);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        Controls.Add(label);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Returns the entered text or null if cancelled.</summary>
    public static string? Ask(IWin32Window? owner, string title, string prompt, string defaultText = "")
    {
        using var f = new PromptForm(title, prompt, defaultText);
        var r = owner != null ? f.ShowDialog(owner) : f.ShowDialog();
        return r == DialogResult.OK ? f.Result : null;
    }

    private string Result => _text.Text.Trim();
}
