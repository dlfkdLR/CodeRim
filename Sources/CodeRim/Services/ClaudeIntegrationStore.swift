import Combine
import ClaudeBridgeCore
import Foundation

@MainActor
final class ClaudeIntegrationStore: ObservableObject {
    private static let maximumFreshLimitAge: TimeInterval = 15 * 60

    @Published private(set) var isEnabled: Bool
    @Published private(set) var isConnected = false
    @Published private(set) var detectedAccount: ClaudeAccount?
    @Published private(set) var account: ClaudeAccount?
    @Published private(set) var snapshot: AccountLimitsSnapshot?
    @Published private(set) var status: ClaudeIntegrationStatus
    @Published private(set) var statusMessage: String
    @Published private(set) var isRefreshing = false

    var onAvailabilityChanged: ((Bool) -> Void)?

    private let authenticator: ClaudeAuthenticating
    private let installer: ClaudeStatusLineInstalling
    private let defaults: UserDefaults
    private let limitsURL: URL
    private let now: @Sendable () -> Date
    private var pollingTask: Task<Void, Never>?
    private var lastAvailability = false
    private var operationGeneration = 0
    /// Set while a disable is tearing down. `isEnabled` only flips false after
    /// `performUninstall()` returns, so an add/refresh that begins during that
    /// await would otherwise pass its `isEnabled` guard and reinstall the helper.
    private var isDisabling = false

    init(
        authenticator: ClaudeAuthenticating = ClaudeCLIService(),
        installer: ClaudeStatusLineInstalling = ClaudeStatusLineInstaller(),
        defaults: UserDefaults = .standard,
        limitsURL: URL = AppPaths.applicationSupportDirectory
            .appendingPathComponent("Claude/ClaudeLimits.json"),
        now: @escaping @Sendable () -> Date = Date.init,
        automaticallyRefresh: Bool = true
    ) {
        self.authenticator = authenticator
        self.installer = installer
        self.defaults = defaults
        self.limitsURL = limitsURL
        self.now = now
        let enabled = defaults.bool(forKey: "claudeEnabled")
        isEnabled = enabled
        status = enabled ? .checking : .disabled
        statusMessage = enabled ? "Checking Claude account…" : "Claude is turned off"
        guard automaticallyRefresh else { return }
        startAutomaticRefresh()
    }

