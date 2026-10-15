using System.Diagnostics;
using System.Text;

namespace UsageDisplay;

/// An app started at sign-in can have an older PATH than a terminal, so the usual install locations are checked too.
static class CommandLineTool
{
    public static string? Find(string name)
    {
        var directories = new[]
        {
            Path.Combine(Paths.Home, ".local", "bin"),
            Path.Combine(Paths.LocalAppData, "Programs", "OpenAI", "Codex", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"')));
        foreach (var directory in directories)
        {
            foreach (var extension in new[] { ".exe", ".cmd" })
            {
                var path = Path.Combine(directory, name + extension);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    /// npm shims need cmd.exe and a PATH containing node.
    public static Process? Start(string executable, string arguments, bool readOutput = false)
    {
        var shim = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                   || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo
        {
            FileName = shim ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : executable,
            Arguments = shim ? $"/d /s /c \"\"{executable}\" {arguments}\"" : arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = readOutput,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = readOutput ? new UTF8Encoding(false) : null,
        };
        info.Environment["PATH"] = Path.GetDirectoryName(executable) + ";" + info.Environment["PATH"];
        try
        {
            return Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    public static async Task RunAsync(string executable, string arguments, TimeSpan? timeout = null)
    {
        using var process = Start(executable, arguments);
        if (process is null) return;
        process.StandardInput.Close();
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(cancel.Token); }
        catch (OperationCanceledException) { Kill(process); }
    }

    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
