import Foundation

struct LimitWindow: Decodable {
    let usedPercent: Double
    let windowMinutes: Int?
    let resetsAt: TimeInterval?

    var resetDate: Date? { resetsAt.map { Date(timeIntervalSince1970: $0) } }

    func remainingPercent(now: Date = Date()) -> Double {
        if let reset = resetDate, reset <= now { return 100 }
        return max(0, min(100, 100 - usedPercent))
    }

    var label: String {
        guard let minutes = windowMinutes else { return "Limit" }
        switch minutes {
        case 10080: return "Weekly"
        case let m where m % 1440 == 0: return "\(m / 1440)-day"
        case let m where m % 60 == 0: return "\(m / 60)-hour"
        default: return "\(minutes)-minute"
        }
    }
}

extension Date {
    /// ISO 8601 with or without fractional seconds, as Codex and Anthropic both send.
    init?(iso8601 string: String) {
        guard let date = (try? Date(string, strategy: Date.ISO8601FormatStyle(includingFractionalSeconds: true)))
            ?? (try? Date(string, strategy: .iso8601)) else { return nil }
        self = date
    }
}

struct CodexUsage {
    let windows: [LimitWindow]
    let planType: String?
    let fetchedAt: Date?
    var bankedResets: BankedResets? = nil

    var tightest: LimitWindow? { windows.min { $0.remainingPercent() < $1.remainingPercent() } }
}

/// Full limit resets OpenAI grants, which the user can spend later.
struct BankedResets {
    let count: Int
    /// Soonest first; nil for a reset with no expiry. May list fewer than `count`.
    let expirations: [Date?]
}

/// Codex appends a `token_count` event with `rate_limits` to its session logs
/// (`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`) after each model turn.
final class CodexUsageReader {
    private let sessionsDir: URL
    private var cache: (url: URL, modified: Date, usage: CodexUsage)?
    private var withoutSnapshot: [URL: Date] = [:]

    init(codexHome: URL = CodexUsageReader.defaultCodexHome) {
        sessionsDir = codexHome.appendingPathComponent("sessions")
    }

    static var defaultCodexHome: URL {
        if let env = ProcessInfo.processInfo.environment["CODEX_HOME"], !env.isEmpty {
            return URL(fileURLWithPath: env)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
    }

    func read() -> CodexUsage? {
        // A session that never got a server reply has no snapshot.
        for (url, modified) in sessionFilesByRecency().prefix(5) {
            if let cache, cache.url == url, cache.modified == modified { return cache.usage }
            if withoutSnapshot[url] == modified { continue }
            if let usage = lastSnapshot(in: url) {
                cache = (url, modified, usage)
                return usage
            }
            withoutSnapshot[url] = modified
        }
        return nil
    }

    private func sessionFilesByRecency() -> [(URL, Date)] {
        guard let enumerator = FileManager.default.enumerator(
            at: sessionsDir,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]
        ) else { return [] }

        var files: [(URL, Date)] = []
        for case let url as URL in enumerator where url.pathExtension == "jsonl" {
            let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?
                .contentModificationDate ?? .distantPast
            files.append((url, modified))
        }
        return files.sorted { $0.1 > $1.1 }
    }

    private func lastSnapshot(in url: URL) -> CodexUsage? {
        // Try the tail first; logs can be several MB and the snapshot is usually near the end.
        if let usage = lastSnapshot(in: url, tailBytes: 512 * 1024) { return usage }
        return lastSnapshot(in: url, tailBytes: nil)
    }

    private func lastSnapshot(in url: URL, tailBytes: UInt64?) -> CodexUsage? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }

        let data: Data
        if let tailBytes {
            guard let size = try? handle.seekToEnd() else { return nil }
            try? handle.seek(toOffset: size > tailBytes ? size - tailBytes : 0)
            data = (try? handle.readToEnd()) ?? Data()
        } else {
            data = (try? handle.readToEnd()) ?? Data()
        }

        let marker = Data("\"rate_limits\":{".utf8)
        for line in data.split(separator: UInt8(ascii: "\n")).reversed()
        where line.range(of: marker) != nil {
            if let usage = decode(Data(line)) { return usage }
        }
        return nil
    }

    private func decode(_ line: Data) -> CodexUsage? {
        struct Event: Decodable {
            let timestamp: String?
            let payload: Payload?
        }
        struct Payload: Decodable {
            let rateLimits: RateLimits?
        }
        struct RateLimits: Decodable {
            let primary: LimitWindow?
            let secondary: LimitWindow?
            let planType: String?
        }

        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        guard let event = try? decoder.decode(Event.self, from: line),
              let limits = event.payload?.rateLimits else { return nil }

        let windows = [limits.primary, limits.secondary].compactMap { $0 }
        guard !windows.isEmpty else { return nil }
        return CodexUsage(windows: windows, planType: limits.planType,
                          fetchedAt: event.timestamp.flatMap { Date(iso8601: $0) })
    }
}

