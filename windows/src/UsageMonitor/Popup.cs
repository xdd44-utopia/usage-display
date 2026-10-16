using System.Drawing.Drawing2D;

namespace UsageMonitor;

class Popup : Form
{
    long hiddenAt;

    public Popup()
    {
        Text = "Usage Monitor";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= Native.WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    protected virtual int ContentPadding => 16;

    /// Deactivation precedes the click that closes the pop-up; that click must not reopen it.
    public bool JustHidden => Environment.TickCount64 - hiddenAt < 300;

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) hiddenAt = Environment.TickCount64;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        Hide();
        return true;
    }

    protected int Px(int logical) => LogicalToDeviceUnits(logical);

    /// Form.Padding only affects docked controls, so inset this content explicitly.
    protected void SetContent(Control content)
    {
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        foreach (var control in old) control.Dispose();
        BackColor = AppTheme.Background;
        ForeColor = AppTheme.Text;
        var padding = Px(ContentPadding);
        content.Location = new Point(padding, padding);
        Controls.Add(content);
        ResumeLayout();
        content.Size = content.GetPreferredSize(Size.Empty);
        ClientSize = new Size(content.Width + 2 * padding, content.Height + 2 * padding);
        Native.RoundCorners(Handle, AppTheme.Line);
    }

    public void ShowNear(Rectangle anchor)
    {
        var area = Screen.FromRectangle(anchor).WorkingArea;
        var gap = Px(8);
        var y = anchor.Bottom + gap + Height <= area.Bottom ? anchor.Bottom + gap : anchor.Top - gap - Height;
        var x = anchor.Left + Width <= area.Right ? anchor.Left : anchor.Right - Width;
        Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)),
                             Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        Show();
        Activate();
    }
}

sealed class TrayMenu : Popup
{
    protected override int ContentPadding => 4;

    /// A null Run shows the entry greyed out.
    public void Fill(IReadOnlyList<(string Text, Action? Run)?> entries)
    {
        var width = Math.Max(Px(180), entries.OfType<(string Text, Action? Run)>()
            .Max(e => TextRenderer.MeasureText(e.Text, Font).Width) + Px(32));
        var list = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
        };
        foreach (var entry in entries)
        {
            if (entry is not var (text, run))
            {
                list.Controls.Add(new Panel
                {
                    Size = new Size(width - Px(16), 1), BackColor = AppTheme.Line,
                    Margin = new Padding(Px(8), Px(4), Px(8), Px(4)),
                });
                continue;
            }
            var row = new MenuRow { Text = text, Size = new Size(width, Px(30)), Margin = Padding.Empty, Enabled = run is not null };
            row.Click += (_, _) =>
            {
                Hide();
                if (run is not null) BeginInvoke(run);
            };
            list.Controls.Add(row);
        }
        SetContent(list);
    }
}

sealed class MenuRow : Control
{
    bool hovered;

    public MenuRow()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(AppTheme.Background);
        if (hovered)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(4));
            using var brush = new SolidBrush(AppTheme.Hover);
            graphics.FillPath(brush, path);
        }
        TextRenderer.DrawText(graphics, Text, Font, new Rectangle(LogicalToDeviceUnits(12), 0, Width, Height),
            Enabled ? AppTheme.Text : AppTheme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
