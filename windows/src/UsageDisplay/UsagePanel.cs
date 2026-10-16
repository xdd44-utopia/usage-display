using Microsoft.Win32;

namespace UsageDisplay;

abstract record PanelItem;

sealed record PanelHeader(string Text) : PanelItem;

sealed record PanelLimit(string Label, double Remaining, string? Reset) : PanelItem;

sealed record PanelNote(string Text) : PanelItem;

sealed record PanelGap : PanelItem;

/// A null Run shows the action greyed out.
sealed record PanelAction(string Text, Action? Run, bool? Checked = null, bool IsLink = false) : PanelItem;

static class AppTheme
{
    static bool Light =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is not 0;

    public static Color Background => Light ? Color.FromArgb(0xF9, 0xF9, 0xF9) : Color.FromArgb(0x20, 0x20, 0x20);
    public static Color Text => Light ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.White;
    public static Color Dim => Light ? Color.FromArgb(0x5F, 0x5F, 0x5F) : Color.FromArgb(0xA8, 0xA8, 0xA8);
    public static Color Line => Light ? Color.FromArgb(0xE0, 0xE0, 0xE0) : Color.FromArgb(0x3A, 0x3A, 0x3A);
    public static Color Control => Light ? Color.White : Color.FromArgb(0x2D, 0x2D, 0x2D);
    public static Color Hover => Light ? Color.FromArgb(0xEA, 0xEA, 0xEA) : Color.FromArgb(0x33, 0x33, 0x33);
    public static Color Link => Light ? Color.FromArgb(0x00, 0x5F, 0xB8) : Color.FromArgb(0x60, 0xCD, 0xFF);
}

sealed class UsagePanel : Popup
{
    const int ColumnWidth = 280;
    readonly Font headerFont = new("Segoe UI Semibold", 11f);
    readonly Font boldFont = new("Segoe UI Semibold", 9f);

    public void Fill(IReadOnlyList<IReadOnlyList<PanelItem>> sections, IReadOnlyList<PanelItem> footer)
    {
        var root = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty,
        };
        var columns = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, RowCount = 1, Margin = Padding.Empty,
            ColumnCount = sections.Count * 2 - 1,
        };
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0) columns.Controls.Add(Rule(new Padding(Px(16), 0, Px(16), 0)));
            columns.Controls.Add(Column(sections[i]));
        }
        root.Controls.Add(columns);
        root.Controls.Add(Rule(new Padding(0, Px(12), 0, Px(12))));
        root.Controls.Add(Footer(footer));
        SetContent(root);
    }

    Control Column(IReadOnlyList<PanelItem> items)
    {
        var column = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
            MinimumSize = new Size(Px(ColumnWidth), 0),
        };
        foreach (var item in items) column.Controls.Add(ItemControl(item));
        return column;
    }

    Control Footer(IReadOnlyList<PanelItem> items)
    {
        var footer = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var toggles = Flow(FlowDirection.LeftToRight);
        var buttons = Flow(FlowDirection.LeftToRight);
        buttons.Margin = new Padding(Px(24), 0, 0, 0);
        foreach (var item in items.OfType<PanelAction>())
            (item.Checked is null ? buttons : toggles).Controls.Add(ItemControl(item));
        footer.Controls.Add(toggles, 0, 0);
        footer.Controls.Add(buttons, 1, 0);
        return footer;

        static FlowLayoutPanel Flow(FlowDirection direction) => new()
        {
            FlowDirection = direction, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Anchor = AnchorStyles.Left,
        };
    }

    Control ItemControl(PanelItem item) => item switch
    {
        PanelHeader header => new Label
        {
            Text = header.Text, AutoSize = true, Font = headerFont, Margin = new Padding(0, 0, 0, Px(4)),
        },
        PanelLimit limit => new LimitView(limit, Font, boldFont)
        {
            Width = Px(ColumnWidth), Margin = new Padding(0, Px(6), 0, Px(6)),
        },
        PanelNote note => new Label
        {
            Text = note.Text, AutoSize = true, ForeColor = AppTheme.Dim,
            MaximumSize = new Size(Px(ColumnWidth), 0), Margin = new Padding(0, Px(2), 0, Px(2)),
        },
        PanelGap => new Panel { Size = new Size(1, Px(8)), Margin = Padding.Empty },
        PanelAction { Checked: bool isChecked } action => Wire(new CheckBox
        {
            Text = action.Text, Checked = isChecked, AutoSize = true, AutoCheck = false,
            Margin = new Padding(0, Px(4), Px(8), Px(4)),
        }, action),
        PanelAction { IsLink: true } action => Wire(new LinkLabel
        {
            Text = action.Text, AutoSize = true, LinkColor = AppTheme.Link, ActiveLinkColor = AppTheme.Link,
            LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, Px(2), 0, Px(2)),
        }, action),
        PanelAction action => Wire(new Button
        {
            Text = action.Text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = AppTheme.Control,
            FlatAppearance = { BorderColor = AppTheme.Line }, Padding = new Padding(Px(6), Px(1), Px(6), Px(1)),
            Margin = new Padding(0, Px(4), Px(8), Px(4)),
        }, action),
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    Control Wire(Control control, PanelAction action)
    {
        if (action.Run is { } run) control.Click += (_, _) => BeginInvoke(run);
        else control.Enabled = false;
        return control;
    }

    static Panel Rule(Padding margin) =>
        new() { Size = new Size(1, 1), Dock = DockStyle.Fill, BackColor = AppTheme.Line, Margin = margin };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            headerFont.Dispose();
            boldFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