/// `codex app-server` asks OpenAI with Codex's own login and renews it when needed.
enum CodexUsageAPI {
    static func fetch(executable: URL) async -> CodexUsage? {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                continuation.resume(returning: request(executable: executable))
            }
        }
    }

    private static func request(executable: URL) -> CodexUsage? {
        let process = Process()
        process.executableURL = executable
        process.arguments = ["app-server", "--listen", "stdio://"]
        process.environment = CommandLineTool.environment(for: executable)
        let input = Pipe()
        let output = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        // If the server exits early, a write fails instead of killing the app with SIGPIPE.
        _ = fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)
        guard (try? process.run()) != nil else { return nil }
        let timeout = DispatchWorkItem { process.terminate() }
        DispatchQueue.global().asyncAfter(deadline: .now() + 20, execute: timeout)
        defer {
            timeout.cancel()
            if process.isRunning { process.terminate() }
            try? input.fileHandleForWriting.close()
        }

        func send(_ message: [String: Any]) {
            guard let data = try? JSONSerialization.data(withJSONObject: message, options: .withoutEscapingSlashes) else { return }
            try? input.fileHandleForWriting.write(contentsOf: data + Data([0x0A]))
        }
        send(["method": "initialize", "id": 1,
              "params": ["clientInfo": ["name": "usage_monitor", "title": "Usage Monitor", "version": "1.0.0"]]])

        var pending = Data()
        while true {
            let chunk = output.fileHandleForReading.availableData
            if chunk.isEmpty { return nil }
            pending.append(chunk)
            while let newline = pending.firstIndex(of: 0x0A) {
                let line = Data(pending[..<newline])
                pending.removeSubrange(...newline)
                guard let message = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                      let id = message["id"] as? Int else { continue }
                if id == 1 {
                    send(["method": "initialized", "params": [:]])
                    send(["method": "account/rateLimits/read", "id": 2])
                } else if id == 2 {
                    return usage(fromReply: line)
                }
            }
        }
    }

    static func usage(fromReply data: Data, now: Date = Date()) -> CodexUsage? {
        struct Reply: Decodable {
            let result: Result?
        }
        struct Result: Decodable {
            let rateLimits: Limits?
            let rateLimitsByLimitId: [String: Limits]?
            let rateLimitResetCredits: Credits?
        }
        struct Credits: Decodable {
            let availableCount: Int
            let credits: [Credit]?
        }
        struct Credit: Decodable {
            let status: String?
            let expiresAt: TimeInterval?
        }
        struct Limits: Decodable {
            let primary: Window?
            let secondary: Window?
            let planType: String?
        }
        struct Window: Decodable {
            let usedPercent: Double
            let windowDurationMins: Int?
            let resetsAt: TimeInterval?
        }

        guard let result = (try? JSONDecoder().decode(Reply.self, from: data))?.result,
              let limits = result.rateLimitsByLimitId?["codex"] ?? result.rateLimits else { return nil }
        let windows = [limits.primary, limits.secondary].compactMap { $0 }
            .map { LimitWindow(usedPercent: $0.usedPercent, windowMinutes: $0.windowDurationMins, resetsAt: $0.resetsAt) }
        guard !windows.isEmpty else { return nil }
        let resets = result.rateLimitResetCredits.map { credits in
            let expirations = (credits.credits ?? []).filter { $0.status == "available" }
                .map { $0.expiresAt.map { Date(timeIntervalSince1970: $0) } }
                .sorted { ($0 ?? .distantFuture) < ($1 ?? .distantFuture) }
            return BankedResets(count: max(0, credits.availableCount), expirations: expirations)
        }
        return CodexUsage(windows: windows, planType: limits.planType, fetchedAt: now, bankedResets: resets)
    }
}
