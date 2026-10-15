import Foundation
import Security

struct ClaudeUsage {
    let fiveHour: LimitWindow?
    let weekly: LimitWindow?
    let planType: String?
    let fetchedAt: Date?

    var windows: [LimitWindow] { [fiveHour, weekly].compactMap { $0 } }
}

/// The usage object `/api/oauth/usage` returns, which Claude Code also caches verbatim.
private struct UsageResponse: Decodable {
    let fiveHour: Window?
    let sevenDay: Window?

    struct Window: Decodable {
        let utilization: Double?
        let resetsAt: String?
    }

    static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return decoder
    }()

    func usage(planType: String?, fetchedAt: Date?) -> ClaudeUsage? {
        func window(_ window: Window?, minutes: Int) -> LimitWindow? {
            guard let used = window?.utilization else { return nil }
            let reset = window?.resetsAt.flatMap { Date(iso8601: $0) }
            return LimitWindow(usedPercent: used, windowMinutes: minutes, resetsAt: reset?.timeIntervalSince1970)
        }
        let usage = ClaudeUsage(
            fiveHour: window(fiveHour, minutes: 300),
            weekly: window(sevenDay, minutes: 10080),
            planType: planType,
            fetchedAt: fetchedAt
        )
        return usage.windows.isEmpty ? nil : usage
    }
}

/// Claude Code refreshes `cachedUsageUtilization` when it fetches usage itself (e.g. `/usage`).
final class ClaudeUsageReader {
    private let configFile: URL
    private var cache: (modified: Date, usage: ClaudeUsage?)?

    init(configFile: URL = ClaudeUsageReader.defaultConfigFile) {
        self.configFile = configFile
    }

    static var defaultConfigFile: URL {
        if let env = ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"], !env.isEmpty {
            return URL(fileURLWithPath: env).appendingPathComponent(".claude.json")
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude.json")
    }

    func read() -> ClaudeUsage? {
        guard let modified = (try? configFile.resourceValues(forKeys: [.contentModificationDateKey]))?
            .contentModificationDate else { return nil }
        if let cache, cache.modified == modified { return cache.usage }
        // Unparseable usually means we caught Claude Code mid-write; keep the last good read.
        guard let data = try? Data(contentsOf: configFile), let usage = decode(data) else { return cache?.usage }
        cache = (modified, usage)
        return usage
    }

    /// Outer nil: not valid config JSON. Inner nil: valid, but no usage cached for this account.
    private func decode(_ data: Data) -> ClaudeUsage?? {
        struct Config: Decodable {
            let oauthAccount: Account?
            let cachedUsageUtilization: Cached?
        }
        struct Account: Decodable {
            let accountUuid: String?
            let organizationType: String? // e.g. "claude_team"
        }
        struct Cached: Decodable {
            let fetchedAtMs: Double?
            let accountUuid: String?
            let utilization: UsageResponse?
        }

        guard let config = try? UsageResponse.decoder.decode(Config.self, from: data) else { return nil }
        guard let cached = config.cachedUsageUtilization, let utilization = cached.utilization else { return .some(nil) }
        // After switching accounts the cache can still hold the previous account's limits.
        if let current = config.oauthAccount?.accountUuid, let owner = cached.accountUuid, current != owner {
            return .some(nil)
        }
        let plan = config.oauthAccount?.organizationType.map { $0.replacingOccurrences(of: "claude_", with: "") }
        return utilization.usage(planType: plan,
                                 fetchedAt: cached.fetchedAtMs.map { Date(timeIntervalSince1970: $0 / 1000) })
    }
}

/// Mirrors the request Claude Code's `/usage` makes. Never renews the login: refresh
/// tokens are single-use, so renewing here would sign Claude Code out.
enum ClaudeUsageAPI {
    enum Result {
        case usage(ClaudeUsage)
        case expired
        case denied
        case unavailable
    }

    private static let session = URLSession(configuration: .ephemeral)

    static func fetch() async -> Result {
        let login: Login
        // The Keychain call blocks while macOS waits on the user, so keep it off the shared async threads.
        let stored: StoredLogin = await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                continuation.resume(returning: storedLogin())
            }
        }
        switch stored {
        case .value(let value): login = value
        case .failed(let result): return result
        }

        var request = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
        request.setValue("Bearer \(login.token)", forHTTPHeaderField: "Authorization")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        request.timeoutInterval = 15
        guard let (data, response) = try? await session.data(for: request),
              let status = (response as? HTTPURLResponse)?.statusCode else { return .unavailable }
        if status == 401 { return .expired }
        guard status == 200,
              let body = try? UsageResponse.decoder.decode(UsageResponse.self, from: data),
              let usage = body.usage(planType: login.plan, fetchedAt: Date()) else { return .unavailable }
        return .usage(usage)
    }

    private struct Login {
        let token: String
        let plan: String?
    }

    private enum StoredLogin {
        case value(Login)
        case failed(Result)
    }

    private static func storedLogin() -> StoredLogin {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: "Claude Code-credentials",
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var item: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        if status == errSecUserCanceled || status == errSecAuthFailed { return .failed(.denied) }
        guard status == errSecSuccess, let data = item as? Data else { return .failed(.unavailable) }

        struct Stored: Decodable {
            let claudeAiOauth: OAuth?
        }
        struct OAuth: Decodable {
            let accessToken: String?
            let expiresAt: Double?
            let subscriptionType: String?
        }
        guard let oauth = try? JSONDecoder().decode(Stored.self, from: data).claudeAiOauth,
              let token = oauth.accessToken else { return .failed(.unavailable) }
        if let expiresAt = oauth.expiresAt, Date(timeIntervalSince1970: expiresAt / 1000) <= Date() {
            return .failed(.expired)
        }
        return .value(Login(token: token, plan: oauth.subscriptionType))
    }
}
