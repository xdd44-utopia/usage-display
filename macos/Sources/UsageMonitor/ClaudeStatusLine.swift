import Foundation

struct ClaudeStatusLine {
    static let argument = "--claude-status-line"
    static let notification = Notification.Name("com.xdd44.usage-monitor.claude-usage")
    let directory: URL
    let executable: URL

    static var configDirectory: URL {
        if let path = ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"], !path.isEmpty {
            return URL(fileURLWithPath: path)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude")
    }

    var command: String {
        let path = "'" + executable.path.replacingOccurrences(of: "'", with: "'\"'\"'") + "'"
        return "if [ -x \(path) ]; then \(path) \(Self.argument); fi"
    }

    private static func isGenerated(_ command: String?) -> Bool {
        guard let command else { return false }
        return command.hasPrefix("if [ -x '") && command.hasSuffix(" \(argument); fi")
    }

    /// True once the status line runs this app; false when the user keeps their own. Throws when it can't be set up.
    func configure(askToReplace: () -> Bool) throws -> Bool {
        // Go through a symlinked settings.json so the write doesn't replace the link with a file.
        let file = directory.appendingPathComponent("settings.json").resolvingSymlinksInPath()
        let original = try settingsData(at: file)
        var settings: [String: Any] = [:]
        if let original {
            guard let object = try JSONSerialization.jsonObject(with: original) as? [String: Any] else {
                throw SetupError.invalidSettings
            }
            settings = object
        }
        if settings["disableAllHooks"] as? Bool == true { throw SetupError.hooksDisabled }
        let existing = settings["statusLine"]
        let ownStatusLine = (existing as? [String: Any]).map {
            $0["type"] as? String == "command" && Self.isGenerated($0["command"] as? String)
        } ?? false
        if ownStatusLine, (existing as? [String: Any])?["command"] as? String == command {
            return true
        }
        if !ownStatusLine && existing != nil && !(existing is NSNull) && !askToReplace() {
            return false
        }
        guard try settingsData(at: file) == original else { throw SetupError.settingsChanged }
        settings["statusLine"] = ["type": "command", "command": command]
        let data = try JSONSerialization.data(withJSONObject: settings, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try data.write(to: file, options: .atomic)
        return true
    }

    private func settingsData(at file: URL) throws -> Data? {
        do { return try Data(contentsOf: file) }
        catch CocoaError.fileReadNoSuchFile { return nil }
    }

    enum SetupError: LocalizedError {
        case invalidSettings, hooksDisabled, settingsChanged

        var errorDescription: String? {
            switch self {
            case .invalidSettings: return "Claude’s settings could not be read safely."
            case .hooksDisabled: return "Claude’s settings disable status-line commands."
            case .settingsChanged: return "Claude’s settings changed while setup was open."
            }
        }
    }

    static func usage(from data: Data, receivedAt: Date = Date()) -> ClaudeUsage? {
        struct Input: Decodable {
            let rate_limits: Limits?
            struct Limits: Decodable {
                let five_hour: Window?
                let seven_day: Window?
            }
            struct Window: Decodable {
                let used_percentage: Double?
                let resets_at: Double?
            }
        }
        guard let input = try? JSONDecoder().decode(Input.self, from: data), let limits = input.rate_limits else {
            return nil
        }
        func window(_ value: Input.Window?, minutes: Int) -> LimitWindow? {
            guard let used = value?.used_percentage, used.isFinite, (0...100).contains(used),
                  let reset = value?.resets_at, reset.isFinite, reset > receivedAt.timeIntervalSince1970 else {
                return nil
            }
            return LimitWindow(usedPercent: used, windowMinutes: minutes, resetsAt: reset)
        }
        let usage = ClaudeUsage(fiveHour: window(limits.five_hour, minutes: 300),
                                weekly: window(limits.seven_day, minutes: 10080),
                                planType: nil, fetchedAt: receivedAt)
        return usage.windows.isEmpty ? nil : usage
    }

    static func publish() {
        let data = FileHandle.standardInput.readDataToEndOfFile()
        guard let usage = usage(from: data) else { return }
        var limits: [String: Any] = [:]
        for (key, window) in [("five_hour", usage.fiveHour), ("seven_day", usage.weekly)] {
            if let window, let reset = window.resetsAt {
                limits[key] = ["used_percentage": window.usedPercent, "resets_at": reset]
            }
        }
        // Forward only quota fields; status-line input also contains private workspace information.
        guard let payload = try? JSONSerialization.data(withJSONObject: ["rate_limits": limits]),
              let json = String(data: payload, encoding: .utf8) else { return }
        DistributedNotificationCenter.default().postNotificationName(
            notification, object: configDirectory.standardizedFileURL.path,
            userInfo: ["usage": json], deliverImmediately: true)
        print(usage.windows.map { "\($0.label): \(Int($0.remainingPercent().rounded()))% left" }.joined(separator: " · "))
    }
}
