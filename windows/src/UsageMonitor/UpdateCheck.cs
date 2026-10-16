using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace UsageMonitor;

sealed record AvailableUpdate(Version Version, string DownloadUrl, string? Sha256 = null, long? Size = null);

static class UpdateCheck
{
    const string LatestRelease = "https://api.github.com/repos/xdd44-utopia/usage-monitor/releases/latest";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly HttpClient downloader = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static Version Current => Normalize(typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static string AssetName(Architecture architecture) =>
        architecture == Architecture.Arm64 ? "UsageMonitor-Setup-arm64.exe" : "UsageMonitor-Setup-x64.exe";

    static string UpdateFolder => Path.Combine(Path.GetTempPath(), "UsageMonitor-Update");

    static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    public static AvailableUpdate? FromRelease(string body, Version current, Architecture architecture)
    {
        var release = Json.Parse(body);
        if (Json.String(release.Get("tag_name"))?.Trim().TrimStart('v', 'V') is not string tag
            || !Version.TryParse(tag, out var parsed)) return null;
        var latest = Normalize(parsed);
        if (latest <= Normalize(current)) return null;
        var name = AssetName(architecture);
        if (release.Get("assets") is not JsonArray assets) return null;
        foreach (var asset in assets)
        {
            if (Json.String(asset.Get("name")) != name || Json.String(asset.Get("browser_download_url")) is not string url
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || uri.Host != "github.com") continue;
            var digest = Json.String(asset.Get("digest"));
            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..] : null;
            var size = Json.Number(asset.Get("size")) is double bytes && bytes >= 0 ? (long)bytes : (long?)null;
            return new AvailableUpdate(latest, url, sha256, size);
        }
        return null;
    }

    /// Returns null when there is no update; throws when the check itself failed.
    public static async Task<AvailableUpdate?> CheckAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UsageMonitor", Current.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return FromRelease(await response.Content.ReadAsStringAsync(), Current, RuntimeInformation.ProcessArchitecture);
    }

    public static void CleanUp()
    {
        try { Directory.Delete(UpdateFolder, true); }
        catch (Exception) { }
    }

    /// Returns the downloaded installer's path, or null when the download failed.
    public static async Task<string?> DownloadAsync(AvailableUpdate update, CancellationToken cancel)
    {
        var path = Path.Combine(UpdateFolder, $"UsageMonitor-Setup-{update.Version}-{Guid.NewGuid():N}.exe");
        try
        {
            Directory.CreateDirectory(UpdateFolder);
            using var response = await downloader.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode) return null;
            await using (var source = await response.Content.ReadAsStreamAsync(cancel))
            await using (var file = File.Create(path))
            {
                await source.CopyToAsync(file, cancel);
            }
            if (update.Size is long size && new FileInfo(path).Length != size) throw new IOException("Size mismatch");
            if (update.Sha256 is string expected)
            {
                await using var file = File.OpenRead(path);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Checksum mismatch");
            }
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            try { File.Delete(path); } catch (Exception) { }
            if (e is OperationCanceledException && cancel.IsCancellationRequested) throw;
            return null;
        }
    }

    public static void StartInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
}
