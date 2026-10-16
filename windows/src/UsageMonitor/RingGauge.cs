using System.Drawing.Drawing2D;

namespace UsageMonitor;

sealed record RingGauge(string Label, Color Color, IReadOnlyList<(double? Percent, float Width)> Rings)
{
    public const float Diameter = 38;
    const float RingGap = 1.5f;
    /// Fits "CLA" inside Claude's inner ring; Codex uses the same size so the labels match.
    const float LabelWidth = 14;
    static readonly FontFamily labelFont = Fonts.Find("Cascadia Mono", "Consolas");

    public static RingGauge Claude(double? fiveHour, double? weekly) =>
        new("CLA", Color.FromArgb(0xD9, 0x77, 0x57), [(weekly, 2.5f), (fiveHour, 4.5f)]);

    public static RingGauge Codex(double? remaining, Color color) => new("COD", color, [(remaining, 4.5f)]);

    public void Draw(Graphics graphics, float x, float y, float scale, Color text)
    {
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(x, y);
        graphics.ScaleTransform(scale, scale);

        var inset = 0f;
        foreach (var (percent, width) in Rings)
        {
            var center = inset + width / 2;
            var bounds = new RectangleF(center, center, Diameter - 2 * center, Diameter - 2 * center);
            using (var track = new Pen(Color.FromArgb(60, Color), width)) graphics.DrawEllipse(track, bounds);
            if (percent is double p && p > 0)
            {
                using var ring = new Pen(Color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (p >= 100) graphics.DrawEllipse(ring, bounds);
                else graphics.DrawArc(ring, bounds, -90, (float)(360 * p / 100));
            }
            inset += width + RingGap;
        }

        using var label = new GraphicsPath();
        var style = labelFont.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
        label.AddString(Label, labelFont, (int)style, 100, PointF.Empty, StringFormat.GenericTypographic);
        var ink = label.GetBounds();
        var size = LabelWidth / ink.Width;
        using var transform = new Matrix(size, 0, 0, size,
            (Diameter - ink.Width * size) / 2 - ink.X * size,
            (Diameter - ink.Height * size) / 2 - ink.Y * size);
        label.Transform(transform);
        var known = Rings.Any(r => r.Percent is not null);
        using (var brush = new SolidBrush(known ? text : Color.FromArgb(102, text))) graphics.FillPath(brush, label);
        graphics.Restore(state);
    }
}
