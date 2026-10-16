using System.Runtime.InteropServices;

namespace UsageDisplay.Tests;

public sealed class UpdateCheckTests
{
    static string Release(string tag, params string[] assets) =>
        "{\"tag_name\":\"" + tag + "\",\"assets\":[" + string.Join(",", assets.Select(a =>
            "{\"name\":\"" + a + "\",\"browser_download_url\":\"https://github.com/o/r/releases/download/" + tag + "/" + a + "\"}")) + "]}";

    static readonly string[] Both = ["UsageDisplay-Setup-x64.exe", "UsageDisplay-Setup-arm64.exe"];
    static readonly Version Current = new(0, 1, 0, 0);

    [Fact]
    public void NewerReleaseOffersTheInstallerForThisArchitecture()
    {
        var x64 = UpdateCheck.FromRelease(Release("v0.1.1", Both), Current, Architecture.X64);
        var arm = UpdateCheck.FromRelease(Release("v0.1.1", Both), Current, Architecture.Arm64);
        Assert.Equal(new Version(0, 1, 1), x64?.Version);
        Assert.Equal("https://github.com/o/r/releases/download/v0.1.1/UsageDisplay-Setup-x64.exe", x64?.DownloadUrl);
        Assert.Equal("https://github.com/o/r/releases/download/v0.1.1/UsageDisplay-Setup-arm64.exe", arm?.DownloadUrl);
    }

    [Fact]
    public void TagWithoutPrefixStillCounts() =>
        Assert.NotNull(UpdateCheck.FromRelease(Release("0.2.0", Both), Current, Architecture.X64));

    [Fact]
    public void SameOrOlderReleaseOffersNothing()
    {
        Assert.Null(UpdateCheck.FromRelease(Release("v0.1.0", Both), Current, Architecture.X64));
        Assert.Null(UpdateCheck.FromRelease(Release("v0.0.9", Both), Current, Architecture.X64));
    }

    [Fact]
    public void MissingInstallerOffersNothing() =>
        Assert.Null(UpdateCheck.FromRelease(Release("v0.1.1", "UsageDisplay-Setup-arm64.exe"), Current, Architecture.X64));

    [Theory]
    [InlineData("http://github.com/o/r/x.exe")]
    [InlineData("https://example.com/o/r/x.exe")]
    [InlineData("https://github.com.evil.com/x.exe")]
    [InlineData("/relative/x.exe")]
    public void InstallerFromAnotherOriginOffersNothing(string url) =>
        Assert.Null(UpdateCheck.FromRelease(
            "{\"tag_name\":\"v0.2.0\",\"assets\":[{\"name\":\"UsageDisplay-Setup-x64.exe\",\"browser_download_url\":\"" + url + "\"}]}",
            Current, Architecture.X64));

    [Fact]
    public void DigestAndSizeAreCaptured()
    {
        var body = "{\"tag_name\":\"v0.2.0\",\"assets\":[{\"name\":\"UsageDisplay-Setup-x64.exe\",\"size\":1234,"
            + "\"digest\":\"sha256:ABCDEF\",\"browser_download_url\":\"https://github.com/o/r/x.exe\"}]}";
        var update = UpdateCheck.FromRelease(body, Current, Architecture.X64);
        Assert.Equal("ABCDEF", update?.Sha256);
        Assert.Equal(1234, update?.Size);
        Assert.Null(UpdateCheck.FromRelease(Release("v0.2.0", Both), Current, Architecture.X64)?.Sha256);
    }

    [Fact]
    public void MalformedBodiesOfferNothing()
    {
        Assert.Null(UpdateCheck.FromRelease("not json", Current, Architecture.X64));
        Assert.Null(UpdateCheck.FromRelease("{\"message\":\"Not Found\"}", Current, Architecture.X64));
        Assert.Null(UpdateCheck.FromRelease("{\"tag_name\":\"latest\",\"assets\":[]}", Current, Architecture.X64));
        Assert.Null(UpdateCheck.FromRelease("{\"tag_name\":\"v0.2.0\"}", Current, Architecture.X64));
    }
}
