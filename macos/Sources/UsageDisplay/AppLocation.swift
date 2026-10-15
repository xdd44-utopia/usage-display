import AppKit

/// Claude's status line and Launch at Login run the app by its path, so it should live in Applications.
enum AppLocation {
    /// The running app when it isn't in an Applications folder; nil under `swift run`.
    static var misplacedBundle: URL? {
        guard Bundle.main.bundleIdentifier != nil else { return nil }
        let bundle = Bundle.main.bundleURL.resolvingSymlinksInPath()
        let folders = [URL(fileURLWithPath: "/Applications"),
                       FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications")]
        let inApplications = folders.contains { bundle.path.hasPrefix($0.resolvingSymlinksInPath().path + "/") }
        return inApplications ? nil : bundle
    }

    /// Moves the app into /Applications, replacing an older copy, and returns where it now is.
    static func moveToApplications(_ bundle: URL) throws -> URL {
        let fileManager = FileManager.default
        let destination = URL(fileURLWithPath: "/Applications").appendingPathComponent(bundle.lastPathComponent)
        // Opened from Downloads without moving it, macOS runs a read-only copy (App Translocation),
        // so move the original instead; copy only if it can't be found.
        let translocated = bundle.path.contains("/AppTranslocation/")
        let source = translocated ? originalLocation(of: bundle) : bundle
        // Already in /Applications but translocated because it's still quarantined: only unquarantine.
        if source?.resolvingSymlinksInPath().path != destination.resolvingSymlinksInPath().path {
            var trashed: NSURL?
            if fileManager.fileExists(atPath: destination.path) {
                try fileManager.trashItem(at: destination, resultingItemURL: &trashed)
            }
            do {
                if let source {
                    try fileManager.moveItem(at: source, to: destination)
                } else {
                    try fileManager.copyItem(at: bundle, to: destination)
                }
            } catch {
                if let trashed { try? fileManager.moveItem(at: trashed as URL, to: destination) }
                throw error
            }
        }
        // A quarantined app moved by anything but Finder is translocated again on launch.
        let xattr = Process()
        xattr.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
        xattr.arguments = ["-dr", "com.apple.quarantine", destination.path]
        try xattr.run()
        xattr.waitUntilExit()
        return destination
    }

    /// Also quits any older copy still running, so only the relaunched one shows icons.
    static func relaunch(at url: URL) {
        if let identifier = Bundle.main.bundleIdentifier {
            NSRunningApplication.runningApplications(withBundleIdentifier: identifier)
                .filter { $0 != .current }
                .forEach { $0.terminate() }
        }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: url, configuration: configuration) { _, error in
            DispatchQueue.main.async {
                if let error { NSAlert(error: error).runModal() }
                NSApp.terminate(nil)
            }
        }
    }

    private static func originalLocation(of bundle: URL) -> URL? {
        typealias Original = @convention(c) (CFURL, UnsafeMutablePointer<Unmanaged<CFError>?>?) -> Unmanaged<CFURL>?
        guard let security = dlopen("/System/Library/Frameworks/Security.framework/Security", RTLD_LAZY),
              let symbol = dlsym(security, "SecTranslocateCreateOriginalPathForURL") else { return nil }
        return unsafeBitCast(symbol, to: Original.self)(bundle as CFURL, nil)?.takeRetainedValue() as URL?
    }
}
