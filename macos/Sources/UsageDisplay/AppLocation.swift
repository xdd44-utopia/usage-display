import AppKit

/// Claude's status line and Launch at Login run the app by its path, so that path must outlive this launch.
enum AppLocation {
    /// The running app when macOS runs it from a temporary copy or it runs from the disk image,
    /// both of which disappear; nil under `swift run`. Any other folder is fine.
    static var misplacedBundle: URL? {
        guard Bundle.main.bundleIdentifier != nil else { return nil }
        let bundle = Bundle.main.bundleURL.resolvingSymlinksInPath()
        return isTranslocated(bundle) || isReadOnly(bundle) ? bundle : nil
    }

    /// Moves (or, off a read-only volume, copies) the app into /Applications, replacing an older copy; returns its new location.
    static func moveToApplications(_ bundle: URL) throws -> URL {
        let fileManager = FileManager.default
        let destination = URL(fileURLWithPath: "/Applications").appendingPathComponent(bundle.lastPathComponent)
        // Under App Translocation the bundle is a read-only copy, so act on the original; copy the bundle only if it can't be found.
        let source = isTranslocated(bundle) ? originalLocation(of: bundle) : bundle
        // Already in /Applications but translocated because it's still quarantined: only unquarantine.
        if source?.resolvingSymlinksInPath().path != destination.resolvingSymlinksInPath().path {
            var trashed: NSURL?
            if fileManager.fileExists(atPath: destination.path) {
                try fileManager.trashItem(at: destination, resultingItemURL: &trashed)
            }
            do {
                // A read-only volume (the disk image) can't be moved from.
                if let source, !isReadOnly(source) {
                    try fileManager.moveItem(at: source, to: destination)
                } else if let source {
                    try fileManager.copyItem(at: source, to: destination)
                } else {
                    try fileManager.copyItem(at: bundle, to: destination)
                }
            } catch {
                if let trashed {
                    try? fileManager.removeItem(at: destination)
                    try? fileManager.moveItem(at: trashed as URL, to: destination)
                }
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

    private static func isTranslocated(_ bundle: URL) -> Bool {
        bundle.path.contains("/AppTranslocation/")
    }

    private static func isReadOnly(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.volumeIsReadOnlyKey]).volumeIsReadOnly) ?? false
    }

    private static func originalLocation(of bundle: URL) -> URL? {
        typealias Original = @convention(c) (CFURL, UnsafeMutablePointer<Unmanaged<CFError>?>?) -> Unmanaged<CFURL>?
        guard let security = dlopen("/System/Library/Frameworks/Security.framework/Security", RTLD_LAZY),
              let symbol = dlsym(security, "SecTranslocateCreateOriginalPathForURL") else { return nil }
        return unsafeBitCast(symbol, to: Original.self)(bundle as CFURL, nil)?.takeRetainedValue() as URL?
    }
}
