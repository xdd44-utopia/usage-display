namespace UsageDisplay;

sealed class FloatingWindow : Form
{
    const int Pad = 6;
    const int Spacing = 8;

    readonly ToolTip tooltip = new();
    List<RingGauge> gauges = [];
    Point pressedAt;
    Point startLocation;
    bool pressed;
    bool dragging;
    bool placed;

    public event Action? Clicked;

    public FloatingWindow()
    {
        Text = "Usage Display";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle, AppTheme.Line);
    }

    float GaugeScale => DeviceDpi / 96f;

    public void SetGauges(IEnumerable<RingGauge> next, string summary)
    {
        gauges = next.ToList();
        if (tooltip.GetToolTip(this) != summary) tooltip.SetToolTip(this, summary);
        Fit();
        Native.RoundCorners(Handle, AppTheme.Line);
        Invalidate();
    }

    void Fit()
    {
        var scale = GaugeScale;
        var diameter = (int)Math.Ceiling(RingGauge.Diameter * scale);
        var size = new Size(
            LogicalToDeviceUnits(Pad) * 2 + diameter * gauges.Count + LogicalToDeviceUnits(Spacing) * Math.Max(0, gauges.Count - 1),
            LogicalToDeviceUnits(Pad) * 2 + diameter);
        if (!placed)
        {
            Size = size;
            Location = SavedLocation() ?? DefaultLocation();
            placed = true;
            return;
        }
        if (size == Size) return;
        var area = Screen.FromControl(this).WorkingArea;
        var right = Right;
        var keepRight = Left + Width / 2 > area.Left + area.Width / 2;
        Size = size;
        if (keepRight) Left = right - Width;
        KeepOnScreen();
    }

    Point? SavedLocation()
    {
        if (Settings.Get("FloatingX") is not int x || Settings.Get("FloatingY") is not int y) return null;
        var center = new Point(x + Width / 2, y + Height / 2);
        return Screen.AllScreens.Any(s => s.WorkingArea.Contains(center)) ? new Point(x, y) : null;
    }

    Point DefaultLocation()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Point.Empty);
        var margin = LogicalToDeviceUnits(16);
        return new Point(area.Right - Width - margin, area.Bottom - Height - margin);
    }

    void KeepOnScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                             Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Fit();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(AppTheme.Background);
        var scale = GaugeScale;
        float x = LogicalToDeviceUnits(Pad);
        float y = LogicalToDeviceUnits(Pad);
        foreach (var gauge in gauges)
        {
            gauge.Draw(e.Graphics, x, y, scale, AppTheme.Text);
            x += (int)Math.Ceiling(RingGauge.Diameter * scale) + LogicalToDeviceUnits(Spacing);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pressed = true;
        dragging = false;
        pressedAt = Cursor.Position;
        startLocation = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!pressed) return;
        var dx = Cursor.Position.X - pressedAt.X;
        var dy = Cursor.Position.Y - pressedAt.Y;
        var threshold = SystemInformation.DragSize;
        if (!dragging && (Math.Abs(dx) > threshold.Width / 2 || Math.Abs(dy) > threshold.Height / 2)) dragging = true;
        if (dragging) Location = new Point(startLocation.X + dx, startLocation.Y + dy);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!pressed || e.Button != MouseButtons.Left) return;
        pressed = false;
        if (!dragging)
        {
            Clicked?.Invoke();
            return;
        }
        KeepOnScreen();
        Settings.Set("FloatingX", Left);
        Settings.Set("FloatingY", Top);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tooltip.Dispose();
        base.Dispose(disposing);
    }
}
