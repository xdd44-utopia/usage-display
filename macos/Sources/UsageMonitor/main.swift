import AppKit
import ServiceManagement

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let codexReader = CodexUsageReader()
    private let claudeReader = ClaudeUsageReader()
    private var codexItem: NSStatusItem?
    private var claudeItem: NSStatusItem?
    private var timer: Timer?
    private var codexUsage: CodexUsage?
    private var codexLive: CodexUsage?
    private var lastCodexFetch = Date.distantPast
    private var codexCLI: URL?
    private var codexLiveEnabled = false
    private var codexLiveFailed = false
    private var codexFetching = false
    private var claudeFetching = false
    private var claudeInstalled = false
    private var claudeCLI: URL?
    private var claudeUsage: ClaudeUsage?
    private var claudeLive: ClaudeUsage?
    private var claudeStatusLineUsage: ClaudeUsage?
    private var lastClaudeFetch = Date.distantPast
    private var claudeCredentials = false
    private var claudeStatusLineInstalled = false
    private var claudeLiveProblem: String?
    private var updater: Updater?

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let bundle = AppLocation.misplacedBundle,
           ask("Move Usage Monitor to Applications?",
               detail: "It’s running from the disk image or a temporary copy, so Claude’s status line will stop working once that’s gone. "
                   + "You can also drag it anywhere in Finder and open it from there.",
               accept: "Move to Applications", decline: "Not Now") {
            do {
                AppLocation.relaunch(at: try AppLocation.moveToApplications(bundle))
                return
            } catch {
                NSAlert(error: error).runModal()
            }
        }
        let codexHome = CodexUsageReader.defaultCodexHome
        let claudeConfig = ClaudeUsageReader.defaultConfigFile
        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: codexHome.path) {
            codexItem = makeItem(length: NSStatusItem.variableLength)
            codexCLI = CommandLineTool.find("codex")
        }
        claudeInstalled = fileManager.fileExists(atPath: claudeConfig.path)
            || fileManager.fileExists(atPath: ClaudeStatusLine.configDirectory.path)
        guard codexItem != nil || claudeInstalled else {
            NSApp.activate(ignoringOtherApps: true)
            let alert = NSAlert()
            alert.messageText = "Neither Codex nor Claude Code was found"
            alert.informativeText = "Usage Monitor shows the limits Codex saves in \(Self.tilde(codexHome)) "
                + "and Claude Code saves in \(Self.tilde(claudeConfig)). "
                + "Install and run one of them, then open Usage Monitor again."
            alert.runModal()
            NSApp.terminate(nil)
            return
        }

        var claudeSetupError: Error?
        if claudeInstalled {
            claudeCLI = CommandLineTool.find("claude")
            DistributedNotificationCenter.default().addObserver(
                self, selector: #selector(receiveClaudeUsage(_:)), name: ClaudeStatusLine.notification,
                object: ClaudeStatusLine.configDirectory.standardizedFileURL.path,
                suspensionBehavior: .deliverImmediately)
            let integration = ClaudeStatusLine(directory: ClaudeStatusLine.configDirectory,
                                                executable: Bundle.main.executableURL!)
            do {
                claudeStatusLineInstalled = try integration.configure(askToReplace: {
                    self.ask("Replace your Claude status line?",
                             detail: "You already have a custom Claude status line. Replace it with Usage Monitor’s usage indicators?",
                             accept: "Replace Status Line", decline: "Keep Existing")
                })
            } catch {
                claudeSetupError = error
            }
        }
        let live = allowLiveUsage(setupError: claudeSetupError,
                                  retrying: codexItem == nil && !claudeStatusLineInstalled)
        codexLiveEnabled = live
        claudeCredentials = live && claudeInstalled
        if claudeCredentials || claudeStatusLineInstalled {
            claudeItem = makeItem(length: NSStatusItem.variableLength)
        }
        guard codexItem != nil || claudeItem != nil else {
            NSApp.terminate(nil)
            return
        }
        refresh()
        fetchClaudeIfDue(after: 0)
        fetchCodexIfDue(after: 0)
        updater = Updater.start()
        updater?.checkIfDue(after: 0)
        timer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { _ in
            MainActor.assumeIsolated {
                self.refresh()
                self.fetchClaudeIfDue(after: 5 * 60) // the usage endpoint rate-limits
                self.fetchCodexIfDue(after: 5 * 60)
                self.updater?.checkIfDue(after: 24 * 60 * 60)
            }
        }
        timer?.tolerance = 10
    }

    private func makeItem(length: CGFloat) -> NSStatusItem {
        let item = NSStatusBar.system.statusItem(withLength: length)
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        return item
    }

    private func refresh() {
        if let codexItem {
            codexUsage = [codexLive, codexReader.read()].compactMap { $0 }
                .max { ($0.fetchedAt ?? .distantPast) < ($1.fetchedAt ?? .distantPast) }
            let remaining = codexUsage?.tightest?.remainingPercent()
            codexItem.button?.image = TextGauge.codex(remainingPercent: remaining)
            codexItem.button?.toolTip = remaining.map { "Codex: \(Int($0.rounded()))% left" }
                ?? "Codex usage unavailable"
        }
        if let claudeItem {
            let sources = claudeCredentials ? [claudeLive, claudeStatusLineUsage, claudeReader.read()] : [claudeStatusLineUsage]
            claudeUsage = sources.compactMap { $0 }
                .max { ($0.fetchedAt ?? .distantPast) < ($1.fetchedAt ?? .distantPast) }
            // The status line doesn't report the plan.
            if let usage = claudeUsage, usage.planType == nil, let plan = claudeLive?.planType {
                claudeUsage = ClaudeUsage(fiveHour: usage.fiveHour, weekly: usage.weekly, planType: plan, fetchedAt: usage.fetchedAt)
            }
            claudeItem.button?.image = TextGauge.claude(
                fiveHourPercent: claudeUsage?.fiveHour?.remainingPercent(),
                weeklyPercent: claudeUsage?.weekly?.remainingPercent())
            let parts = claudeUsage?.windows.map { "\($0.label) \(Int($0.remainingPercent().rounded()))% left" }
            claudeItem.button?.toolTip = parts.map { "Claude Code: " + $0.joined(separator: " · ") }
                ?? "Claude Code usage unavailable"
        }
    }

    private func fetchClaudeIfDue(after interval: TimeInterval) {
        guard claudeItem != nil, claudeCredentials, !claudeFetching,
              Date().timeIntervalSince(lastClaudeFetch) >= interval else { return }
        lastClaudeFetch = Date()
        claudeFetching = true
        Task {
            defer { claudeFetching = false }
            switch await fetchClaudeRenewingLogin() {
            case .usage(let usage):
                claudeLive = usage
                claudeLiveProblem = nil
            case .expired:
                claudeLiveProblem = "Claude Code login expired"
            case .denied:
                claudeCredentials = false
                claudeLiveProblem = nil
                if !claudeStatusLineInstalled { disableClaude() }
            case .unavailable:
                claudeLiveProblem = "Couldn’t get live usage"
            }
            refresh()
        }
    }

    /// Claude Code renews its own expired login whenever it runs.
    private func fetchClaudeRenewingLogin() async -> ClaudeUsageAPI.Result {
        let result = await ClaudeUsageAPI.fetch()
        guard case .expired = result, let claudeCLI else { return result }
        await CommandLineTool.run(claudeCLI, arguments: ["auth", "status"])
        return await ClaudeUsageAPI.fetch()
    }

    private func fetchCodexIfDue(after interval: TimeInterval, onlyIfStale: Bool = true) {
        guard codexLiveEnabled, !codexFetching, let codexCLI, Date().timeIntervalSince(lastCodexFetch) >= interval,
              !onlyIfStale || Date().timeIntervalSince(codexUsage?.fetchedAt ?? .distantPast) >= interval
        else { return }
        lastCodexFetch = Date()
        codexFetching = true
        Task {
            defer { codexFetching = false }
            let usage = await CodexUsageAPI.fetch(executable: codexCLI)
            if let usage { codexLive = usage }
            codexLiveFailed = usage == nil
            refresh()
        }
    }

    private func ask(_ title: String, detail: String, accept: String, decline: String) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = detail
        alert.addButton(withTitle: accept)
        alert.addButton(withTitle: decline)
        return alert.runModal() == .alertFirstButtonReturn
    }

    /// Asked once for both tools and remembered, or again when a declined user retries; the local sources stay on
    /// either way as the backup.
    private func allowLiveUsage(setupError: Error?, retrying: Bool = false) -> Bool {
        let key = "LiveUsage"
        if let saved = UserDefaults.standard.object(forKey: key) as? Bool, saved || !retrying { return saved }
        let claude = claudeInstalled
        var uses: [String] = []
        if codexCLI != nil { uses.append("run Codex in the background to get current limits and banked resets from OpenAI") }
        if claude { uses.append("read your Claude login from the macOS Keychain to request usage from Anthropic") }
        guard !uses.isEmpty else { return false }
        let reason = setupError.map { "\($0.localizedDescription) " } ?? ""
        let declining = claude && !claudeStatusLineInstalled
            ? "Without it, Claude Code usage is hidden" + (codexItem != nil ? " and Codex usage updates only while you chat." : ".")
            : "Without it, usage updates only while you chat."
        let allowed = ask("Allow live usage?",
                          detail: reason + "Usage Monitor can \(uses.joined(separator: ", and ")), about every five minutes. " + declining,
                          accept: "Allow Live Usage", decline: "Use Local Only")
        UserDefaults.standard.set(allowed, forKey: key)
        return allowed
    }

    @objc private func useLiveUsage() {
        guard allowLiveUsage(setupError: nil, retrying: true) else { return }
        codexLiveEnabled = true
        if claudeInstalled {
            claudeCredentials = true
            if claudeItem == nil { claudeItem = makeItem(length: NSStatusItem.variableLength) }
        }
        fetchCodexIfDue(after: 0, onlyIfStale: false)
        fetchClaudeIfDue(after: 0)
        refresh()
    }

    @objc private func checkCodexAgain() {
        fetchCodexIfDue(after: 0, onlyIfStale: false)
    }

    private func disableClaude() {
        claudeLive = nil
        claudeUsage = nil
        if let claudeItem { NSStatusBar.system.removeStatusItem(claudeItem) }
        claudeItem = nil
        if codexItem == nil { NSApp.terminate(nil) }
    }

    @objc private func receiveClaudeUsage(_ notification: Notification) {
        guard claudeStatusLineInstalled, let json = notification.userInfo?["usage"] as? String,
              let usage = ClaudeStatusLine.usage(from: Data(json.utf8)) else { return }
        claudeStatusLineUsage = usage
        refresh()
    }

    @objc private func checkClaudeAgain() {
        fetchClaudeIfDue(after: 0)
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        refresh()
        if menu === claudeItem?.menu { fetchClaudeIfDue(after: 60) }
        if menu === codexItem?.menu { fetchCodexIfDue(after: 60, onlyIfStale: false) }
        menu.removeAllItems()

        if menu === codexItem?.menu {
            if let usage = codexUsage {
                menu.addItem(disabledItem(usage.planType.map { "Codex (\($0))" } ?? "Codex"))
                let showingLive = codexLiveEnabled && !codexLiveFailed && codexLive != nil
                    && usage.fetchedAt == codexLive?.fetchedAt
                addUsage(usage.windows, fetchedAt: showingLive ? nil : usage.fetchedAt,
                         hint: showingLive ? nil : "Update only while you chat in Codex locally", to: menu,
                         extraItems: codexLive?.bankedResets.map(bankedResetItems) ?? [])
            } else {
                menu.addItem(disabledItem("No Codex usage found"))
                menu.addItem(disabledItem("Run Codex once to record your limits"))
            }
            if codexCLI != nil && !codexLiveEnabled {
                menu.addItem(.separator())
                menu.addItem(useLiveUsageItem())
            } else if codexLiveEnabled && codexLiveFailed {
                menu.addItem(.separator())
                menu.addItem(disabledItem("Couldn’t get live usage"))
                menu.addItem(disabledItem("Run “codex login status” in a terminal to check your login, then check again"))
                let retry = NSMenuItem(title: "Check Again", action: #selector(checkCodexAgain), keyEquivalent: "")
                retry.target = self
                menu.addItem(retry)
            }
            menu.addItem(.separator())
            let profile = NSMenuItem(title: "Did Tibo reset today?", action: #selector(openProfile), keyEquivalent: "")
            profile.target = self
            menu.addItem(profile)
            menu.addItem(.separator())
        } else {
            if let usage = claudeUsage {
                menu.addItem(disabledItem(usage.planType.map { "Claude Code (\($0))" } ?? "Claude Code"))
                let liveWorking = claudeCredentials && claudeLiveProblem == nil
                let showingLive = liveWorking && claudeLive != nil && usage.fetchedAt == claudeLive?.fetchedAt
                addUsage(usage.windows, fetchedAt: showingLive ? nil : usage.fetchedAt,
                         hint: claudeStatusLineInstalled && !liveWorking ? "Update only while you chat in CLI" : nil, to: menu)
            } else {
                menu.addItem(disabledItem("No Claude Code usage found"))
                menu.addItem(disabledItem(claudeCredentials
                    ? "Waiting for usage from Anthropic"
                    : "Chat with Claude Code CLI to load usage"))
            }
            if !claudeCredentials {
                menu.addItem(.separator())
                menu.addItem(useLiveUsageItem())
            } else if let claudeLiveProblem {
                menu.addItem(.separator())
                menu.addItem(disabledItem(claudeLiveProblem))
                menu.addItem(disabledItem("Run /status in Claude Code to check your login, then check again"))
                let retry = NSMenuItem(title: "Check Again", action: #selector(checkClaudeAgain), keyEquivalent: "")
                retry.target = self
                menu.addItem(retry)
            }
            menu.addItem(.separator())
        }

        if Bundle.main.bundleIdentifier != nil {
            let login = NSMenuItem(title: "Launch at Login", action: #selector(toggleLogin), keyEquivalent: "")
            login.target = self
            login.state = SMAppService.mainApp.status == .enabled ? .on : .off
            menu.addItem(login)
        }
        if let updater, let version = updater.availableVersion {
            menu.addItem(.separator())
            let update = NSMenuItem(title: updater.isInstalling ? "Downloading update…" : "Update to v\(version)",
                                    action: updater.isInstalling ? nil : #selector(installUpdate), keyEquivalent: "")
            update.target = self
            menu.addItem(update)
            menu.addItem(.separator())
        }
        // A custom selector: macOS 26 auto-adds an icon to items using terminate(_:), misaligning the text.
        let quit = NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
    }

    private func addUsage(_ windows: [LimitWindow], fetchedAt: Date?, hint: String? = nil, to menu: NSMenu,
                          extraItems: [NSMenuItem] = []) {
        for window in windows {
            menu.addItem(.separator())
            menu.addItem(disabledItem("\(window.label): \(Int(window.remainingPercent().rounded()))% left"))
            if let reset = window.resetDate, reset > Date() {
                menu.addItem(disabledItem("Resets \(Self.relative(reset)) · \(Self.absolute(reset))"))
            }
        }
        if !extraItems.isEmpty { menu.addItem(.separator()) }
        extraItems.forEach(menu.addItem)
        if fetchedAt != nil || hint != nil { menu.addItem(.separator()) }
        if let fetchedAt {
            menu.addItem(disabledItem("Updated \(Self.relative(min(fetchedAt, Date())))")) // clock skew
        }
        if let hint { menu.addItem(disabledItem(hint)) }
    }

    @objc private func installUpdate() {
        updater?.install()
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    @objc private func openProfile() {
        NSWorkspace.shared.open(URL(string: "https://x.com/thsottiaux")!)
    }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            let alert = NSAlert(error: error)
            alert.informativeText = "Drag UsageMonitor.app out of the disk image to Applications or another folder, open it from there, and try again."
            alert.runModal()
        }
    }

    private func bankedResetItems(_ resets: BankedResets) -> [NSMenuItem] {
        let expirations = resets.expirations.filter { ($0 ?? .distantFuture) > Date() }
        let count = max(0, resets.count - (resets.expirations.count - expirations.count))
        var items = [disabledItem(count == 0 ? "No banked resets" : "Banked resets: \(count)")]
        for expiry in expirations.prefix(count) {
            items.append(disabledItem(expiry.map { "Expires \(Self.relative($0)) · \(Self.absolute($0))" } ?? "Never expires"))
        }
        let unlisted = count - min(count, expirations.count)
        if unlisted > 0 { items.append(disabledItem("\(unlisted) more, expiry unknown")) }
        return items
    }

    private func useLiveUsageItem() -> NSMenuItem {
        let item = NSMenuItem(title: "Use Live Usage…", action: #selector(useLiveUsage), keyEquivalent: "")
        item.target = self
        return item
    }

    private func disabledItem(_ title: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        return item
    }

    private static func relative(_ date: Date) -> String {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        return formatter.localizedString(for: date, relativeTo: Date())
    }

    private static func tilde(_ url: URL) -> String {
        (url.path as NSString).abbreviatingWithTildeInPath
    }

    private static func absolute(_ date: Date) -> String {
        date.formatted(.dateTime.weekday(.abbreviated).month(.abbreviated).day().hour().minute())
    }
}

if CommandLine.arguments.contains(ClaudeStatusLine.argument) {
    ClaudeStatusLine.publish()
} else {
    MainActor.assumeIsolated {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }
}
