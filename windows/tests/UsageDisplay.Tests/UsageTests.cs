using System.Diagnostics;
using System.Text.Json.Nodes;

namespace UsageDisplay.Tests;

public sealed class CodexUsageTests
{
    [Fact]
    public void ReplyPrefersTheCodexLimitAndKeepsEveryWindow()
    {
        const string reply = """
            {"id":2,"result":{
              "rateLimits":{"primary":{"usedPercent":90,"windowDurationMins":300,"resetsAt":1700000000},"planType":"other"},
              "rateLimitsByLimitId":{"codex":{
                "primary":{"usedPercent":19,"windowDurationMins":300,"resetsAt":1700000000},
                "secondary":{"usedPercent":28.4,"windowDurationMins":10080,"resetsAt":1700500000},
                "planType":"prolite"}},
              "rateLimitResetCredits":{"availableCount":4,"credits":[
                {"status":"available"},
                {"status":"available","expiresAt":1794007077},
                {"status":"redeemed","expiresAt":1790000000},
                {"status":"available","expiresAt":1793299515}]}}}
            """;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_699_000_000);
        var usage = CodexUsageApi.UsageFromReply(reply, now);
        Assert.NotNull(usage);
        Assert.Equal("prolite", usage.PlanType);
        Assert.Equal(["5-hour", "Weekly"], usage.Windows.Select(w => w.Label));
        Assert.Equal([19, 28.4], usage.Windows.Select(w => w.UsedPercent));
        Assert.Equal(now, usage.FetchedAt);
        Assert.Equal(4, usage.BankedResets!.Count);
        Assert.Equal([DateTimeOffset.FromUnixTimeSeconds(1_793_299_515), DateTimeOffset.FromUnixTimeSeconds(1_794_007_077), null],
            usage.BankedResets.Expirations);
    }

    [Fact]
    public void ReplyFallsBackToTopLevelLimitsAndRejectsEmptyOrErrorReplies()
    {
        const string topLevel = """{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":19,"windowDurationMins":10080},"secondary":null}}}""";
        Assert.Equal(["Weekly"], CodexUsageApi.UsageFromReply(topLevel)!.Windows.Select(w => w.Label));
        Assert.Null(CodexUsageApi.UsageFromReply(topLevel)!.BankedResets);
        Assert.Null(CodexUsageApi.UsageFromReply("""{"id":2,"result":{"rateLimits":{"primary":null,"secondary":null}}}"""));
        Assert.Null(CodexUsageApi.UsageFromReply("""{"id":2,"error":{"code":-32600,"message":"not logged in"}}"""));
    }

    [Fact]
    public async Task HandshakeWithAppServer()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            // An npm-style .cmd shim, which also exercises launching through cmd.exe.
            File.WriteAllText(Path.Combine(directory, "fake.ps1"), """
                $line = [Console]::In.ReadLine(); if ($line -notlike '*"initialize"*') { exit 1 }
                [Console]::Out.WriteLine('{"method":"configWarning","params":{}}')
                [Console]::Out.WriteLine('{"id":1,"result":{}}'); [Console]::Out.Flush()
                $line = [Console]::In.ReadLine(); if ($line -notlike '*"initialized"*') { exit 1 }
                $line = [Console]::In.ReadLine(); if ($line -notlike '*account/rateLimits/read*') { exit 1 }
                [Console]::Out.WriteLine('{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":19,"windowDurationMins":10080}}}}')
                [Console]::Out.Flush()
                Start-Sleep 30
                """);
            var codex = Path.Combine(directory, "codex.cmd");
            File.WriteAllText(codex, "@powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0fake.ps1\"\r\n");

            var started = Stopwatch.StartNew();
            var usage = await CodexUsageApi.FetchAsync(codex);
            Assert.Equal(19, usage?.Windows[0].UsedPercent);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), "returns on the reply, without waiting for the server to exit");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ReaderFindsTheLatestSnapshotInTheNewestSession()
    {
        var home = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var day = Path.Combine(home, "sessions", "2026", "10", "11");
        Directory.CreateDirectory(day);
        try
        {
            static string Line(double used) =>
                """{"timestamp":"2026-10-11T07:05:00.123Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":"""
                + used + ""","window_minutes":10080,"resets_at":1792327796},"secondary":null,"plan_type":"prolite"}}}""";
            var older = Path.Combine(day, "rollout-a.jsonl");
            File.WriteAllText(older, Line(50) + "\n");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
            File.WriteAllText(Path.Combine(day, "rollout-b.jsonl"), Line(1) + "\n" + Line(2) + "\n{\"type\":\"other\"}\n");

            var usage = new CodexUsageReader(home).Read();
            Assert.NotNull(usage);
            Assert.Equal(2, usage.Tightest!.UsedPercent);
            Assert.Equal("prolite", usage.PlanType);
            Assert.Equal(new DateTimeOffset(2026, 10, 11, 7, 5, 0, 123, TimeSpan.Zero), usage.FetchedAt);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}

public sealed class ClaudeUsageTests
{
    [Fact]
    public void CachedUsageIsIgnoredForAnotherAccount()
    {
        var config = JsonNode.Parse("""
            {"oauthAccount":{"accountUuid":"a","organizationType":"claude_max"},
             "cachedUsageUtilization":{"accountUuid":"a","fetchedAtMs":1700000000000,"utilization":{
               "five_hour":{"utilization":40,"resets_at":"2026-10-15T12:00:00.5+00:00"},
               "seven_day":{"utilization":10,"resets_at":null}}}}
            """)!;
        var usage = ClaudeUsageReader.Decode(config);
        Assert.NotNull(usage);
        Assert.Equal("max", usage.PlanType);
        Assert.Equal(60, usage.FiveHour!.RemainingPercent(DateTimeOffset.FromUnixTimeSeconds(0)));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 0, 0, 500, TimeSpan.Zero), usage.FiveHour.ResetDate);
        Assert.Null(usage.Weekly!.ResetDate);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), usage.FetchedAt);

        config["oauthAccount"]!["accountUuid"] = "b";
        Assert.Null(ClaudeUsageReader.Decode(config));
    }
}

