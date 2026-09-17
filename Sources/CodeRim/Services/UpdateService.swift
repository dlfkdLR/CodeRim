import AppKit
import Darwin
import Foundation
import Sparkle

@MainActor
final class UpdateService: NSObject, SPUUpdaterDelegate {
    static let shared = UpdateService()

    private var updaterController: SPUStandardUpdaterController?
    /// The app executable's file identity captured when the updater started. If it
    /// no longer matches on disk, an update was already applied and only a restart
    /// can pick it up — Sparkle would fail trying to install from the stale image.
    private var launchExecutableIdentity: ExecutableIdentity?
    /// Set while a user-initiated check is in flight, so a background check that
    /// fails never pops an unsolicited modal on this menu-bar app.
    private var checkIsUserInitiated = false
    private var isPromptingRestart = false

    private override init() { super.init() }

    var isAvailable: Bool {
        updaterController != nil || Self.canStartUpdater(bundle: .main)
    }

    var automaticallyChecksForUpdates: Bool {
        updaterController?.updater.automaticallyChecksForUpdates
            ?? (Bundle.main.object(forInfoDictionaryKey: "SUEnableAutomaticChecks") as? Bool ?? false)
    }

    func start() {
        guard updaterController == nil, Self.canStartUpdater(bundle: .main) else { return }
        launchExecutableIdentity = Self.currentExecutableIdentity()
        updaterController = SPUStandardUpdaterController(
            startingUpdater: true,
            updaterDelegate: self,
            userDriverDelegate: nil
        )
    }

    func setAutomaticallyChecksForUpdates(_ enabled: Bool) {
        start()
        updaterController?.updater.automaticallyChecksForUpdates = enabled
    }

    func checkForUpdates() {
        start()
        if pendingUpdateNeedsRestart {
            // A newer build is already on disk from an earlier update. Sparkle
            // cannot install over it from this stale process; a restart is the
            // only thing that helps, so say so instead of failing later.
            promptRestartToFinishUpdate()
            return
        }
        checkIsUserInitiated = true
        updaterController?.checkForUpdates(nil)
    }

    /// True once a newer build already sits on disk (from an earlier automatic
    /// update) while this older process keeps running.
    var pendingUpdateNeedsRestart: Bool {
        Self.bundleWasReplaced(sinceLaunch: launchExecutableIdentity, current: Self.currentExecutableIdentity())
    }

    // MARK: - SPUUpdaterDelegate

    func updater(_ updater: SPUUpdater, didAbortWithError error: any Error) {
        let userInitiated = checkIsUserInitiated
        checkIsUserInitiated = false

        // Only take over the failure when we have positive evidence a restart
        // fixes it: a newer build is already on disk and Sparkle aborted an
        // install/relaunch step. Every other abort (no update found, download
        // failure, /Applications not writable, user-cancelled authorization)
        // keeps Sparkle's own message, which is accurate for those.
        let nsError = error as NSError
        guard userInitiated,
              nsError.domain == SUSparkleErrorDomain,
              Self.installFailureCodes.contains(nsError.code),
              pendingUpdateNeedsRestart else { return }

        // Defer past Sparkle's own alert and the rest of this callback so our
        // recovery prompt is the last thing on screen, not a nested modal.
        Task { @MainActor in self.promptRestartToFinishUpdate() }
    }

    func updater(_ updater: SPUUpdater,
                 didFinishUpdateCycleFor updateCheck: SPUUpdateCheck,
                 error: (any Error)?) {
        checkIsUserInitiated = false
    }

    /// `SUSparkleErrorDomain` codes raised while applying an update. On their own
    /// they do not prove a restart helps — `pendingUpdateNeedsRestart` does — but
    /// they scope the takeover to the install phase.
    nonisolated static let installFailureCodes: Set<Int> = [
        Int(SUError.missingInstallerToolError.rawValue),
        Int(SUError.relaunchError.rawValue),
        Int(SUError.installationError.rawValue),
    ]

    // MARK: - Restart recovery

    private func promptRestartToFinishUpdate() {
        guard !isPromptingRestart else { return }
        isPromptingRestart = true
        defer { isPromptingRestart = false }

        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = "Restart CodeRim to finish updating"
        alert.informativeText = "A newer version was downloaded but can't replace CodeRim while it is running. "
            + "Restarting applies it. You can keep using this version until then."
        alert.addButton(withTitle: "Restart Now")
        alert.addButton(withTitle: "Later")

        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        Self.relaunch()
    }

    /// Waits for this process to exit, then reopens the app bundle. The wait is
    /// required because the app forbids a second instance while this one lives,
    /// and is capped so a vetoed quit can't leave the helper spinning.
    private static func relaunch() {
        guard let executablePath = Bundle.main.executableURL?.path,
              FileManager.default.isExecutableFile(atPath: executablePath) else {
            presentManualRestartFallback()
            return
        }

        let bundlePath = Bundle.main.bundlePath
        let pid = ProcessInfo.processInfo.processIdentifier
        let quoted = "'" + bundlePath.replacingOccurrences(of: "'", with: "'\\''") + "'"
        let script = "for _ in $(/usr/bin/seq 1 300); do /bin/kill -0 \(pid) 2>/dev/null || break; /bin/sleep 0.2; done; "
            + "exec /usr/bin/open \(quoted)"

        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", script]
        do {
            try task.run()
        } catch {
            presentManualRestartFallback()
            return
        }
        NSApp.terminate(nil)
    }

    private static func presentManualRestartFallback() {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Couldn't restart CodeRim automatically"
        alert.informativeText = "Quit CodeRim and open it again from your Applications folder to finish updating."
        alert.addButton(withTitle: "OK")
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    // MARK: - Executable identity

    struct ExecutableIdentity: Equatable {
        let device: UInt64
        let inode: UInt64
        let modified: Int64
        let size: Int64
    }

    nonisolated static func currentExecutableIdentity() -> ExecutableIdentity? {
        guard let path = Bundle.main.executableURL?.path else { return nil }
        var info = stat()
        guard stat(path, &info) == 0, info.st_mode & S_IFMT == S_IFREG else { return nil }
        return ExecutableIdentity(
            device: UInt64(UInt32(bitPattern: info.st_dev)),
            inode: info.st_ino,
            modified: Int64(info.st_mtimespec.tv_sec),
            size: Int64(info.st_size)
        )
    }

    /// Only reports a replacement when both readings are present and differ. A
    /// missing reading (unbundled test host, unreadable path) is never a prompt.
    nonisolated static func bundleWasReplaced(sinceLaunch launchIdentity: ExecutableIdentity?,
                                              current: ExecutableIdentity?) -> Bool {
        guard let launchIdentity, let current else { return false }
        return launchIdentity != current
    }

    // MARK: - Availability

    nonisolated static func canStartUpdater(bundle: Bundle) -> Bool {
        canStartUpdater(
            bundleURL: bundle.bundleURL,
            infoDictionary: bundle.infoDictionary ?? [:]
        )
    }

    nonisolated static func canStartUpdater(
        bundleURL: URL,
        infoDictionary: [String: Any]
    ) -> Bool {
        guard bundleURL.pathExtension == "app",
              let feed = infoDictionary["SUFeedURL"] as? String,
              let feedURL = URL(string: feed),
              feedURL.scheme == "https",
              feedURL.host != nil,
              let publicKey = infoDictionary["SUPublicEDKey"] as? String,
              !publicKey.isEmpty else {
            return false
        }
        return true
    }
}