    func startAutomaticRefresh() {
        guard pollingTask == nil else { return }
        pollingTask = Task { [weak self] in
            await self?.refresh()
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(120))
                guard !Task.isCancelled else { return }
                await self?.refresh()
            }
        }
    }

    deinit { pollingTask?.cancel() }

    private(set) var accountSwitchInProgress = false
    var isAvailable: Bool { isEnabled && isConnected && !accountSwitchInProgress }

    func beginAccountSwitch() {
        accountSwitchInProgress = true
        cancelCurrentOperation()
        clearLimitsCache()
        snapshot = nil
        account = nil
        detectedAccount = nil
        isConnected = false
        status = .checking
        statusMessage = "Switching Claude account…"
        notifyAvailabilityIfNeeded()
    }

    func finishAccountSwitch() async {
        clearLimitsCache()
        accountSwitchInProgress = false
        guard isEnabled else { return }
        defaults.set(false, forKey: "claudeAccountLinked")
        defaults.removeObject(forKey: "claudeLinkedAccountID")
        await addCurrentAccount()
    }

    func setEnabled(_ enabled: Bool) async {
        guard isEnabled != enabled else { return }
        if enabled {
            isEnabled = true
            defaults.set(true, forKey: "claudeEnabled")
            status = .checking
            statusMessage = "Checking Claude account…"
            await refresh()
        } else {
            isDisabling = true
            defer { isDisabling = false }
            cancelCurrentOperation()
            do {
                try await performUninstall()
            } catch {
                status = .unavailable
                statusMessage = "Claude could not be turned off safely. Try again."
                return
            }
            isEnabled = false
            defaults.set(false, forKey: "claudeEnabled")
            isConnected = false
            detectedAccount = nil
            account = nil
            snapshot = nil
            status = .disabled
            statusMessage = "Claude is turned off"
            defaults.set(UsageProvider.codex.rawValue, forKey: "usageProvider")
            notifyAvailabilityIfNeeded()
        }
    }

    func addCurrentAccount() async {
        guard isEnabled, !isRefreshing, !isDisabling, !accountSwitchInProgress else { return }
        let operation = beginOperation()
        status = .checking
        statusMessage = "Checking Claude account…"
        defer { finishOperation(operation) }
        do {
            var detected = try await authenticator.accountStatus()
            guard isCurrent(operation), isEnabled, !isDisabling else { return }
            if detected == nil {
                // Nothing to add yet: run the CLI's own sign-in rather than
                // repeat "sign in first" and leave the button looking dead.
                statusMessage = "Finish signing in to Claude in your browser…"
                do { try await authenticator.signIn() } catch {
                    guard isCurrent(operation) else { return }
                    status = .needsAccount
                    statusMessage = "Claude sign-in didn’t finish. Choose Add Account to try again."
                    notifyAvailabilityIfNeeded()
                    return
                }
                guard isCurrent(operation), isEnabled, !isDisabling else { return }
                detected = try await authenticator.accountStatus()
                guard isCurrent(operation), isEnabled, !isDisabling else { return }
            }
            guard let found = detected else {
                detectedAccount = nil
                account = nil
                isConnected = false
                status = .needsAccount
                statusMessage = "Sign in to Claude Code first"
                notifyAvailabilityIfNeeded()
                return
            }
            clearLimitsCache()
            detectedAccount = found
            await linkAndConnect(found, operation: operation)
        } catch {
            if isCurrent(operation) { fail(error) }
        }
    }

    func disconnect() async {
        cancelCurrentOperation()
        do {
            try await performUninstall()
        } catch {
            status = .unavailable
            statusMessage = "Claude could not be disconnected safely. Try again."
            return
        }
        defaults.set(false, forKey: "claudeAccountLinked")
        defaults.removeObject(forKey: "claudeLinkedAccountID")
        clearLimitsCache()
        isConnected = false
        account = nil
        snapshot = nil
        status = .needsAccount
        statusMessage = "Add a Claude account to continue"
        defaults.set(UsageProvider.codex.rawValue, forKey: "usageProvider")
        notifyAvailabilityIfNeeded()
        await refresh()
    }

    func refresh() async {
        guard isEnabled, !isRefreshing, !isDisabling, !accountSwitchInProgress else { return }
        let operation = beginOperation()
        status = .checking
        defer { finishOperation(operation) }
        do {
            let found = try await authenticator.accountStatus()
            guard isCurrent(operation), isEnabled, !isDisabling else { return }
            detectedAccount = found
            let linkedIdentifier = defaults.string(forKey: "claudeLinkedAccountID")
            guard defaults.bool(forKey: "claudeAccountLinked"),
                  let found,
                  linkedIdentifier == found.linkIdentifier
            else {
                isConnected = false
                account = nil
                snapshot = nil
                status = .needsAccount
                statusMessage = found == nil
                    ? "Sign in to Claude Code, then add the account"
                    : "Add this Claude account to CodeRim"
                notifyAvailabilityIfNeeded()
                return
            }
            await linkAndConnect(found, operation: operation)
        } catch {
            if isCurrent(operation) { fail(error) }
        }
    }

    /// Local Claude usage needs no helper; only the limit percentages do. Connect
    /// for usage even when the status-line helper cannot be installed, and surface
    /// the limits problem rather than failing the whole integration.
    private func linkAndConnect(_ found: ClaudeAccount, operation: Int) async {
        defaults.set(true, forKey: "claudeAccountLinked")
        defaults.set(found.linkIdentifier, forKey: "claudeLinkedAccountID")
        account = found
        isConnected = true
        do {
            try await performInstall()
            guard isCurrent(operation), !accountSwitchInProgress, !isDisabling else { return }
            loadLimits()
        } catch where snapshot != nil {
            guard isCurrent(operation), !accountSwitchInProgress, !isDisabling else { return }
            status = .stale
            statusMessage = "Showing last known Claude limits"
        } catch {
            guard isCurrent(operation), !accountSwitchInProgress, !isDisabling else { return }
            snapshot = nil
            status = .waitingForLimits
            statusMessage = (error as? LocalizedError)?.errorDescription
                ?? "Claude limits could not be set up. Reconnect in Settings to retry."
        }
        notifyAvailabilityIfNeeded()
    }

    private func loadLimits() {
        guard let data = try? Data(contentsOf: limitsURL, options: .mappedIfSafe),
              data.count <= 131_072,
              let bridgeSnapshot = try? ClaudeRateLimitCodec.decode(data)
        else {
            snapshot = nil
            status = .waitingForLimits
            statusMessage = "Use Claude Code once, then refresh"
            return
        }
        var windows: [AccountLimitWindow] = []
        if let weekly = bridgeSnapshot.sevenDay {
            windows.append(limitWindow(id: "claude-seven-day", duration: 10_080, source: weekly))
        }
        if let session = bridgeSnapshot.fiveHour {
            windows.append(limitWindow(id: "claude-five-hour", duration: 300, source: session))
        }
        snapshot = AccountLimitsSnapshot(windows: windows, resetCredits: nil, fetchedAt: bridgeSnapshot.fetchedAt)
        guard !windows.isEmpty else {
            status = .waitingForLimits
            statusMessage = "Use Claude Code once, then refresh"
            return
        }
        let currentDate = now()
        let age = currentDate.timeIntervalSince(bridgeSnapshot.fetchedAt)
        let resetHasPassed = windows.contains { window in
            window.resetsAt.map { $0 <= currentDate } ?? false
        }
        if age > Self.maximumFreshLimitAge || age < -300 || resetHasPassed {
            status = .stale
            statusMessage = "Use Claude Code to update limits"
        } else {
            status = .ready
            statusMessage = "Claude limits updated"
        }
    }

    // Status-line install touches the settings file and byte-compares the bundled
    // helper. Keep that off the main actor so a 2-minute refresh never stutters UI.
    private func performInstall() async throws {
        let installer = self.installer
        try await Task.detached(priority: .utility) { try installer.install() }.value
    }

    private func performUninstall() async throws {
        let installer = self.installer
        try await Task.detached(priority: .utility) { try installer.uninstall() }.value
    }

    private func clearLimitsCache() {
        try? FileManager.default.removeItem(at: limitsURL)
        snapshot = nil
    }

    private func limitWindow(
        id: String,
        duration: Int,
        source: ClaudeRateLimitWindow
    ) -> AccountLimitWindow {
        // Mirror the Codex primary-limit shape: one limit name ("Claude"), with
        // the window itself named by AccountLimitWindow.windowLabel ("Weekly" /
        // "5 hours"). Keeps the card title from reading "Weekly · Weekly".
        AccountLimitWindow(
            id: id,
            limitID: "claude",
            displayName: "Claude",
            windowDurationMinutes: duration,
            usedPercent: source.usedPercentage,
            resetsAt: source.resetsAt
        )
    }

    private func fail(_ error: Error) {
        if isConnected, account != nil {
            status = .stale
            statusMessage = snapshot == nil
                ? "Claude account check failed. Try again."
                : "Showing last known Claude limits"
            return
        }
        isConnected = false
        account = nil
        snapshot = nil
        status = .unavailable
        statusMessage = (error as? LocalizedError)?.errorDescription ?? "Claude is unavailable"
        notifyAvailabilityIfNeeded()
    }

    private func notifyAvailabilityIfNeeded() {
        guard isAvailable != lastAvailability else { return }
        lastAvailability = isAvailable
        onAvailabilityChanged?(isAvailable)
    }

    private func beginOperation() -> Int {
        operationGeneration += 1
        isRefreshing = true
        return operationGeneration
    }

    private func finishOperation(_ operation: Int) {
        guard operationGeneration == operation else { return }
        isRefreshing = false
    }

    private func cancelCurrentOperation() {
        operationGeneration += 1
        isRefreshing = false
    }

    private func isCurrent(_ operation: Int) -> Bool {
        operationGeneration == operation
    }
}

