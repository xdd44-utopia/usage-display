using Microsoft.Win32;

namespace UsageDisplay;

static class AppLocation
{
#pragma warning disable IL3000 // Location is empty exactly when the app is a single-file build.
    public static bool IsPublished => string.IsNullOrEmpty(typeof(AppLocation).Assembly.Location);
#pragma warning restore IL3000
}

static class LaunchAtLogin
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "UsageDisplay";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) as string == Command;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
