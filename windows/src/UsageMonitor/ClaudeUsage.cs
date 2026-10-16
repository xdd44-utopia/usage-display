using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace UsageMonitor;

/// Claude Code refreshes `cachedUsageUtilization` when it fetches usage itself (e.g. `/usage`).
sealed class ClaudeUsageReader(string? configFile = null)
{
    readonly string configFile = configFile ?? DefaultConfigFile;
    (DateTime Modified, ClaudeUsage? Usage)? cache;

    public static string DefaultConfigFile =>
        Path.Combine(Paths.FromEnvironment("CLAUDE_CONFIG_DIR") ?? Paths.Home, ".claude.json");

    public ClaudeUsage? Read()
    {
        string text;
        DateTime modified;
        try
        {
            if (!File.Exists(configFile)) return null;
            modified = File.GetLastWriteTimeUtc(configFile);
            if (cache is { } cached && cached.Modified == modified) return cached.Usage;
            using var stream = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            text = new StreamReader(stream).ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return cache?.Usage;
        }
        // Unparseable usually means we caught Claude Code mid-write; keep the last good read.
        if (Json.Parse(text) is not JsonObject config) return cache?.Usage;
        var usage = Decode(config);
        cache = (modified, usage);
        return usage;
    }

    internal static ClaudeUsage? Decode(JsonNode config)
    {
        var cached = config.Get("cachedUsageUtilization");
        var account = config.Get("oauthAccount");
        // After switching accounts the cache can still hold the previous account's limits.
        if (Json.String(account.Get("accountUuid")) is string current
            && Json.String(cached.Get("accountUuid")) is string owner && current != owner) return null;
        var plan = Json.String(account.Get("organizationType"))?.Replace("claude_", "");
        var fetchedAt = Json.Number(cached.Get("fetchedAtMs")) is double ms
            ? DateTimeOffset.UnixEpoch.AddMilliseconds(ms) : (DateTimeOffset?)null;
        return FromUsageResponse(cached.Get("utilization"), plan, fetchedAt);
    }

    /// The usage object `/api/oauth/usage` returns, which Claude Code also caches verbatim.
    internal static ClaudeUsage? FromUsageResponse(JsonNode? body, string? plan, DateTimeOffset? fetchedAt)
    {
        var usage = new ClaudeUsage(Window(body.Get("five_hour"), 300), Window(body.Get("seven_day"), 10080), plan, fetchedAt);
        return usage.Windows.Count == 0 ? null : usage;

        static LimitWindow? Window(JsonNode? window, int minutes) => Json.Number(window.Get("utilization")) is double used
            ? new LimitWindow(used, minutes, Json.Date(window.Get("resets_at"))?.ToUnixTimeMilliseconds() / 1000.0)
            : null;
    }
}

enum ClaudeFetch { Usage, Expired, Unavailable }

/// Refresh tokens are single-use; renewing here would sign Claude Code out.
static class ClaudeUsageApi
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// On Windows Claude Code keeps its login in a file the user's account protects, not a credential vault.
    public static string CredentialsFile =>
        Path.Combine(Paths.FromEnvironment("CLAUDE_CONFIG_DIR") ?? Path.Combine(Paths.Home, ".claude"), ".credentials.json");

    public static async Task<(ClaudeFetch Status, ClaudeUsage? Usage)> FetchAsync()
    {
        string text;
        try { text = await File.ReadAllTextAsync(CredentialsFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (ClaudeFetch.Unavailable, null); }

        var oauth = Json.Parse(text).Get("claudeAiOauth");
        if (Json.String(oauth.Get("accessToken")) is not string token) return (ClaudeFetch.Unavailable, null);
        if (Json.Number(oauth.Get("expiresAt")) is double expiresAt
            && DateTimeOffset.UnixEpoch.AddMilliseconds(expiresAt) <= DateTimeOffset.Now) return (ClaudeFetch.Expired, null);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        try
        {
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return (ClaudeFetch.Expired, null);
            if (response.StatusCode != HttpStatusCode.OK) return (ClaudeFetch.Unavailable, null);
            var body = Json.Parse(await response.Content.ReadAsStringAsync());
            var usage = ClaudeUsageReader.FromUsageResponse(body, Json.String(oauth.Get("subscriptionType")), DateTimeOffset.Now);
            return usage is null ? (ClaudeFetch.Unavailable, null) : (ClaudeFetch.Usage, usage);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (ClaudeFetch.Unavailable, null);
        }
    }
}
