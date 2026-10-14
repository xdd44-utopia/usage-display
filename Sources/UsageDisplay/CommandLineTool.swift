import Foundation

/// An app opened from the Dock doesn't get the shell's PATH, so the usual install locations are checked too.
enum CommandLineTool {
    static func find(_ name: String) -> URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let path = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
        let directories = ["\(home)/.local/bin", "\(home)/.claude/local", "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]
            + path
        return directories.lazy.map { URL(fileURLWithPath: $0).appendingPathComponent(name) }
            .first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    /// npm-installed tools run `#!/usr/bin/env node`, so node's folder must be on PATH.
    static func environment(for executable: URL) -> [String: String] {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        var environment = ProcessInfo.processInfo.environment
        let directories = [executable.deletingLastPathComponent().path, "/opt/homebrew/bin", "/usr/local/bin",
                           "\(home)/.local/bin"] + (environment["PATH"] ?? "").split(separator: ":").map(String.init)
        environment["PATH"] = directories.joined(separator: ":")
        return environment
    }

    static func run(_ executable: URL, arguments: [String], timeout: TimeInterval = 20) async {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.environment = environment(for: executable)
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            process.terminationHandler = { _ in continuation.resume() }
            do {
                try process.run()
            } catch {
                process.terminationHandler = nil
                continuation.resume()
                return
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                if process.isRunning { process.terminate() }
            }
        }
    }
}
