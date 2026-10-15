using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageDisplay;

/// Codex appends rate-limit snapshots to session logs after each model turn.
sealed class CodexUsageReader(string? codexHome = null)
{
    readonly string sessionsDir = Path.Combine(codexHome ?? DefaultCodexHome, "sessions");
    (string Path, DateTime Modified, CodexUsage Usage)? cache;
    readonly Dictionary<string, DateTime> withoutSnapshot = [];

    public static string DefaultCodexHome => Paths.FromEnvironment("CODEX_HOME") ?? Path.Combine(Paths.Home, ".codex");

    public CodexUsage? Read()
    {
        // A session that never got a server reply has no snapshot.
        foreach (var (path, modified) in SessionFilesByRecency().Take(5))
        {
            if (cache is { } cached && cached.Path == path && cached.Modified == modified) return cached.Usage;
            if (withoutSnapshot.TryGetValue(path, out var seen) && seen == modified) continue;
            if (LastSnapshot(path) is { } usage)
            {
                cache = (path, modified, usage);
                return usage;
            }
            withoutSnapshot[path] = modified;
        }
        return null;
    }

    List<(string Path, DateTime Modified)> SessionFilesByRecency()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        try
        {
            return new DirectoryInfo(sessionsDir).EnumerateFiles("*.jsonl", options)
                .Select(file => (file.FullName, file.LastWriteTimeUtc))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Try the tail first; logs can be several MB and the snapshot is usually near the end.
    static CodexUsage? LastSnapshot(string path) => LastSnapshot(path, 512 * 1024) ?? LastSnapshot(path, null);

    static CodexUsage? LastSnapshot(string path, long? tailBytes)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (tailBytes is long tail && stream.Length > tail) stream.Seek(-tail, SeekOrigin.End);
            else if (tailBytes is null && stream.Length <= 512 * 1024) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var line in text.Split('\n').Reverse())
        {
            if (line.Contains("\"rate_limits\":{") && Decode(line) is { } usage) return usage;
        }
        return null;
    }

    internal static CodexUsage? Decode(string line)
    {
        var e = Json.Parse(line);
        var limits = e.Get("payload").Get("rate_limits");
        var windows = new[] { Window(limits.Get("primary")), Window(limits.Get("secondary")) }
            .OfType<LimitWindow>().ToList();
        if (windows.Count == 0) return null;
        return new CodexUsage(windows, Json.String(limits.Get("plan_type")), Json.Date(e.Get("timestamp")));

        static LimitWindow? Window(JsonNode? window) => Json.Number(window.Get("used_percent")) is double used
            ? new LimitWindow(used, Json.Int(window.Get("window_minutes")), Json.Number(window.Get("resets_at")))
            : null;
    }
}

/// `codex app-server` asks OpenAI with Codex's own login and renews it when needed.
static class CodexUsageApi
{
    public static Task<CodexUsage?> FetchAsync(string executable) => Task.Run(() => RequestAsync(executable));

    static async Task<CodexUsage?> RequestAsync(string executable)
    {
        using var process = CommandLineTool.Start(executable, "app-server --listen stdio://", readOutput: true);
        if (process is null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            async Task Send(object message)
            {
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(message) + "\n");
                await process.StandardInput.FlushAsync();
            }

            await Send(new
            {
                method = "initialize", id = 1,
                @params = new { clientInfo = new { name = "usage_display", title = "Usage Display", version = "1.0.0" } },
            });
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is string line)
            {
                var id = Json.Int(Json.Parse(line).Get("id"));
                if (id == 1)
                {
                    await Send(new { method = "initialized", @params = new { } });
                    await Send(new { method = "account/rateLimits/read", id = 2 });
                }
                else if (id == 2)
                {
                    return UsageFromReply(line);
                }
            }
            return null;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            CommandLineTool.Kill(process);
        }
    }

    internal static CodexUsage? UsageFromReply(string reply, DateTimeOffset? now = null)
    {
        var result = Json.Parse(reply).Get("result");
        var limits = result.Get("rateLimitsByLimitId").Get("codex") ?? result.Get("rateLimits");
        var windows = new[] { limits.Get("primary"), limits.Get("secondary") }
            .Select(w => Json.Number(w.Get("usedPercent")) is double used
                ? new LimitWindow(used, Json.Int(w.Get("windowDurationMins")), Json.Number(w.Get("resetsAt")))
                : null)
            .OfType<LimitWindow>().ToList();
        if (windows.Count == 0) return null;

        BankedResets? resets = null;
        if (result.Get("rateLimitResetCredits") is JsonObject credits && Json.Int(credits.Get("availableCount")) is int count)
        {
            var expirations = (credits.Get("credits") as JsonArray ?? [])
                .Where(credit => Json.String(credit.Get("status")) == "available")
                .Select(credit => Json.UnixSeconds(credit.Get("expiresAt")))
                .OrderBy(date => date ?? DateTimeOffset.MaxValue)
                .ToList();
            resets = new BankedResets(Math.Max(0, count), expirations);
        }
        return new CodexUsage(windows, Json.String(limits.Get("planType")), now ?? DateTimeOffset.Now, resets);
    }
}