enum ClaudeCommandRunner {
    static func run(
        executable: URL,
        arguments: [String],
        timeout: Duration,
        maximumOutputBytes: Int,
        acceptsNonzeroExit: Bool = false,
        environment: [String: String]? = nil
    ) async throws -> Data {
        do {
            let result = try await BoundedProcess.run(
                executable: executable, arguments: arguments,
                environment: environment ?? ClaudeProcessEnvironment.sanitized,
                timeout: timeout, maximumOutputBytes: maximumOutputBytes
            )
            guard result.status == 0 || acceptsNonzeroExit else { throw ClaudeIntegrationError.processFailed }
            return result.output
        } catch BoundedProcessError.timedOut { throw ClaudeIntegrationError.timedOut }
        catch BoundedProcessError.outputTooLarge { throw ClaudeIntegrationError.responseTooLarge }
        catch is BoundedProcessError { throw ClaudeIntegrationError.processFailed }
    }
}

enum ClaudeProcessEnvironment {
    static var sanitized: [String: String] {
        let inherited = ProcessInfo.processInfo.environment
        let allowed = [
            "HOME", "TMPDIR", "USER", "LOGNAME", "LANG", "LC_ALL", "LC_CTYPE",
            "__CF_USER_TEXT_ENCODING", "CLAUDE_CONFIG_DIR", "HTTP_PROXY", "HTTPS_PROXY",
            "NO_PROXY", "http_proxy", "https_proxy", "no_proxy"
        ]
        return allowed.reduce(into: ["PATH": "/usr/bin:/bin:/usr/sbin:/sbin"]) { result, key in
            result[key] = inherited[key]
        }
    }
}
