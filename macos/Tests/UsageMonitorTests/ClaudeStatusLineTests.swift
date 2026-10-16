import Foundation
import Testing
@testable import UsageMonitor

@Suite(.serialized)
@MainActor
final class ClaudeStatusLineTests {
    private var directory: URL!
    private var integration: ClaudeStatusLine!
    private var settings: URL { directory.appendingPathComponent("settings.json") }

    init() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        integration = ClaudeStatusLine(directory: directory,
                                      executable: directory.appendingPathComponent("User's App $(echo surprise)"))
    }

    deinit {
        try? FileManager.default.removeItem(at: directory)
    }

    @Test func testInstallationPreservesOtherSettingsAndRecognizesItselfOnRelaunch() throws {
        try Data(#"{"permissions":{"allow":["Read"]},"model":"sonnet"}"#.utf8).write(to: settings)
        for _ in 0..<2 {
            let mode = integration.configure(askToReplace: {
                Issue.record("No replacement prompt for an empty slot or our own command")
                return false
            }, askForCredentials: { _ in
                Issue.record("No credential access for a working status line")
                return false
            })
            #expect((mode) == (.statusLine))
        }
        let saved = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: settings)) as? [String: Any])
        #expect((saved["model"] as? String) == ("sonnet"))
        #expect(((saved["permissions"] as? [String: [String]])?["allow"]) == (["Read"]))
        #expect(((saved["statusLine"] as? [String: String])?["command"]) == (integration.command))
    }

    @Test func testMovedAppRewritesItsOwnStatusLineWithoutAsking() throws {
        let moved = ClaudeStatusLine(directory: directory, executable: URL(fileURLWithPath: "/Applications/Other's.app/Contents/MacOS/UsageMonitor"))
        try JSONSerialization.data(withJSONObject: ["statusLine": ["type": "command", "command": moved.command]]).write(to: settings)
        let mode = integration.configure(askToReplace: {
            Issue.record("A moved copy of our own command is not a custom status line")
            return false
        }, askForCredentials: { _ in false })
        #expect((mode) == (.statusLine))
        let saved = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: settings)) as? [String: Any])
        #expect(((saved["statusLine"] as? [String: String])?["command"]) == (integration.command))
    }

    @Test func testExistingStatusLineFollowsConsentAndIsUntouchedWhenKept() throws {
        let original = Data(#"{"statusLine":{"type":"command","command":"existing-script","padding":2}}"#.utf8)
        for replace in [false, true] {
            for credentials in [false, true] {
                try original.write(to: settings)
                var prompts: [String] = []
                let mode = integration.configure(askToReplace: {
                    prompts.append("replace")
                    return replace
                }, askForCredentials: { error in
                    #expect((error) == nil)
                    prompts.append("credentials")
                    return credentials
                })
                #expect((mode) == (replace ? .statusLine : credentials ? .credentials : .disabled))
                #expect((prompts) == (replace ? ["replace"] : ["replace", "credentials"]))
                if !replace { #expect((try Data(contentsOf: settings)) == (original)) }
            }
        }
    }

    @Test func testUnreadableDisabledAndConcurrentlyChangedSettingsArePreserved() throws {
        for original in ["{broken", "[]", #"{"disableAllHooks":true}"#] {
            let data = Data(original.utf8)
            try data.write(to: settings)
            var offeredFallback = false
            let mode = integration.configure(askToReplace: {
                Issue.record("Must not offer replacement when settings cannot be safely used")
                return true
            }, askForCredentials: { error in
                #expect((error) != nil)
                offeredFallback = true
                return false
            })
            #expect(mode == .disabled)
            #expect(offeredFallback)
            #expect((try Data(contentsOf: settings)) == (data))
        }
        try Data(#"{"statusLine":{"command":"existing"}}"#.utf8).write(to: settings)
        let changed = Data(#"{"statusLine":{"command":"new-user-choice"}}"#.utf8)
        let mode = integration.configure(askToReplace: {
            try! changed.write(to: self.settings)
            return true
        }, askForCredentials: { error in
            #expect((error) != nil)
            return true
        })
        #expect(mode == .credentials)
        #expect((try Data(contentsOf: settings)) == (changed))
    }

    @Test func testMissingSettingsAndShellSpecialCharactersInExecutablePath() throws {
        #expect((integration.configure(askToReplace: { false }, askForCredentials: { _ in false })) == (.statusLine))
        try Data("#!/bin/sh\nprintf '%s' \"$1\"\n".utf8).write(to: integration.executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: integration.executable.path)
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = ["-c", integration.command]
        process.standardOutput = output
        try process.run()
        process.waitUntilExit()
        #expect((process.terminationStatus) == (0))
        #expect((String(data: output.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)) == (ClaudeStatusLine.argument))
    }

    @Test func testStatusLineProcessDeliversOnlyQuotaDataToTheApp() async throws {
        var products = Bundle(for: Self.self).bundleURL.deletingLastPathComponent()
        while !FileManager.default.isExecutableFile(atPath: products.appendingPathComponent("UsageMonitor").path) {
            let parent = products.deletingLastPathComponent()
            guard parent != products else { throw CocoaError(.fileNoSuchFile) }
            products = parent
        }
        var received = false
        let center = DistributedNotificationCenter.default()
        let observer = center.addObserver(forName: ClaudeStatusLine.notification,
                                          object: directory.standardizedFileURL.path, queue: .main) { note in
            let json = note.userInfo?["usage"] as? String
            #expect((json) != nil)
            #expect(!(json?.contains("private-project") ?? true))
            let usage = json.flatMap { ClaudeStatusLine.usage(from: Data($0.utf8)) }
            #expect((usage?.fiveHour?.usedPercent) == (25))
            #expect((usage?.weekly) == nil)
            received = true
        }
        defer { center.removeObserver(observer) }
        let process = Process()
        process.executableURL = products.appendingPathComponent("UsageMonitor")
        process.arguments = [ClaudeStatusLine.argument]
        var environment = ProcessInfo.processInfo.environment
        environment["CLAUDE_CONFIG_DIR"] = directory.path
        process.environment = environment
        let input = Pipe(), output = Pipe()
        process.standardInput = input
        process.standardOutput = output
        try process.run()
        let json: [String: Any] = ["cwd": "private-project", "rate_limits": [
            "five_hour": ["used_percentage": 25, "resets_at": Date().timeIntervalSince1970 + 3600]
        ]]
        try input.fileHandleForWriting.write(contentsOf: JSONSerialization.data(withJSONObject: json))
        try input.fileHandleForWriting.close()
        let deadline = Date().addingTimeInterval(5)
        while !received && Date() < deadline {
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        #expect(received)
        process.waitUntilExit()
        #expect((process.terminationStatus) == (0))
        #expect((String(data: output.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)) == ("5-hour: 75% left\n"))
        #expect(!(FileManager.default.fileExists(atPath: settings.path)))
    }

    @Test func testQuotaParsingHandlesPartialMissingExpiredAndInvalidWindows() throws {
        let now = Date(timeIntervalSince1970: 1000)
        let cases: [(String, Int)] = [
            (#"{"rate_limits":{"five_hour":{"used_percentage":25,"resets_at":2000},"seven_day":{"used_percentage":80,"resets_at":3000}}}"#, 2),
            (#"{"rate_limits":{"seven_day":{"used_percentage":80,"resets_at":3000}}}"#, 1),
            (#"{"rate_limits":{"five_hour":{"used_percentage":101,"resets_at":2000},"seven_day":{"used_percentage":80,"resets_at":3000}}}"#, 1),
            (#"{"rate_limits":{"five_hour":{"used_percentage":25,"resets_at":1000}}}"#, 0),
            (#"{"rate_limits":{"five_hour":{"used_percentage":-1,"resets_at":2000}}}"#, 0),
            (#"{"rate_limits":{"five_hour":{"resets_at":2000}}}"#, 0),
            (#"{"context_window":{"used_percentage":25}}"#, 0),
            (#"{"rate_limits":null}"#, 0),
            ("{broken", 0)
        ]
        for (json, count) in cases {
            let usage = ClaudeStatusLine.usage(from: Data(json.utf8), receivedAt: now)
            #expect((usage?.windows.count ?? 0) == (count))
            if count == 2 {
                #expect((usage?.fiveHour?.remainingPercent(now: now)) == (75))
                #expect((usage?.weekly?.remainingPercent(now: now)) == (20))
                #expect((usage?.fetchedAt) == (now))
            }
        }
    }
}
