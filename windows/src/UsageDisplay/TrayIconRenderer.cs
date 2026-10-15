using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using Microsoft.Win32;

namespace UsageDisplay;

static class TrayIconRenderer
{
    /// SF Mono Semibold "%" outline; GDI+ cannot load its PostScript OpenType font.
    const string PercentGlyph =
        "M288 745 C465 745 577 873 577 1074 L577 1148 C577 1341 463 1469 288 1469 C114 1469 0 1341 0 1148 L0 1074 "
        + "C0 873 112 745 288 745 Z M288 898 C208 898 164 961 164 1079 L164 1137 C164 1254 208 1318 288 1318 "
        + "C369 1318 412 1254 412 1137 L412 1079 C412 961 369 898 288 898 Z M978 -27 C1154 -27 1266 101 1266 301 "
        + "L1266 375 C1266 568 1152 697 978 697 C803 697 689 568 689 375 L689 301 C689 101 801 -27 978 -27 Z "
        + "M978 125 C897 125 854 189 854 306 L854 364 C854 481 897 545 978 545 C1058 545 1102 481 1102 364 "
        + "L1102 306 C1102 189 1058 125 978 125 Z M1266 1443 L0 157 L0 0 L1266 1286 Z";

    public static Bitmap Mark(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var path = Glyph(PercentGlyph);
        var ink = path.GetBounds();
        var inset = size / 16f;
        var scale = Math.Min((size - 2 * inset) / ink.Width, (size - 2 * inset) / ink.Height);
        using var transform = new Matrix(scale, 0, 0, scale,
            (size - ink.Width * scale) / 2 - ink.X * scale,
            (size - ink.Height * scale) / 2 - ink.Y * scale);
        path.Transform(transform);
        using var brush = new SolidBrush(Theme.Foreground);
        graphics.FillPath(brush, path);
        return bitmap;
    }

    static GraphicsPath Glyph(string data)
    {
        var path = new GraphicsPath(FillMode.Winding);
        var tokens = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = PointF.Empty;
        var i = 0;
        while (i < tokens.Length)
        {
            var command = tokens[i][0];
            tokens[i] = tokens[i][1..];
            switch (command)
            {
                case 'M':
                    path.StartFigure();
                    current = Next();
                    break;
                case 'L':
                    var end = Next();
                    path.AddLine(current, end);
                    current = end;
                    break;
                case 'C':
                    var (c1, c2, to) = (Next(), Next(), Next());
                    path.AddBezier(current, c1, c2, to);
                    current = to;
                    break;
                case 'Z':
                    path.CloseFigure();
                    i++;
                    break;
            }
        }
        return path;

        PointF Next()
        {
            var x = float.Parse(tokens[i++], CultureInfo.InvariantCulture);
            var y = float.Parse(tokens[i++], CultureInfo.InvariantCulture);
            return new PointF(x, -y);
        }
    }

    public static int IconSize()
    {
        var taskbar = Native.FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar != 0 ? Native.GetDpiForWindow(taskbar) : 0;
        if (dpi == 0) dpi = Native.GetDpiForSystem();
        var size = Native.GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi);
        return size > 0 ? size : SystemInformation.SmallIconSize.Width;
    }

    static class Theme
    {
        static bool LightTaskbar =>
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0) is int light && light != 0;

        public static Color Foreground => LightTaskbar ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.White;
        public static string Key => LightTaskbar ? "light" : "dark";
    }

    public static string ThemeKey => Theme.Key;
}
