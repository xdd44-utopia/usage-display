import Foundation
import Testing
@testable import UsageDisplay

struct CodexUsageAPITests {
    @Test func testReplyPrefersTheCodexLimitAndKeepsEveryWindow() throws {
        let reply = Data(#"""
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
        """#.utf8)
        let now = Date(timeIntervalSince1970: 1_699_000_000)
        let usage = try #require(CodexUsageAPI.usage(fromReply: reply, now: now))
        #expect(usage.planType == "prolite")
        #expect(usage.windows.map(\.label) == ["5-hour", "Weekly"])
        #expect(usage.windows.map(\.usedPercent) == [19, 28.4])
        #expect(usage.fetchedAt == now)
        let resets = try #require(usage.bankedResets)
        #expect(resets.count == 4)
        #expect(resets.expirations == [Date(timeIntervalSince1970: 1_793_299_515), Date(timeIntervalSince1970: 1_794_007_077), nil])
    }

    @Test func testReplyFallsBackToTopLevelLimitsAndRejectsEmptyOrErrorReplies() {
        let topLevel = Data(#"{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":19,"windowDurationMins":10080},"secondary":null}}}"#.utf8)
        #expect(CodexUsageAPI.usage(fromReply: topLevel)?.windows.map(\.label) == ["Weekly"])
        #expect(CodexUsageAPI.usage(fromReply: topLevel)?.bankedResets == nil)
        #expect(CodexUsageAPI.usage(fromReply: Data(#"{"id":2,"result":{"rateLimits":{"primary":null,"secondary":null}}}"#.utf8)) == nil)
        #expect(CodexUsageAPI.usage(fromReply: Data(#"{"id":2,"error":{"code":-32600,"message":"not logged in"}}"#.utf8)) == nil)
    }

    @Test func testHandshakeWithAppServer() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let fake = directory.appendingPathComponent("codex")
        try Data(#"""
        #!/bin/sh
        read -r line; case "$line" in *'"initialize"'*) ;; *) exit 1;; esac
        echo '{"method":"configWarning","params":{}}'
        echo '{"id":1,"result":{}}'
        read -r line; case "$line" in *'"initialized"'*) ;; *) exit 1;; esac
        read -r line; case "$line" in *'account/rateLimits/read'*) ;; *) exit 1;; esac
        echo '{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":19,"windowDurationMins":10080}}}}'
        sleep 30
        """#.utf8).write(to: fake)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fake.path)

        let started = Date()
        let usage = await CodexUsageAPI.fetch(executable: fake)
        #expect(usage?.windows.first?.usedPercent == 19)
        #expect(Date().timeIntervalSince(started) < 10, "returns on the reply, without waiting for the server to exit")
    }
}
