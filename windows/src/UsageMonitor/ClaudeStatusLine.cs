using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageMonitor;

enum ClaudeUsageMode { StatusLine, Credentials, Disabled }

sealed class ClaudeStatusLine(string directory, string executable)
{
    public const string Argument = "--claude-status-line";
    public const string RemoveArgument = "--remove-claude-status-line";
    public string Executable => executable;

    public static string ConfigDirectory =>
        Paths.FromEnvironment("CLAUDE_CONFIG_DIR") ?? Path.Combine(Paths.Home, ".claude");

    /// Claude Code uses Git Bash or PowerShell; both accept bare forward-slash paths without spaces.
    public string? Command => ShellSafePath(executable) is string path ? $"{path} {Argument}" : null;

    internal static string? ShellSafePath(string path)
    {
        if (!IsShellSafe(path)) path = Native.ShortPath(path) ?? path;
        path = path.Replace('\\', '/');
        return IsShellSafe(path) ? path : null;
    }

    static bool IsShellSafe(string path) => path.Length > 0 && path.All(c => char.IsAsciiLetterOrDigit(c) || @"\/:._-~".Contains(c));

    static bool IsGenerated(string? command) =>
        command is not null && command.EndsWith(" " + Argument, StringComparison.Ordinal)
        && command[..^(Argument.Length + 1)] is var path && !path.Contains(' ')
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public ClaudeUsageMode Configure(Func<bool> askToReplace, Func<Exception?, bool> askForCredentials)
    {
        try
        {
            var command = Command ?? throw new SetupException(
                "Usage Monitor is in a folder whose path Claude’s status line can’t run. Move it to a path without spaces or symbols.");
            var file = SettingsFile;
            var original = SettingsData(file);
            var settings = new JsonObject();
            if (original is not null)
            {
                settings = (original.Length == 0 ? null : Json.Parse(Encoding.UTF8.GetString(original))) as JsonObject
                    ?? throw new SetupException("Claude’s settings could not be read safely.");
            }
            if (Json.Bool(settings.Get("disableAllHooks")) == true)
                throw new SetupException("Claude’s settings disable status-line commands.");
            var existing = settings["statusLine"];
            var ownStatusLine = existing is JsonObject
                && Json.String(existing.Get("type")) == "command" && IsGenerated(Json.String(existing.Get("command")));
            if (ownStatusLine && Json.String(existing.Get("command")) == command) return ClaudeUsageMode.StatusLine;
            if (!ownStatusLine && existing is not null && !askToReplace())
                return askForCredentials(null) ? ClaudeUsageMode.Credentials : ClaudeUsageMode.Disabled;
            if (!SameData(SettingsData(file), original))
                throw new SetupException("Claude’s settings changed while setup was open.");

            settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command };
            Save(file, settings);
            return ClaudeUsageMode.StatusLine;
        }
        catch (Exception e)
        {
            return askForCredentials(e) ? ClaudeUsageMode.Credentials : ClaudeUsageMode.Disabled;
        }
    }

    /// Run by the uninstaller: removes the status line only while it still runs this copy of the app.
    public void Remove()
    {
        try
        {
            var file = SettingsFile;
            if (SettingsData(file) is not { Length: > 0 } data
                || Json.Parse(Encoding.UTF8.GetString(data)) is not JsonObject settings
                || Command is not string command
                || Json.String(settings.Get("statusLine").Get("command")) != command) return;
            settings.Remove("statusLine");
            Save(file, settings);
        }
        catch (Exception) { } // Never block uninstalling; a leftover status line just prints nothing.
    }

    string SettingsFile
    {
        get
        {
            var file = Path.Combine(directory, "settings.json");
            return File.Exists(file) ? new FileInfo(file).ResolveLinkTarget(true)?.FullName ?? file : file;
        }
    }

    static void Save(string file, JsonObject settings)
    {
        var json = settings.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".usage-monitor.tmp";
        File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
        File.Move(temporary, file, overwrite: true);
    }

    static byte[]? SettingsData(string file)
    {
        try { return File.ReadAllBytes(file); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    static bool SameData(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    sealed class SetupException(string message) : Exception(message);

    internal static ClaudeUsage? UsageFromStatusLine(string json, DateTimeOffset? receivedAt = null)
    {
        var now = receivedAt ?? DateTimeOffset.Now;
        var limits = Json.Parse(json).Get("rate_limits");
        var usage = new ClaudeUsage(Window(limits.Get("five_hour"), 300), Window(limits.Get("seven_day"), 10080), null, now);
        return usage.Windows.Count == 0 ? null : usage;

        LimitWindow? Window(JsonNode? window, int minutes) =>
            Json.Number(window.Get("used_percentage")) is double used && used is >= 0 and <= 100
            && Json.Number(window.Get("resets_at")) is double reset && reset > now.ToUnixTimeMilliseconds() / 1000.0
                ? new LimitWindow(used, minutes, reset)
                : null;
    }

    public static int Publish()
    {
        var utf8 = new UTF8Encoding(false);
        var input = new StreamReader(Console.OpenStandardInput(), utf8).ReadToEnd();
        if (UsageFromStatusLine(input) is not { } usage) return 0;
        var limits = new JsonObject();
        foreach (var (key, window) in new[] { ("five_hour", usage.FiveHour), ("seven_day", usage.Weekly) })
        {
            if (window?.ResetsAt is double reset)
                limits[key] = new JsonObject { ["used_percentage"] = window.UsedPercent, ["resets_at"] = reset };
        }
        // Forward only quota fields; status-line input also contains private workspace information.
        UsagePipe.Send(ConfigDirectory, new JsonObject { ["rate_limits"] = limits }.ToJsonString());
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
        output.WriteLine(string.Join(" · ", usage.Windows.Select(w => $"{w.Label}: {Format.Percent(w.RemainingPercent())}% left")));
        return 0;
    }
}

static class UsagePipe
{
    internal static string Name(string configDirectory)
    {
        var key = Path.GetFullPath(configDirectory).TrimEnd('\\', '/').ToUpperInvariant();
        return "UsageMonitor.Claude." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    public static void Send(string configDirectory, string message)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name(configDirectory), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(250); // Short: the status line waits on us, and the app may not be running.
            pipe.Write(Encoding.UTF8.GetBytes(message));
            pipe.Flush();
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { }
    }

    public static void Listen(string configDirectory, Action<string> received, CancellationToken cancel)
    {
        var context = SynchronizationContext.Current;
        var name = Name(configDirectory);
        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(name, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cancel);
                    var message = await new StreamReader(server, Encoding.UTF8).ReadToEndAsync(cancel);
                    if (context is null) received(message);
                    else context.Post(_ => received(message), null);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(1000, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }
}