public sealed class TrayIconTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void IconDrawsAtEveryScale(int size)
    {
        using var mark = TrayIconRenderer.Mark(size);
        Assert.Equal(size, mark.Width);
        Assert.Equal(size, mark.Height);
        Assert.True(Inked(mark));
    }

    [Fact]
    public void RingsDrawEveryPercentage()
    {
        foreach (var percent in new double?[] { null, 0, 7, 42, 100 })
        {
            foreach (var gauge in new[] { RingGauge.Claude(percent, 60), RingGauge.Codex(percent, Color.Black) })
            {
                using var bitmap = new Bitmap((int)RingGauge.Diameter * 2, (int)RingGauge.Diameter * 2);
                using (var graphics = Graphics.FromImage(bitmap)) gauge.Draw(graphics, 0, 0, 2, Color.Black);
                Assert.True(Inked(bitmap));
            }
        }
    }

    static bool Inked(Bitmap bitmap)
    {
        for (var x = 0; x < bitmap.Width; x++)
            for (var y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(x, y).A > 0) return true;
        return false;
    }

    [Fact]
    public void RelativeTimesUseTheTwoLargestUnits()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        Assert.Equal("in 3 hours 5 minutes", Format.Relative(now.AddMinutes(185), now));
        Assert.Equal("in 2 days 1 hour", Format.Relative(now.AddHours(49), now));
        Assert.Equal("in 1 minute", Format.Relative(now.AddSeconds(61), now));
        Assert.Equal("5 minutes ago", Format.Relative(now.AddMinutes(-5), now));
        Assert.Equal("just now", Format.Relative(now.AddSeconds(-3), now));
    }
}
