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
    /// Deliberately not persisted: the user opts in each launch, like Claude's credential access.
    private var codexLiveEnabled = false
    private var codexLiveFailed = false
    private var codexFetching = false
    private var claudeFetching = false
    private var claudeCLI: URL?
    private var claudeUsage: ClaudeUsage?
    private var claudeLive: ClaudeUsage?
    private var lastClaudeFetch = Date.distantPast
    private var claudeMode = ClaudeUsageMode.disabled
    /// Our status line is in Claude's settings, so declining credential access can fall back to it.
    private var claudeStatusLineInstalled = false
    private var claudeLiveProblem: String?

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let bundle = AppLocation.misplacedBundle,
           ask("Move Usage Display to Applications?",
               detail: "Claude’s status line runs Usage Display from where it is now, so it needs a permanent place.",
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
            codexItem = makeItem(length: NSStatusItem.squareLength)
            codexCLI = CommandLineTool.find("codex")
        }
        let claudeInstalled = fileManager.fileExists(atPath: claudeConfig.path)
            || fileManager.fileExists(atPath: ClaudeStatusLine.configDirectory.path)
        guard codexItem != nil || claudeInstalled else {
            NSApp.activate(ignoringOtherApps: true)
            let alert = NSAlert()
            alert.messageText = "Neither Codex nor Claude Code was found"
            alert.informativeText = "Usage Display shows the limits Codex saves in \(Self.tilde(codexHome)) "
                + "and Claude Code saves in \(Self.tilde(claudeConfig)). "
                + "Install and run one of them, then open Usage Display again."
            alert.runModal()
            NSApp.terminate(nil)
            return
        }

        if claudeInstalled {
            claudeCLI = CommandLineTool.find("claude")
            DistributedNotificationCenter.default().addObserver(
                self, selector: #selector(receiveClaudeUsage(_:)), name: ClaudeStatusLine.notification,
                object: ClaudeStatusLine.configDirectory.standardizedFileURL.path,
                suspensionBehavior: .deliverImmediately)
            let integration = ClaudeStatusLine(directory: ClaudeStatusLine.configDirectory,
                                                executable: Bundle.main.executableURL!)
            claudeMode = integration.configure(askToReplace: {
                self.ask("Replace your Claude status line?",
                         detail: "You already have a custom Claude status line. Replace it with Usage Display’s usage indicators?",
                         accept: "Replace Status Line", decline: "Keep Existing")
            }, askForCredentials: { error in
                self.askForClaudeCredentials(error: error)
            })
            claudeStatusLineInstalled = claudeMode == .statusLine
            if claudeMode != .disabled {
                claudeItem = makeItem(length: NSStatusItem.variableLength)
            }
        }
        guard codexItem != nil || claudeItem != nil else {
            NSApp.terminate(nil)
            return
        }
        refresh()
        fetchClaudeIfDue(after: 0)
        timer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { _ in
            MainActor.assumeIsolated {
                self.refresh()
                self.fetchClaudeIfDue(after: 5 * 60) // the usage endpoint rate-limits
                self.fetchCodexIfDue(after: 5 * 60)
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
            codexItem.button?.image = CodexIcon.image(remainingPercent: remaining)
            codexItem.button?.toolTip = remaining.map { "Codex: \(Int($0.rounded()))% left" }
                ?? "Codex usage unavailable"
        }
        if let claudeItem {
            if claudeMode == .statusLine {
                claudeUsage = claudeLive
            } else {
                claudeUsage = [claudeLive, claudeReader.read()].compactMap { $0 }
                    .max { ($0.fetchedAt ?? .distantPast) < ($1.fetchedAt ?? .distantPast) }
            }
            claudeItem.button?.image = ClaudeIcon.image(
                fiveHourPercent: claudeUsage?.fiveHour?.remainingPercent(),
                weeklyPercent: claudeUsage?.weekly?.remainingPercent())
            let parts = claudeUsage?.windows.map { "\($0.label) \(Int($0.remainingPercent().rounded()))% left" }
            claudeItem.button?.toolTip = parts.map { "Claude Code: " + $0.joined(separator: " · ") }
                ?? "Claude Code usage unavailable"
        }
    }

    private func fetchClaudeIfDue(after interval: TimeInterval) {
        guard claudeItem != nil, claudeMode == .credentials, !claudeFetching,
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
                if claudeStatusLineInstalled {
                    claudeMode = .statusLine
                    claudeLiveProblem = nil
                } else {
                    disableClaude()
                }
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
            if codexLiveEnabled {
                if let usage { codexLive = usage }
                codexLiveFailed = usage == nil
            }
            refresh()
        }
    }

    @objc private func toggleCodexLive() {
        codexLiveEnabled.toggle()
        if codexLiveEnabled {
            fetchCodexIfDue(after: 0, onlyIfStale: false)
        } else {
            codexLive = nil
            codexLiveFailed = false
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

    private func askForClaudeCredentials(error: Error?) -> Bool {
        let reason = error.map { "\($0.localizedDescription) " } ?? ""
        let declining = claudeStatusLineInstalled
            ? "Declining keeps using the status line." : "Declining disables Claude usage for this launch."
        return ask("Allow access to your Claude login?",
                   detail: reason + "Usage Display can read your Claude login from the macOS Keychain and request usage from Anthropic every five minutes. " + declining,
                   accept: "Allow Credential Access", decline: claudeStatusLineInstalled ? "Keep Status Line" : "Disable Claude")
    }

    private func disableClaude() {
        claudeMode = .disabled
        claudeLive = nil
        claudeUsage = nil
        if let claudeItem { NSStatusBar.system.removeStatusItem(claudeItem) }
        claudeItem = nil
        if codexItem == nil { NSApp.terminate(nil) }
    }

    @objc private func receiveClaudeUsage(_ notification: Notification) {
        guard claudeMode == .statusLine, let json = notification.userInfo?["usage"] as? String,
              let usage = ClaudeStatusLine.usage(from: Data(json.utf8)) else { return }
        claudeLive = usage
        refresh()
    }

    @objc private func useClaudeCredentials() {
        guard askForClaudeCredentials(error: nil) else { return }
        claudeMode = .credentials
        fetchClaudeIfDue(after: 0)
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
            if codexCLI != nil {
                menu.addItem(.separator())
                if codexLiveEnabled && codexLiveFailed {
                    menu.addItem(disabledItem("Couldn’t get live usage"))
                    menu.addItem(disabledItem("Run “codex login status” in a terminal to check your login"))
                }
                let live = NSMenuItem(title: "Use Background Codex for Live Usage", action: #selector(toggleCodexLive), keyEquivalent: "")
                live.target = self
                live.state = codexLiveEnabled ? .on : .off
                menu.addItem(live)
            }
            menu.addItem(.separator())
            let profile = NSMenuItem(title: "Did Tibo reset today?", action: #selector(openProfile), keyEquivalent: "")
            profile.target = self
            menu.addItem(profile)
            menu.addItem(.separator())
        } else {
            if let usage = claudeUsage {
                menu.addItem(disabledItem(usage.planType.map { "Claude Code (\($0))" } ?? "Claude Code"))
                let showingLive = claudeMode == .credentials && claudeLiveProblem == nil && claudeLive != nil
                    && usage.fetchedAt == claudeLive?.fetchedAt
                addUsage(usage.windows, fetchedAt: showingLive ? nil : usage.fetchedAt,
                         hint: claudeMode == .statusLine ? "Update only while you chat in CLI" : nil, to: menu)
            } else {
                menu.addItem(disabledItem("No Claude Code usage found"))
                menu.addItem(disabledItem(claudeMode == .statusLine
                    ? "Chat with Claude Code CLI to load usage"
                    : "Waiting for usage from Anthropic"))
            }
            if claudeMode == .statusLine {
                menu.addItem(.separator())
                let fallback = NSMenuItem(title: "Use Credential Access for Live Usage…", action: #selector(useClaudeCredentials), keyEquivalent: "")
                fallback.target = self
                menu.addItem(fallback)
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
            alert.informativeText = "Move UsageDisplay.app to /Applications and try again."
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
