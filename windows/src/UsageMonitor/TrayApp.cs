using System.Diagnostics;
using Microsoft.Win32;

namespace UsageMonitor;

sealed class TrayApp : ApplicationContext
{
    readonly CodexUsageReader codexReader = new();
    readonly ClaudeUsageReader claudeReader = new();
    TrayItem? trayItem;
    readonly FloatingWindow floating = new();
    readonly UsagePanel panel = new();
    readonly TrayMenu menu = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 30_000 };
    readonly CancellationTokenSource stopping = new();
    bool codexShown;
    CodexUsage? codexUsage;
    CodexUsage? codexLive;
    DateTimeOffset lastCodexFetch = DateTimeOffset.MinValue;
    string? codexCli;
    /// Deliberately not persisted: the user opts in each launch, like Claude's credential access.
    bool codexLiveEnabled;
    bool codexLiveFailed;
    bool codexFetching;
    bool claudeFetching;
    string? claudeCli;
    ClaudeUsage? claudeUsage;
    ClaudeUsage? claudeLive;
    DateTimeOffset lastClaudeFetch = DateTimeOffset.MinValue;
    ClaudeUsageMode claudeMode = ClaudeUsageMode.Disabled;
    bool claudeStatusLineInstalled;
    string? claudeLiveProblem;
    string? loggedUsage;
    AvailableUpdate? availableUpdate;
    DateTimeOffset lastUpdateCheck = DateTimeOffset.MinValue;
    bool updateChecking;
    bool updateDownloading;

    /// Set when the user chose to update; Program starts it once the app has fully exited.
    public string? PendingInstaller { get; private set; }

    bool ClaudeShown => claudeMode != ClaudeUsageMode.Disabled;

    public bool Start()
    {
        var codexHome = CodexUsageReader.DefaultCodexHome;
        var claudeConfig = ClaudeUsageReader.DefaultConfigFile;
        if (Directory.Exists(codexHome))
        {
            codexShown = true;
            codexCli = CommandLineTool.Find("codex");
            Log.Write($"Codex found; CLI {codexCli ?? "not found"}");
        }
        var claudeInstalled = File.Exists(claudeConfig) || Directory.Exists(ClaudeStatusLine.ConfigDirectory);
        if (!codexShown && !claudeInstalled)
        {
            Dialogs.Error("Neither Codex nor Claude Code was found",
                $"Usage Monitor shows the limits Codex saves in {Tilde(codexHome)} and Claude Code saves in {Tilde(claudeConfig)}. "
                + "Install and run one of them, then open Usage Monitor again.");
            return false;
        }

        if (claudeInstalled)
        {
            claudeCli = CommandLineTool.Find("claude");
            UsagePipe.Listen(ClaudeStatusLine.ConfigDirectory, ReceiveClaudeUsage, stopping.Token);
            var integration = new ClaudeStatusLine(ClaudeStatusLine.ConfigDirectory, Environment.ProcessPath!);
            claudeMode = integration.Configure(
                () => Dialogs.Ask("Replace your Claude status line?",
                    "You already have a custom Claude status line. Replace it with Usage Monitor’s usage indicators?",
                    "Replace Status Line", "Keep Existing"),
                AskForClaudeCredentials);
            claudeStatusLineInstalled = claudeMode == ClaudeUsageMode.StatusLine;
            Log.Write($"Claude Code found; usage via {claudeMode}, status line runs {integration.Command ?? "nothing"}");
        }
        if (!codexShown && !ClaudeShown) return false;

        trayItem = new TrayItem(ClickTray);
        floating.Clicked += () => TogglePanel(floating.Bounds);
        Refresh();
        if (Settings.Get("FloatingHidden") != 1) floating.Show();
        FetchClaudeIfDue(TimeSpan.Zero);
        CheckForUpdateIfDue(TimeSpan.Zero);
        timer.Tick += (_, _) =>
        {
            Refresh();
            FetchClaudeIfDue(TimeSpan.FromMinutes(5)); // the usage endpoint rate-limits
            FetchCodexIfDue(TimeSpan.FromMinutes(5));
            CheckForUpdateIfDue(TimeSpan.FromHours(24));
        };
        timer.Start();
        SystemEvents.UserPreferenceChanged += Redraw;
        SystemEvents.DisplaySettingsChanged += Redraw;
        return true;
    }

    void Redraw(object? sender, EventArgs e) => Refresh();

    void Refresh()
    {
        var gauges = new List<RingGauge>();
        var tips = new List<string>();
        if (ClaudeShown)
        {
            claudeUsage = claudeMode == ClaudeUsageMode.StatusLine
                ? claudeLive
                : Newest(claudeLive, claudeReader.Read(), u => u.FetchedAt);
            gauges.Add(RingGauge.Claude(claudeUsage?.FiveHour?.RemainingPercent(), claudeUsage?.Weekly?.RemainingPercent()));
            var parts = claudeUsage?.Windows.Select(w => $"{w.Label} {Format.Percent(w.RemainingPercent())}% left");
            tips.Add(parts is null ? "Claude Code usage unavailable" : "Claude Code: " + string.Join(" · ", parts));
        }
        if (codexShown)
        {
            codexUsage = Newest(codexLive, codexReader.Read(), u => u.FetchedAt);
            var remaining = codexUsage?.Tightest?.RemainingPercent();
            gauges.Add(RingGauge.Codex(remaining, AppTheme.Text));
            tips.Add(remaining is double r ? $"Codex: {Format.Percent(r)}% left" : "Codex usage unavailable");
        }
        floating.SetGauges(gauges, string.Join("\n", tips));
        var size = TrayIconRenderer.IconSize();
        trayItem?.Update($"{size} {TrayIconRenderer.ThemeKey}", () => TrayIconRenderer.Mark(size), string.Join("\n", tips));
        if (panel.Visible) FillPanel();

        var summary = string.Join(" | ", tips).Replace(" · ", ", ");
        if (summary != loggedUsage) Log.Write(summary);
        loggedUsage = summary;
    }

    static T? Newest<T>(T? a, T? b, Func<T, DateTimeOffset?> date) where T : class =>
        a is null ? b : b is null ? a : (date(b) ?? DateTimeOffset.MinValue) > (date(a) ?? DateTimeOffset.MinValue) ? b : a;

    async void FetchClaudeIfDue(TimeSpan interval)
    {
        if (claudeMode != ClaudeUsageMode.Credentials || claudeFetching
            || DateTimeOffset.Now - lastClaudeFetch < interval) return;
        lastClaudeFetch = DateTimeOffset.Now;
        claudeFetching = true;
        try
        {
            var (status, usage) = await FetchClaudeRenewingLogin();
            Log.Write($"Claude live usage: {status}");
            switch (status)
            {
                case ClaudeFetch.Usage:
                    claudeLive = usage;
                    claudeLiveProblem = null;
                    break;
                case ClaudeFetch.Expired:
                    claudeLiveProblem = "Claude Code login expired";
                    break;
                default:
                    claudeLiveProblem = "Couldn’t get live usage";
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Write($"Claude live usage failed: {e.Message}");
            claudeLiveProblem = "Couldn’t get live usage";
        }
        finally
        {
            claudeFetching = false;
        }
        Refresh();
    }

    /// Claude Code renews its own expired login whenever it runs.
    async Task<(ClaudeFetch, ClaudeUsage?)> FetchClaudeRenewingLogin()
    {
        var result = await ClaudeUsageApi.FetchAsync();
        if (result.Status != ClaudeFetch.Expired || claudeCli is null) return result;
        await CommandLineTool.RunAsync(claudeCli, "auth status");
        return await ClaudeUsageApi.FetchAsync();
    }

    async void CheckForUpdateIfDue(TimeSpan interval)
    {
        if (!AppLocation.IsPublished || updateChecking || updateDownloading
            || DateTimeOffset.Now - lastUpdateCheck < interval) return;
        lastUpdateCheck = DateTimeOffset.Now;
        updateChecking = true;
        try
        {
            var update = await UpdateCheck.CheckAsync();
            Log.Write(update is null ? "No update available" : $"Update available: v{update.Version}");
            if (update is not null) availableUpdate = update;
        }
        catch (Exception e)
        {
            Log.Write($"Update check failed: {e.Message}");
            lastUpdateCheck = DateTimeOffset.Now - TimeSpan.FromHours(24) + TimeSpan.FromMinutes(15);
        }
        finally
        {
            updateChecking = false;
        }
        Refresh();
    }

    async void StartUpdate()
    {
        if (availableUpdate is null || updateDownloading) return;
        updateDownloading = true;
        Refresh();
        string? path = null;
        var problem = "Couldn’t reach GitHub. Try again later.";
        try
        {
            // Checks again so a release published since the last check is the one installed.
            availableUpdate = await UpdateCheck.CheckAsync();
            if (availableUpdate is null) problem = "You’re already on the latest version.";
            else
            {
                problem = "The download failed. Try again later.";
                path = await UpdateCheck.DownloadAsync(availableUpdate, stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
        catch (Exception e) { Log.Write($"Update failed: {e.Message}"); }
        updateDownloading = false;
        if (path is null || availableUpdate is null)
        {
            Refresh();
            Dialogs.Error("Couldn’t update Usage Monitor", problem);
            return;
        }
        Log.Write($"Update v{availableUpdate.Version} downloaded; quitting to install");
        PendingInstaller = path;
        Quit();
    }

    async void FetchCodexIfDue(TimeSpan interval, bool onlyIfStale = true)
    {
        var now = DateTimeOffset.Now;
        if (!codexLiveEnabled || codexFetching || codexCli is null || now - lastCodexFetch < interval
            || (onlyIfStale && now - (codexUsage?.FetchedAt ?? DateTimeOffset.MinValue) < interval)) return;
        lastCodexFetch = now;
        codexFetching = true;
        try
        {
            var usage = await CodexUsageApi.FetchAsync(codexCli);
            Log.Write(usage is null ? "Codex live usage failed" : "Codex live usage received");
            if (codexLiveEnabled)
            {
                if (usage is not null) codexLive = usage;
                codexLiveFailed = usage is null;
            }
        }
        finally
        {
            codexFetching = false;
        }
        Refresh();
    }

    void ToggleCodexLive()
    {
        codexLiveEnabled = !codexLiveEnabled;
        if (codexLiveEnabled)
        {
            FetchCodexIfDue(TimeSpan.Zero, onlyIfStale: false);
            if (panel.Visible) FillPanel();
        }
        else
        {
            codexLive = null;
            codexLiveFailed = false;
            Refresh();
        }
    }

    bool AskForClaudeCredentials(Exception? error)
    {
        var reason = error is null ? "" : error.Message + " ";
        var declining = claudeStatusLineInstalled
            ? "Declining keeps using the status line."
            : "Declining disables Claude usage for this launch.";
        return Dialogs.Ask("Allow access to your Claude login?",
            reason + $"Usage Monitor can read your Claude login from {Tilde(ClaudeUsageApi.CredentialsFile)} "
            + "and request usage from Anthropic every five minutes. " + declining,
            "Allow Credential Access", claudeStatusLineInstalled ? "Keep Status Line" : "Disable Claude");
    }

    void ReceiveClaudeUsage(string json)
    {
        if (claudeMode != ClaudeUsageMode.StatusLine || ClaudeStatusLine.UsageFromStatusLine(json) is not { } usage) return;
        Log.Write("Claude usage arrived from the status line");
        claudeLive = usage;
        Refresh();
    }

    void UseClaudeCredentials()
    {
        if (!AskForClaudeCredentials(null)) return;
        claudeMode = ClaudeUsageMode.Credentials;
        FetchClaudeIfDue(TimeSpan.Zero);
    }

    void ClickTray(MouseButtons button)
    {
        var cursor = new Rectangle(Cursor.Position, new Size(1, 1));
        if (button == MouseButtons.Left && !floating.Visible) TogglePanel(cursor);
        else ToggleMenu(cursor);
    }

    void ToggleMenu(Rectangle anchor)
    {
        if (menu.Visible)
        {
            menu.Hide();
            return;
        }
        if (menu.JustHidden) return;
        panel.Hide();
        var entries = new List<(string, Action?)?>();
        if (floating.Visible)
        {
            entries.Add(("Hide Floating Window", () => SetFloatingVisible(false)));
        }
        else
        {
            entries.Add(("Show Usage", () => TogglePanel(anchor)));
            entries.Add(("Show Floating Window", () => SetFloatingVisible(true)));
        }
        if (updateDownloading || availableUpdate is not null) entries.Add(null);
        if (updateDownloading) entries.Add(("Downloading update…", null));
        else if (availableUpdate is { } update) entries.Add(($"Update to v{update.Version}", StartUpdate));
        entries.Add(null);
        entries.Add(("Quit", Quit));
        menu.Fill(entries);
        menu.ShowNear(anchor);
    }

    void TogglePanel(Rectangle anchor)
    {
        if (panel.Visible)
        {
            panel.Hide();
            return;
        }
        if (panel.JustHidden) return;
        Refresh();
        FetchCodexIfDue(TimeSpan.FromMinutes(1), onlyIfStale: false);
        FetchClaudeIfDue(TimeSpan.FromMinutes(1));
        FillPanel();
        panel.ShowNear(anchor);
    }

    void SetFloatingVisible(bool visible)
    {
        Log.Write(visible ? "Floating window shown" : "Floating window hidden");
        panel.Hide();
        Settings.Set("FloatingHidden", visible ? 0 : 1);
        if (visible)
        {
            floating.Show();
            return;
        }
        floating.Hide();
        ShowHiddenHint();
    }

    void FillPanel()
    {
        var sections = new List<IReadOnlyList<PanelItem>>();
        if (ClaudeShown) sections.Add(ClaudeSection());
        if (codexShown) sections.Add(CodexSection());

        var footer = new List<PanelItem>();
        if (AppLocation.IsPublished)
        {
            footer.Add(new PanelAction("Launch at Login", () =>
            {
                try { LaunchAtLogin.Enabled = !LaunchAtLogin.Enabled; }
                catch (Exception e) { Dialogs.Error("Couldn’t change Launch at Login", e.Message); }
                if (panel.Visible) FillPanel();
            }, LaunchAtLogin.Enabled));
        }
        footer.Add(floating.Visible
            ? new PanelAction("Hide Floating Window", () => SetFloatingVisible(false))
            : new PanelAction("Show Floating Window", () => SetFloatingVisible(true)));
        if (updateDownloading) footer.Add(new PanelAction("Downloading update…", null));
        else if (availableUpdate is { } update) footer.Add(new PanelAction($"Update to v{update.Version}", StartUpdate));
        footer.Add(new PanelAction("Quit", Quit));
        panel.Fill(sections, footer);
    }

    List<PanelItem> CodexSection()
    {
        var items = new List<PanelItem>();
        if (codexUsage is { } usage)
        {
            items.Add(new PanelHeader(usage.PlanType is string plan ? $"Codex ({plan})" : "Codex"));
            var showingLive = codexLiveEnabled && !codexLiveFailed && codexLive is not null && usage.FetchedAt == codexLive.FetchedAt;
            AddUsage(items, usage.Windows, showingLive ? null : usage.FetchedAt,
                showingLive ? null : "Updates only while you chat in Codex locally",
                codexLive?.BankedResets is { } resets ? BankedResetItems(resets) : []);
        }
        else
        {
            items.Add(new PanelHeader("Codex"));
            items.Add(new PanelNote("No Codex usage found"));
            items.Add(new PanelNote("Run Codex once to record your limits"));
        }
        if (codexCli is not null)
        {
            items.Add(new PanelGap());
            if (codexLiveEnabled && codexLiveFailed)
            {
                items.Add(new PanelNote("Couldn’t get live usage"));
                items.Add(new PanelNote("Run “codex login status” in a terminal to check your login"));
            }
            items.Add(new PanelAction("Use Background Codex for Live Usage", ToggleCodexLive, codexLiveEnabled));
        }
        items.Add(new PanelGap());
        items.Add(new PanelAction("Did Tibo reset today?", () => Open("https://x.com/thsottiaux"), IsLink: true));
        return items;
    }

    List<PanelItem> ClaudeSection()
    {
        var items = new List<PanelItem>();
        if (claudeUsage is { } usage)
        {
            items.Add(new PanelHeader(usage.PlanType is string plan ? $"Claude Code ({plan})" : "Claude Code"));
            var showingLive = claudeMode == ClaudeUsageMode.Credentials && claudeLiveProblem is null
                && claudeLive is not null && usage.FetchedAt == claudeLive.FetchedAt;
            AddUsage(items, usage.Windows, showingLive ? null : usage.FetchedAt,
                claudeMode == ClaudeUsageMode.StatusLine ? "Updates only while you chat in the CLI" : null, []);
        }
        else
        {
            items.Add(new PanelHeader("Claude Code"));
            items.Add(new PanelNote("No Claude Code usage found"));
            items.Add(new PanelNote(claudeMode == ClaudeUsageMode.StatusLine
                ? "Chat with Claude Code CLI to load usage"
                : "Waiting for usage from Anthropic"));
        }
        if (claudeMode == ClaudeUsageMode.StatusLine)
        {
            items.Add(new PanelGap());
            items.Add(new PanelAction("Use Credential Access for Live Usage…", UseClaudeCredentials));
        }
        else if (claudeLiveProblem is not null)
        {
            items.Add(new PanelGap());
            items.Add(new PanelNote(claudeLiveProblem));
            items.Add(new PanelNote("Run /status in Claude Code to check your login, then check again"));
            items.Add(new PanelAction("Check Again", () => FetchClaudeIfDue(TimeSpan.Zero)));
        }
        return items;
    }

    static void AddUsage(List<PanelItem> items, IReadOnlyList<LimitWindow> windows, DateTimeOffset? fetchedAt,
                         string? hint, IReadOnlyList<PanelItem> extraItems)
    {
        var now = DateTimeOffset.Now;
        foreach (var window in windows)
        {
            items.Add(new PanelLimit(window.Label, window.RemainingPercent(),
                window.ResetDate is { } reset && reset > now ? $"Resets {Format.Relative(reset)} · {Format.Absolute(reset)}" : null));
        }
        if (extraItems.Count > 0) items.Add(new PanelGap());
        items.AddRange(extraItems);
        if (fetchedAt is not null || hint is not null) items.Add(new PanelGap());
        if (fetchedAt is { } fetched)
            items.Add(new PanelNote($"Updated {Format.Relative(fetched < now ? fetched : now)}"));
        if (hint is not null) items.Add(new PanelNote(hint));
    }

    static List<PanelItem> BankedResetItems(BankedResets resets)
    {
        var now = DateTimeOffset.Now;
        var expirations = resets.Expirations.Where(date => (date ?? DateTimeOffset.MaxValue) > now).ToList();
        var count = Math.Max(0, resets.Count - (resets.Expirations.Count - expirations.Count));
        var items = new List<PanelItem> { new PanelNote(count == 0 ? "No banked resets" : $"Banked resets: {count}") };
        foreach (var expiry in expirations.Take(count))
        {
            items.Add(new PanelNote(expiry is { } date ? $"Expires {Format.Relative(date)} · {Format.Absolute(date)}" : "Never expires"));
        }
        var unlisted = count - Math.Min(count, expirations.Count);
        if (unlisted > 0) items.Add(new PanelNote($"{unlisted} more, expiry unknown"));
        return items;
    }

    static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    static string Tilde(string path) =>
        path.StartsWith(Paths.Home, StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" + path[Paths.Home.Length..] : path;

    /// Windows initially places new tray icons behind the overflow arrow.
    void ShowHiddenHint()
    {
        if (Settings.Get("ShownTrayHint") is int shown && shown != 0) return;
        Settings.Set("ShownTrayHint", 1);
        trayItem?.ShowHint("Usage Monitor is still running",
            "Click its icon in the notification area to see usage or bring the floating window back. If you don’t see it, "
            + "open the ^ arrow, or turn it on in Settings › Personalization › Taskbar › Other system tray icons.");
    }

    public void Quit()
    {
        Log.Write("Quitting");
        timer.Stop();
        stopping.Cancel();
        panel.Hide();
        menu.Hide();
        floating.Hide();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= Redraw;
            SystemEvents.DisplaySettingsChanged -= Redraw;
            trayItem?.Dispose();
            floating.Dispose();
            panel.Dispose();
            menu.Dispose();
            timer.Dispose();
            stopping.Dispose();
        }
        base.Dispose(disposing);
    }
}

sealed class TrayItem : IDisposable
{
    readonly NotifyIcon icon = new();
    IntPtr handle;
    string? key;

    public TrayItem(Action<MouseButtons> click)
    {
        icon.MouseUp += (_, e) => click(e.Button);
    }

    public void Update(string key, Func<Bitmap> draw, string tooltip)
    {
        icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (key == this.key) return;
        this.key = key;
        using var bitmap = draw();
        var newHandle = bitmap.GetHicon();
        var old = icon.Icon;
        icon.Icon = Icon.FromHandle(newHandle);
        old?.Dispose();
        if (handle != 0) Native.DestroyIcon(handle);
        handle = newHandle;
        icon.Visible = true;
    }

    public void ShowHint(string title, string text) => icon.ShowBalloonTip(10_000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        if (handle != 0) Native.DestroyIcon(handle);
        handle = 0;
    }
}

static class Dialogs
{
    public static bool Ask(string title, string detail, string accept, string decline)
    {
        var yes = new TaskDialogButton(accept);
        var no = new TaskDialogButton(decline);
        var page = new TaskDialogPage
        {
            Caption = "Usage Monitor",
            Heading = title,
            Text = detail,
            Buttons = { yes, no },
            DefaultButton = yes,
        };
        return TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) == yes;
    }

    public static void Error(string title, string detail) =>
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = "Usage Monitor",
            Heading = title,
            Text = detail,
            Icon = TaskDialogIcon.Warning,
            Buttons = { TaskDialogButton.OK },
        }, TaskDialogStartupLocation.CenterScreen);
}
