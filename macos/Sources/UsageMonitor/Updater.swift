import AppKit
import Sparkle

/// Offers the latest GitHub release as a menu item instead of Sparkle's windows, on the Windows app's schedule.
/// Choosing the item downloads, installs, and relaunches without further prompts.
@MainActor
final class Updater: NSObject, SPUUserDriver, SPUUpdaterDelegate {
    /// The newer version on offer, or nil when none has been found.
    private(set) var availableVersion: String?
    /// Between choosing the menu item and relaunching.
    private(set) var isInstalling = false
    private var updater: SPUUpdater?
    private var lastCheck = Date.distantPast

    /// Nil under `swift run` or from the disk image or a temporary copy, which have no bundle Sparkle can replace.
    static func start() -> Updater? {
        guard Bundle.main.bundleIdentifier != nil, AppLocation.misplacedBundle == nil else { return nil }
        let driver = Updater()
        let updater = SPUUpdater(hostBundle: .main, applicationBundle: .main, userDriver: driver, delegate: driver)
        do {
            try updater.start()
        } catch {
            NSLog("Usage Monitor: updater didn’t start: \(error.localizedDescription)")
            return nil
        }
        driver.updater = updater
        return driver
    }

    /// Only looks: Sparkle's own scheduler is off (SUEnableAutomaticChecks), since it skips launches within a day of the last check.
    func checkIfDue(after interval: TimeInterval) {
        guard let updater, !isInstalling, !updater.sessionInProgress,
              Date().timeIntervalSince(lastCheck) >= interval else { return }
        lastCheck = Date()
        updater.checkForUpdateInformation()
    }

    /// Checks again so a release published since the last check is the one installed.
    func install() {
        guard let updater, !isInstalling else { return }
        isInstalling = true
        // Ignored while a background look is running; that look's end (see below) starts the install instead.
        if !updater.sessionInProgress { updater.checkForUpdates() }
    }

    private static func isNoUpdate(_ error: NSError) -> Bool {
        error.domain == SUSparkleErrorDomain && error.code == Int(SUError.noUpdateError.rawValue)
    }

    private func fail(_ error: Error) {
        isInstalling = false
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Couldn’t update Usage Monitor"
        alert.informativeText = Self.isNoUpdate(error as NSError)
            ? "You’re already on the latest version." : error.localizedDescription
        alert.runModal()
    }

    // MARK: SPUUpdaterDelegate

    func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
        availableVersion = item.displayVersionString
    }

    func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: Error?) {
        if isInstalling, updateCheck == .updateInformation {
            updater.checkForUpdates()
            return
        }
        guard updateCheck == .updateInformation, let error = error as NSError?, !Self.isNoUpdate(error) else { return }
        NSLog("Usage Monitor: update check failed: \(error.localizedDescription)")
        lastCheck = Date() - 24 * 60 * 60 + 15 * 60 // retry in 15 minutes
    }

    // MARK: SPUUserDriver

    /// Not asked while SUEnableAutomaticChecks is set; declining keeps Sparkle's scheduler off.
    func show(_ request: SPUUpdatePermissionRequest, reply: @escaping (SUUpdatePermissionResponse) -> Void) {
        reply(SUUpdatePermissionResponse(automaticUpdateChecks: false, sendSystemProfile: false))
    }

    func showUpdateFound(with appcastItem: SUAppcastItem, state: SPUUserUpdateState,
                         reply: @escaping (SPUUserUpdateChoice) -> Void) {
        availableVersion = appcastItem.displayVersionString
        reply(isInstalling && !appcastItem.isInformationOnlyUpdate ? .install : .dismiss)
    }

    func showUpdateNotFoundWithError(_ error: Error, acknowledgement: @escaping () -> Void) {
        availableVersion = nil
        if isInstalling { fail(error) }
        acknowledgement()
    }

    func showUpdaterError(_ error: Error, acknowledgement: @escaping () -> Void) {
        if isInstalling { fail(error) }
        acknowledgement()
    }

    func showReady(toInstallAndRelaunch reply: @escaping (SPUUserUpdateChoice) -> Void) {
        reply(.install)
    }

    func showUpdateInstalledAndRelaunched(_ relaunched: Bool, acknowledgement: @escaping () -> Void) {
        acknowledgement()
    }

    func dismissUpdateInstallation() {
        isInstalling = false
    }

    func showUserInitiatedUpdateCheck(cancellation: @escaping () -> Void) {}
    func showUpdateReleaseNotes(with downloadData: SPUDownloadData) {}
    func showUpdateReleaseNotesFailedToDownloadWithError(_ error: Error) {}
    func showDownloadInitiated(cancellation: @escaping () -> Void) {}
    func showDownloadDidReceiveExpectedContentLength(_ expectedContentLength: UInt64) {}
    func showDownloadDidReceiveData(ofLength length: UInt64) {}
    func showDownloadDidStartExtractingUpdate() {}
    func showExtractionReceivedProgress(_ progress: Double) {}
    func showInstallingUpdate(withApplicationTerminated applicationTerminated: Bool,
                              retryTerminatingApplication: @escaping () -> Void) {}
}
