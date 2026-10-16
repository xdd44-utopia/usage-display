using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace UsageMonitor.Tests;

[Collection("Claude config")]
public sealed class ClaudeStatusLineTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly ClaudeStatusLine integration;
    string Settings => Path.Combine(directory, "settings.json");

    public ClaudeStatusLineTests()
    {
        Directory.CreateDirectory(directory);
        integration = new ClaudeStatusLine(directory, Path.Combine(directory, "UsageMonitor.exe"));
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    JsonNode Saved() => JsonNode.Parse(File.ReadAllText(Settings))!;

    [Fact]
    public void InstallationPreservesOtherSettingsAndRecognizesItselfOnRelaunch()
    {
        File.WriteAllText(Settings, """{"permissions":{"allow":["Read"]},"model":"sonnet"}""");
        for (var i = 0; i < 2; i++)
        {
            var mode = integration.Configure(
                () => throw new Exception("No replacement prompt for an empty slot or our own command"),
                _ => throw new Exception("No credential access for a working status line"));
            Assert.Equal(ClaudeUsageMode.StatusLine, mode);
        }
        var saved = Saved();
        Assert.Equal("sonnet", (string?)saved["model"]);
        Assert.Equal("Read", (string?)saved["permissions"]!["allow"]![0]);
        Assert.Equal(integration.Command, (string?)saved["statusLine"]!["command"]);
        Assert.Equal("command", (string?)saved["statusLine"]!["type"]);
    }

    [Fact]
    public void MovedAppRewritesItsOwnStatusLineWithoutAsking()
    {
        var moved = new ClaudeStatusLine(directory, @"C:\Somewhere\Else\UsageMonitor.exe");
        File.WriteAllText(Settings, new JsonObject
        {
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = moved.Command },
        }.ToJsonString());
        var mode = integration.Configure(
            () => throw new Exception("A moved copy of our own command is not a custom status line"), _ => false);
        Assert.Equal(ClaudeUsageMode.StatusLine, mode);
        Assert.Equal(integration.Command, (string?)Saved()["statusLine"]!["command"]);
    }

    [Fact]
    public void UninstallRemovesOnlyThisCopysStatusLine()
    {
        File.WriteAllText(Settings, """{"model":"sonnet"}""");
        integration.Configure(() => true, _ => false);
        integration.Remove();
        Assert.Null(Saved()["statusLine"]);
        Assert.Equal("sonnet", (string?)Saved()["model"]);

        var other = new ClaudeStatusLine(directory, @"C:\Somewhere\Else\UsageMonitor.exe");
        foreach (var command in new[] { "existing-script", other.Command! })
        {
            var original = new JsonObject { ["statusLine"] = new JsonObject { ["command"] = command } }.ToJsonString();
            File.WriteAllText(Settings, original);
            integration.Remove();
            Assert.Equal(original, File.ReadAllText(Settings));
        }
    }

    [Fact]
    public void ExistingStatusLineFollowsConsentAndIsUntouchedWhenKept()
    {
        const string original = """{"statusLine":{"type":"command","command":"existing-script","padding":2}}""";
        foreach (var replace in new[] { false, true })
        {
            foreach (var credentials in new[] { false, true })
            {
                File.WriteAllText(Settings, original);
                var prompts = new List<string>();
                var mode = integration.Configure(() =>
                {
                    prompts.Add("replace");
                    return replace;
                }, error =>
                {
                    Assert.Null(error);
                    prompts.Add("credentials");
                    return credentials;
                });
                Assert.Equal(replace ? ClaudeUsageMode.StatusLine
                    : credentials ? ClaudeUsageMode.Credentials : ClaudeUsageMode.Disabled, mode);
                Assert.Equal(replace ? ["replace"] : new[] { "replace", "credentials" }, prompts);
                if (!replace) Assert.Equal(original, File.ReadAllText(Settings));
            }
        }
    }

    [Fact]
    public void UnreadableDisabledAndConcurrentlyChangedSettingsArePreserved()
    {
        foreach (var original in new[] { "{broken", "[]", """{"disableAllHooks":true}""" })
        {
            File.WriteAllText(Settings, original);
            var offeredFallback = false;
            var mode = integration.Configure(
                () => throw new Exception("Must not offer replacement when settings cannot be safely used"),
                error =>
                {
                    Assert.NotNull(error);
                    offeredFallback = true;
                    return false;
                });
            Assert.Equal(ClaudeUsageMode.Disabled, mode);
            Assert.True(offeredFallback);
            Assert.Equal(original, File.ReadAllText(Settings));
        }

        File.WriteAllText(Settings, """{"statusLine":{"command":"existing"}}""");
        const string changed = """{"statusLine":{"command":"new-user-choice"}}""";
        var result = integration.Configure(() =>
        {
            File.WriteAllText(Settings, changed);
            return true;
        }, error =>
        {
            Assert.NotNull(error);
            return true;
        });
        Assert.Equal(ClaudeUsageMode.Credentials, result);
        Assert.Equal(changed, File.ReadAllText(Settings));
    }

    [Fact]
    public void MissingSettingsAreCreated()
    {
        Assert.Equal(ClaudeUsageMode.StatusLine, integration.Configure(() => false, _ => false));
        Assert.Equal(integration.Command, (string?)Saved()["statusLine"]!["command"]);
    }

    [Fact]
    public void CommandIsABareForwardSlashPathOrNothing()
    {
        Assert.Equal("C:/Users/me/AppData/Local/Programs/UsageMonitor/UsageMonitor.exe --claude-status-line",
            new ClaudeStatusLine(directory, @"C:\Users\me\AppData\Local\Programs\UsageMonitor\UsageMonitor.exe").Command);
        // Doesn't exist, so has no short name to fall back to.
        Assert.Null(new ClaudeStatusLine(directory, @"C:\No Such\User's App $(echo surprise)\UsageMonitor.exe").Command);
        Assert.Equal(ClaudeUsageMode.Credentials, new ClaudeStatusLine(directory, @"C:\No Such\App.exe")
            .Configure(() => false, error => error is not null));
        Assert.False(File.Exists(Settings));
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("powershell")]
    public void StatusLineProcessDeliversOnlyQuotaDataToTheApp(string shell)
    {
        var app = new ClaudeStatusLine(directory, Path.Combine(AppContext.BaseDirectory, "UsageMonitor.exe"));
        Assert.NotNull(app.Command);
        var info = shell == "bash"
            ? new ProcessStartInfo(GitBash() ?? "bash.exe", ["-c", app.Command!])
            : new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", app.Command!]);
        if (shell == "bash" && GitBash() is null) return; // Git Bash isn't installed
        // Elevated, the pipe is owned by Administrators but Git Bash children claim the user as owner,
        // so the app's same-owner check refuses to send. CI runners are elevated.
        if (shell == "bash" && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return;
        info.RedirectStandardInput = info.RedirectStandardOutput = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.Environment["CLAUDE_CONFIG_DIR"] = directory;

        string? received = null;
        using var gotIt = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        UsagePipe.Listen(directory, json =>
        {
            received = json;
            gotIt.Set();
        }, stop.Token);
        SynchronizationContext.SetSynchronizationContext(previous);
        Thread.Sleep(200); // let the listener create its pipe

        using var process = Process.Start(info)!;
        var reset = DateTimeOffset.Now.ToUnixTimeSeconds() + 3600;
        process.StandardInput.Write("""{"cwd":"private-project","rate_limits":{"five_hour":{"used_percentage":25,"resets_at":""" + reset + "}}}");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(15_000));
        stop.Cancel();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("5-hour: 75% left", output.TrimEnd());
        Assert.True(gotIt.Wait(5_000));
        Assert.DoesNotContain("private-project", received);
        var usage = ClaudeStatusLine.UsageFromStatusLine(received!);
        Assert.Equal(25, usage?.FiveHour?.UsedPercent);
        Assert.Null(usage?.Weekly);
        Assert.False(File.Exists(Settings));
    }

    static string? GitBash() =>
        new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe" }.FirstOrDefault(File.Exists);

    [Fact]
    public void QuotaParsingHandlesPartialMissingExpiredAndInvalidWindows()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        (string Json, int Count)[] cases =
        [
            ("""{"rate_limits":{"five_hour":{"used_percentage":25,"resets_at":2000},"seven_day":{"used_percentage":80,"resets_at":3000}}}""", 2),
            ("""{"rate_limits":{"seven_day":{"used_percentage":80,"resets_at":3000}}}""", 1),
            ("""{"rate_limits":{"five_hour":{"used_percentage":101,"resets_at":2000},"seven_day":{"used_percentage":80,"resets_at":3000}}}""", 1),
            ("""{"rate_limits":{"five_hour":{"used_percentage":25,"resets_at":1000}}}""", 0),
            ("""{"rate_limits":{"five_hour":{"used_percentage":-1,"resets_at":2000}}}""", 0),
            ("""{"rate_limits":{"five_hour":{"resets_at":2000}}}""", 0),
            ("""{"context_window":{"used_percentage":25}}""", 0),
            ("""{"rate_limits":null}""", 0),
            ("{broken", 0),
        ];
        foreach (var (json, count) in cases)
        {
            var usage = ClaudeStatusLine.UsageFromStatusLine(json, now);
            Assert.Equal(count, usage?.Windows.Count ?? 0);
            if (count == 2)
            {
                Assert.Equal(75, usage!.FiveHour!.RemainingPercent(now));
                Assert.Equal(20, usage.Weekly!.RemainingPercent(now));
                Assert.Equal(now, usage.FetchedAt);
            }
        }
    }
}
