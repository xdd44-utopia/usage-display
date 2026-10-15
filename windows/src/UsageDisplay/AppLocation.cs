using System.Diagnostics;
using Microsoft.Win32;

namespace UsageDisplay;

static class AppLocation
{
    public static string InstallPath => Path.Combine(Paths.LocalAppData, "Programs", "UsageDisplay", "UsageDisplay.exe");

#pragma warning disable IL3000 // Location is empty exactly when the app is a single-file build.
    public static bool IsPublished => string.IsNullOrEmpty(typeof(AppLocation).Assembly.Location);
#pragma warning restore IL3000

    public static string? MisplacedExecutable =>
        IsPublished && Environment.ProcessPath is string exe
        && !string.Equals(Path.GetFullPath(exe), InstallPath, StringComparison.OrdinalIgnoreCase) ? exe : null;

    public static string Install(string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstallPath)!);
        try
        {
            File.Move(executable, InstallPath, overwrite: true);
        }
        catch (IOException)
        {
            File.Copy(executable, InstallPath, overwrite: true); // e.g. run from another drive or a read-only folder
        }
        CreateStartMenuShortcut(InstallPath);
        return InstallPath;
    }

    static void CreateStartMenuShortcut(string target)
    {
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not Type type) return;
            dynamic shell = Activator.CreateInstance(type)!;
            var link = shell.CreateShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Usage Display.lnk"));
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "Codex and Claude Code usage in the notification area";
            link.Save();
        }
        catch (Exception) { }
    }

    public static void Relaunch(string executable) =>
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
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