sealed class LimitView : Control
{
    readonly PanelLimit limit;
    readonly Font boldFont;

    public LimitView(PanelLimit limit, Font font, Font boldFont)
    {
        this.limit = limit;
        this.boldFont = boldFont;
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, false);
        Font = font;
        FitHeight();
    }

    int Line => TextRenderer.MeasureText("Ag", Font, Size.Empty, TextFormatFlags.NoPadding).Height;
    /// Match the default text padding of neighboring WinForms labels.
    int Inset => (TextRenderer.MeasureText("A", Font).Width - TextRenderer.MeasureText("A", Font, Size.Empty, TextFormatFlags.NoPadding).Width) / 2;
    int Gap => LogicalToDeviceUnits(4);
    int BarHeight => LogicalToDeviceUnits(4);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        FitHeight();
    }

    void FitHeight() => Height = Line + Gap + BarHeight + (limit.Reset is null ? 0 : Gap + Line);

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(Parent?.BackColor ?? AppTheme.Background);
        const TextFormatFlags flags = TextFormatFlags.NoPadding;
        var inset = Inset;
        var width = Width - 2 * inset;
        TextRenderer.DrawText(graphics, limit.Label, Font, new Point(inset, 0), AppTheme.Text, flags);
        var left = $"{Format.Percent(limit.Remaining)}% left";
        var leftWidth = TextRenderer.MeasureText(left, boldFont, Size.Empty, flags).Width;
        TextRenderer.DrawText(graphics, left, boldFont, new Point(inset + width - leftWidth, 0), AppTheme.Text, flags);

        var barTop = Line + Gap;
        using (var track = new SolidBrush(AppTheme.Line)) graphics.FillRectangle(track, inset, barTop, width, BarHeight);
        using (var fill = new SolidBrush(AppTheme.Text))
            graphics.FillRectangle(fill, inset, barTop, (int)Math.Round(width * Math.Clamp(limit.Remaining / 100, 0, 1)), BarHeight);

        if (limit.Reset is string reset)
            TextRenderer.DrawText(graphics, reset, Font, new Point(inset, barTop + BarHeight + Gap), AppTheme.Dim, flags);
    }
}
