using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace UsageDisplay;

static class Paths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string? FromEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}

static class Log
{
    [Conditional("DEBUG")]
    public static void AttachToTerminal() => Native.AttachConsole(-1 /* ATTACH_PARENT_PROCESS */);

    [Conditional("DEBUG")]
    public static void QuitOnCtrlC(Action quit)
    {
        var context = SynchronizationContext.Current;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // otherwise the process dies at once, leaving a dead tray icon behind
            context?.Post(_ => quit(), null);
        };
    }

    [Conditional("DEBUG")]
    public static void Write(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
}

static class Settings
{
    const string Key = @"HKEY_CURRENT_USER\Software\UsageDisplay";

    public static int? Get(string name) => Registry.GetValue(Key, name, null) as int?;

    public static void Set(string name, int value) => Registry.SetValue(Key, name, value);
}

static class Fonts
{
    public static FontFamily Find(params string[] names)
    {
        foreach (var name in names)
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { }
        }
        return FontFamily.GenericSansSerif;
    }
}

static class Json
{
    public static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    public static JsonNode? Get(this JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    public static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? number : null;

    public static int? Int(JsonNode? node) =>
        Number(node) is double number && number == Math.Floor(number) && Math.Abs(number) <= int.MaxValue ? (int)number : null;

    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    public static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    /// ISO 8601 with or without fractional seconds, as Codex and Anthropic both send.
    public static DateTimeOffset? Date(JsonNode? node) =>
        String(node) is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    public static DateTimeOffset? UnixSeconds(JsonNode? node) =>
        Number(node) is double seconds ? DateTimeOffset.UnixEpoch.AddSeconds(seconds) : null;
}

static class Format
{
    public static int Percent(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    public static string Relative(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var delta = date - (now ?? DateTimeOffset.Now);
        var past = delta < TimeSpan.Zero;
        var span = past ? -delta : delta;
        if (span.TotalMinutes < 1) return past ? "just now" : "in under a minute";
        string amount;
        if (span.TotalHours < 1) amount = Unit(span.Minutes, "minute");
        else if (span.TotalDays < 1) amount = Join(Unit(span.Hours, "hour"), Unit(span.Minutes, "minute"));
        else amount = Join(Unit(span.Days, "day"), Unit(span.Hours, "hour"));
        return past ? amount + " ago" : "in " + amount;

        static string Unit(int count, string name) => count == 0 ? "" : count == 1 ? $"1 {name}" : $"{count} {name}s";
        static string Join(string a, string b) => b.Length == 0 ? a : $"{a} {b}";
    }

    public static string Absolute(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return $"{local:ddd MMM d}, {local:t}";
    }
}

static class Native
{
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// Windows 11's rounded corners and a thin border for borderless windows; ignored on Windows 10.
    public static void RoundCorners(IntPtr window, Color border)
    {
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(window, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        var color = border.R | border.G << 8 | border.B << 16;
        DwmSetWindowAttribute(window, 34 /* DWMWA_BORDER_COLOR */, ref color, sizeof(int));
    }

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint length);

    /// The 8.3 form of an existing path, or null when the volume doesn't keep short names.
    public static string? ShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
    }
}
