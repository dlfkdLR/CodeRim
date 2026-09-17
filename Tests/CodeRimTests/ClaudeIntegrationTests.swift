import ClaudeBridgeCore
import Foundation
import XCTest
@testable import CodeRim

final class ClaudeAccountPlanTests: XCTestCase {
    private func status(subscription: String = "max", tier: String? = nil) throws -> Data {
        var object = ["loggedIn": true, "orgId": "org-a", "email": "person@example.test",
                      "subscriptionType": subscription] as [String: Any]
        object["rateLimitTier"] = tier
        return try JSONSerialization.data(withJSONObject: object)
    }

    private func configuration(
        email: String = "person@example.test", organization: String = "org-a",
        tier: String = "default_claude_max_20x", userTier: String? = nil
    ) throws -> Data {
        var account = ["emailAddress": email, "organizationUuid": organization,
                       "organizationRateLimitTier": tier]
        account["userRateLimitTier"] = userTier
        return try JSONSerialization.data(withJSONObject: ["oauthAccount": account])
    }

    func testMaxTierComesFromTheMatchingLocalAccountProfile() throws {
        for (tier, label) in [("default_claude_max_5x", "Max 5x"), ("default_claude_max_20x", "Max 20x")] {
            let account = try XCTUnwrap(ClaudeCLIService.account(
                from: status(), configurationData: configuration(tier: tier)
            ))
            XCTAssertEqual(account.planName, label)
        }
    }

    func testPreviousAccountOrWorkspaceCannotSupplyMaxTier() throws {
        for data in [try configuration(email: "previous@example.test"), try configuration(organization: "org-b"),
                     Data(#"{"oauthAccount":{"organizationRateLimitTier":"default_claude_max_20x"}}"#.utf8),
                     Data("malformed".utf8)] {
            let account = try XCTUnwrap(ClaudeCLIService.account(from: status(), configurationData: data))
            XCTAssertEqual(account.planName, "Max")
            XCTAssertNil(account.rateLimitTier)
        }
        let noOrganization = Data(#"{"loggedIn":true,"email":"person@example.test","subscriptionType":"max"}"#.utf8)
        XCTAssertEqual(try ClaudeCLIService.account(from: noOrganization, configurationData: configuration())?.planName, "Max")
    }

    func testLiveTierWinsOverCachedTierAndUserTierWinsOverOrganization() throws {
        let live = try ClaudeCLIService.account(from: status(tier: "default_claude_max_5x"),
                                               configurationData: configuration())
        XCTAssertEqual(live?.planName, "Max 5x")
        let individual = try ClaudeCLIService.account(from: status(),
            configurationData: configuration(userTier: "default_claude_max_5x"))
        XCTAssertEqual(individual?.planName, "Max 5x")
        let unknownLive = try ClaudeCLIService.account(from: status(tier: "future_tier"),
                                                      configurationData: configuration())
        XCTAssertEqual(unknownLive?.planName, "Max", "Unknown current tiers must not reuse an old multiplier")
    }

    func testMissingOrUnknownTierDoesNotInventMultiplierOrUpgradePlan() throws {
        XCTAssertEqual(try ClaudeCLIService.account(from: status())?.planName, "Max")
        XCTAssertEqual(try ClaudeCLIService.account(from: status(tier: "default_claude_ai"))?.planName, "Max")
        XCTAssertEqual(try ClaudeCLIService.account(from: status(subscription: "pro"),
                                                   configurationData: configuration())?.planName, "Pro")
        XCTAssertNil(try ClaudeCLIService.account(from: Data(#"{"loggedIn":false}"#.utf8),
                                                  configurationData: configuration()))
    }

    func testPlanUpgradeKeepsTheAccountLinkIdentity() throws {
        let five = try ClaudeCLIService.account(from: status(tier: "default_claude_max_5x"))
        let twenty = try ClaudeCLIService.account(from: status(tier: "default_claude_max_20x"))
        XCTAssertNotNil(five)
        XCTAssertEqual(five?.linkIdentifier, twenty?.linkIdentifier)
        XCTAssertNotEqual(five?.planName, twenty?.planName)
    }

    func testCustomClaudeConfigurationUsesTheSameDirectoryAsTheCLI() {
        let home = URL(fileURLWithPath: "/Users/test")
        XCTAssertEqual(ClaudeCLIService.configurationURL(home: home, environment: [:]).path, "/Users/test/.claude.json")
        XCTAssertEqual(ClaudeCLIService.configurationURL(home: home, environment: ["CLAUDE_CONFIG_DIR": "/tmp/claude"]).path,
                       "/tmp/claude/.claude.json")
        XCTAssertEqual(ClaudeCLIService.configurationURL(home: home, environment: ["CLAUDE_CONFIG_DIR": "relative"]).path,
                       "/Users/test/.claude.json")
    }
}

final class ClaudeRateLimitCodecTests: XCTestCase {
    func testAccountIdentityDistinguishesEmailsWithinTheSameOrganization() throws {
        let first = try XCTUnwrap(try ClaudeCLIService.account(from: Data(#"{"loggedIn":true,"orgId":"shared-org","email":"first@example.com"}"#.utf8)))
        let second = try XCTUnwrap(try ClaudeCLIService.account(from: Data(#"{"loggedIn":true,"orgId":"shared-org","email":"second@example.com"}"#.utf8)))

        XCTAssertNotEqual(first.linkIdentifier, second.linkIdentifier)
        XCTAssertEqual(first.email, "first@example.com")
        XCTAssertEqual(second.email, "second@example.com")
    }

    func testLoggedInAccountWithoutAStableIdentityIsRejected() {
        XCTAssertThrowsError(
            try ClaudeCLIService.account(from: Data(#"{"loggedIn":true,"subscriptionType":"pro"}"#.utf8))
        ) { error in
            XCTAssertEqual(error as? ClaudeIntegrationError, .malformedResponse)
        }
    }

    func testParsesOnlyDocumentedFiveHourAndSevenDayFields() throws {
        let data = Data(#"{"rate_limits":{"five_hour":{"used_percentage":42.5,"resets_at":1800000000},"seven_day":{"used_percentage":75,"resets_at":1800100000}},"session_id":"private","transcript_path":"/private/path"}"#.utf8)
        let fetchedAt = Date(timeIntervalSince1970: 1_700_000_000)
        let snapshot = try XCTUnwrap(try ClaudeRateLimitCodec.parseStatusLineInput(data, fetchedAt: fetchedAt))

        XCTAssertEqual(snapshot.fiveHour?.usedPercentage, 42.5)
        XCTAssertEqual(snapshot.sevenDay?.usedPercentage, 75)
        XCTAssertEqual(snapshot.fetchedAt, fetchedAt)
        let encoded = try ClaudeRateLimitCodec.encode(snapshot)
        let text = try XCTUnwrap(String(data: encoded, encoding: .utf8))
        XCTAssertFalse(text.contains("private"))
        XCTAssertFalse(text.contains("transcript"))
    }

    func testRejectsOversizedAndInvalidPercentages() throws {
        XCTAssertThrowsError(
            try ClaudeRateLimitCodec.parseStatusLineInput(
                Data(repeating: 0x20, count: ClaudeRateLimitCodec.maximumInputBytes + 1)
            )
        )
        let invalid = Data(#"{"rate_limits":{"five_hour":{"used_percentage":101},"seven_day":{"used_percentage":-1}}}"#.utf8)
        XCTAssertNil(try ClaudeRateLimitCodec.parseStatusLineInput(invalid))
    }

    func testBridgeOutputIsLimitedToCodeRimApplicationSupport() {
        let support = URL(fileURLWithPath: "/Users/example/Library/Application Support", isDirectory: true)
        let production = support.appendingPathComponent("CodexMeter/Claude/ClaudeLimits.json")
        let development = support.appendingPathComponent("CodexMeter-Development/Claude/ClaudeLimits.json")

        XCTAssertTrue(ClaudeBridgeOutputPath.isAllowed(production, applicationSupportDirectory: support))
        XCTAssertTrue(ClaudeBridgeOutputPath.isAllowed(development, applicationSupportDirectory: support))
        XCTAssertFalse(ClaudeBridgeOutputPath.isAllowed(URL(fileURLWithPath: "/tmp/ClaudeLimits.json"), applicationSupportDirectory: support))
        XCTAssertFalse(ClaudeBridgeOutputPath.isAllowed(support.appendingPathComponent("CodexMeter/Other.json"), applicationSupportDirectory: support))
    }

    func testExecutableResolutionFallsBackToTheLaunchPath() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home", isDirectory: true)
        let versioned = root.appendingPathComponent("nodes/v20/bin", isDirectory: true)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: versioned, withIntermediateDirectories: true)

        XCTAssertThrowsError(try ClaudeExecutable.resolve(home: home, environment: [:]))

        let claude = versioned.appendingPathComponent("claude")
        FileManager.default.createFile(atPath: claude.path, contents: Data("#!/bin/sh\n".utf8),
                                       attributes: [.posixPermissions: 0o755])
        let resolved = try ClaudeExecutable.resolve(
            home: home,
            environment: ["PATH": "relative/skip:\(versioned.path)"]
        )
        XCTAssertEqual(resolved, claude.standardizedFileURL)
    }
}

@MainActor
final class ClaudeIntegrationStoreTests: XCTestCase {
    func testAccountMustBeExplicitlyAddedBeforeClaudeBecomesAvailable() async throws {
        let fixture = try ClaudeIntegrationFixture()
        defer { fixture.cleanup() }
        fixture.defaults.set(true, forKey: "claudeEnabled")
        let account = ClaudeAccount(email: "person@example.com", subscriptionType: "pro", authenticationMethod: "claude.ai")
        let authenticator = StaticClaudeAuthenticator(account: account)
        let installer = RecordingClaudeInstaller()
        let store = ClaudeIntegrationStore(
            authenticator: authenticator,
            installer: installer,
            defaults: fixture.defaults,
            limitsURL: fixture.limitsURL,
            automaticallyRefresh: false
        )

        await store.refresh()
        XCTAssertEqual(store.detectedAccount, account)
        XCTAssertFalse(store.isConnected)
        XCTAssertFalse(store.isAvailable)
        XCTAssertEqual(installer.installCount, 0)

        await store.addCurrentAccount()
        XCTAssertTrue(store.isConnected)
        XCTAssertTrue(store.isAvailable)
        XCTAssertEqual(installer.installCount, 1)
    }

    func testConnectedAccountLoadsWeeklyThenFiveHourLimitsAndDisablesCleanly() async throws {
        let fixture = try ClaudeIntegrationFixture()
        defer { fixture.cleanup() }
        fixture.defaults.set(true, forKey: "claudeEnabled")
        try FileManager.default.createDirectory(at: fixture.limitsURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let limits = ClaudeRateLimitSnapshot(
            fiveHour: ClaudeRateLimitWindow(usedPercentage: 20, resetsAt: Date(timeIntervalSince1970: 1_800_000_000)),
            sevenDay: ClaudeRateLimitWindow(usedPercentage: 30, resetsAt: Date(timeIntervalSince1970: 1_800_100_000)),
            fetchedAt: Date(timeIntervalSince1970: 1_700_000_000)
        )
        try ClaudeRateLimitCodec.encode(limits).write(to: fixture.limitsURL)
        let installer = RecordingClaudeInstaller()
        let account = ClaudeAccount(email: "person@example.com", subscriptionType: "max", authenticationMethod: "claude.ai",
                                    rateLimitTier: "default_claude_max_20x")
        fixture.defaults.set(true, forKey: "claudeAccountLinked")
        fixture.defaults.set(account.linkIdentifier, forKey: "claudeLinkedAccountID")
        let store = ClaudeIntegrationStore(
            authenticator: StaticClaudeAuthenticator(account: account),
            installer: installer,
            defaults: fixture.defaults,
            limitsURL: fixture.limitsURL,
            now: { Date(timeIntervalSince1970: 1_700_000_060) },
            automaticallyRefresh: false
        )

        await store.refresh()
        XCTAssertEqual(store.status, .ready)
        // The menu header renders these two fields next to the Codex account row.
        XCTAssertEqual(store.account?.displayName, "person@example.com")
        XCTAssertEqual(store.account?.planName, "Max 20x")
        let notch = ClaudeNotchProvider(claude: store)
        let notchSnapshot = try await notch.fetchSnapshot()
        XCTAssertEqual(notchSnapshot.accountPlanLabel, "Max 20x")
        XCTAssertEqual(notch.account()?.plan, "Max 20x")
        XCTAssertEqual(store.snapshot?.windows.map(\.windowDurationMinutes), [10_080, 300])
        XCTAssertEqual(store.snapshot?.windows.map(\.remainingPercent), [70, 80])
        await store.setEnabled(false)
        XCTAssertFalse(store.isAvailable)
        XCTAssertNil(store.snapshot)
        XCTAssertEqual(installer.uninstallCount, 1)
        XCTAssertEqual(fixture.defaults.string(forKey: "usageProvider"), UsageProvider.codex.rawValue)
    }

    func testUsageStaysAvailableWhenTheLimitsHelperCannotInstall() async throws {
        let fixture = try ClaudeIntegrationFixture()
        defer { fixture.cleanup() }
        fixture.defaults.set(true, forKey: "claudeEnabled")
        let account = ClaudeAccount(email: "person@example.com", subscriptionType: "pro", authenticationMethod: "claude.ai")
        let store = ClaudeIntegrationStore(
            authenticator: StaticClaudeAuthenticator(account: account),
            installer: ThrowingInstallClaudeInstaller(),
            defaults: fixture.defaults,
            limitsURL: fixture.limitsURL,
            automaticallyRefresh: false
        )

        await store.addCurrentAccount()

        XCTAssertTrue(store.isConnected, "usage tracking must not depend on the limits helper")
        XCTAssertTrue(store.isAvailable)
        XCTAssertNil(store.snapshot)
        XCTAssertEqual(store.status, .waitingForLimits)
        XCTAssertEqual(store.statusMessage, ClaudeIntegrationError.bridgeNotFound.errorDescription)
        XCTAssertTrue(fixture.defaults.bool(forKey: "claudeAccountLinked"))
    }

    func testChangedClaudeAccountMustBeAddedBeforeOldLimitsCanAppear() async throws {
        let fixture = try ClaudeIntegrationFixture()
        defer { fixture.cleanup() }
        fixture.defaults.set(true, forKey: "claudeEnabled")
        fixture.defaults.set(true, forKey: "claudeAccountLinked")
        fixture.defaults.set("different-account", forKey: "claudeLinkedAccountID")
        let current = ClaudeAccount(
            email: "new@example.com",
            subscriptionType: "pro",
            authenticationMethod: "claude.ai"
        )
        let store = ClaudeIntegrationStore(
            authenticator: StaticClaudeAuthenticator(account: current),
            installer: RecordingClaudeInstaller(),
            defaults: fixture.defaults,
            limitsURL: fixture.limitsURL,
            automaticallyRefresh: false
        )

        await store.refresh()

        XCTAssertFalse(store.isAvailable)
        XCTAssertNil(store.snapshot)
        XCTAssertEqual(store.status, .needsAccount)
        XCTAssertEqual(store.statusMessage, "Add this Claude account to CodeRim")
    }

    func testOldLimitSnapshotIsKeptButMarkedStale() async throws {
        let fixture = try ClaudeIntegrationFixture()
        defer { fixture.cleanup() }
        let fetchedAt = Date(timeIntervalSince1970: 1_700_000_000)
        let currentDate = fetchedAt.addingTimeInterval(16 * 60)
        fixture.defaults.set(true, forKey: "claudeEnabled")
        try FileManager.default.createDirectory(at: fixture.limitsURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let limits = ClaudeRateLimitSnapshot(
            fiveHour: ClaudeRateLimitWindow(usedPercentage: 45, resetsAt: currentDate.addingTimeInterval(3_600)),
            sevenDay: ClaudeRateLimitWindow(usedPercentage: 65, resetsAt: currentDate.addingTimeInterval(86_400)),
            fetchedAt: fetchedAt
        )
        try ClaudeRateLimitCodec.encode(limits).write(to: fixture.limitsURL)
        let account = ClaudeAccount(email: "person@example.com", subscriptionType: "pro", authenticationMethod: "claude.ai")
        fixture.defaults.set(true, forKey: "claudeAccountLinked")
        fixture.defaults.set(account.linkIdentifier, forKey: "claudeLinkedAccountID")
        let store = ClaudeIntegrationStore(
            authenticator: StaticClaudeAuthenticator(account: account),
            installer: RecordingClaudeInstaller(),
            defaults: fixture.defaults,
            limitsURL: fixture.limitsURL,
            now: { currentDate },
            automaticallyRefresh: false
        )

        await store.refresh()

        XCTAssertTrue(store.isAvailable)
        XCTAssertNotNil(store.snapshot)
        XCTAssertEqual(store.status, .stale)
        XCTAssertEqual(store.statusMessage, "Use Claude Code to update limits")
    }

    func testTransientAccountCheckFailureKeepsLastKnownConnectionAndLimits() async throws {
        let fixture = try ClaudeIntegrationFixture()
        defer { fixture.cleanup() }
        let currentDate = Date(timeIntervalSince1970: 1_700_000_000)
        fixture.defaults.set(true, forKey: "claudeEnabled")
        try FileManager.default.createDirectory(at: fixture.limitsURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let limits = ClaudeRateLimitSnapshot(
            fiveHour: ClaudeRateLimitWindow(usedPercentage: 20, resetsAt: currentDate.addingTimeInterval(3_600)),
            sevenDay: nil,
            fetchedAt: currentDate
        )
        try ClaudeRateLimitCodec.encode(limits).write(to: fixture.limitsURL)
        let account = ClaudeAccount(email: "person@example.com", subscriptionType: "pro", authenticationMethod: "claude.ai")
        fixture.defaults.set(true, forKey: "claudeAccountLinked")
        fixture.defaults.set(account.linkIdentifier, forKey: "claudeLinkedAccountID")
        let authenticator = SwitchableClaudeAuthenticator(account: account)
        let store = ClaudeIntegrationStore(
            authenticator: authenticator,
            installer: RecordingClaudeInstaller(),
            defaults: fixture.defaults,
            limitsURL: fixture.limitsURL,
            now: { currentDate },
            automaticallyRefresh: false
        )
        await store.refresh()
        let lastKnown = store.snapshot

        await authenticator.setShouldFail(true)
        await store.refresh()

        XCTAssertTrue(store.isAvailable)
        XCTAssertEqual(store.account, account)
        XCTAssertEqual(store.snapshot, lastKnown)
        XCTAssertEqual(store.status, .stale)
        XCTAssertEqual(store.statusMessage, "Showing last known Claude limits")
    }

    func testDisableFailureKeepsClaudeEnabledAndReportsTheProblem() async throws {
        let fixture = try ClaudeIntegrationFixture()
        defer { fixture.cleanup() }
        fixture.defaults.set(true, forKey: "claudeEnabled")
        let store = ClaudeIntegrationStore(
            authenticator: StaticClaudeAuthenticator(account: nil),
            installer: ThrowingUninstallClaudeInstaller(),
            defaults: fixture.defaults,
            limitsURL: fixture.limitsURL,
            automaticallyRefresh: false
        )

        await store.setEnabled(false)

        XCTAssertTrue(store.isEnabled)
        XCTAssertTrue(fixture.defaults.bool(forKey: "claudeEnabled"))
        XCTAssertEqual(store.status, .unavailable)
        XCTAssertEqual(store.statusMessage, "Claude could not be turned off safely. Try again.")
    }

    func testDisablingCancelsAnAccountAddInProgress() async throws {
        let fixture = try ClaudeIntegrationFixture()
        defer { fixture.cleanup() }
        fixture.defaults.set(true, forKey: "claudeEnabled")
        let account = ClaudeAccount(email: "person@example.com", subscriptionType: "pro", authenticationMethod: "claude.ai")
        let installer = RecordingClaudeInstaller()
        let authenticator = DelayedClaudeAuthenticator(account: account)
        let store = ClaudeIntegrationStore(
            authenticator: authenticator,
            installer: installer,
            defaults: fixture.defaults,
            limitsURL: fixture.limitsURL,
            automaticallyRefresh: false
        )

        let addTask = Task { await store.addCurrentAccount() }
        await authenticator.waitUntilStarted()
        await store.setEnabled(false)
        await authenticator.release()
        await addTask.value

        XCTAssertFalse(store.isEnabled)
        XCTAssertFalse(store.isConnected)
        XCTAssertFalse(store.isRefreshing)
        XCTAssertEqual(installer.installCount, 0)
    }
}

final class ClaudeStatusLineInstallerTests: XCTestCase {
    func testInstallPreservesAndUninstallRestoresExistingStatusLine() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let settings = root.appendingPathComponent(".claude/settings.json")
        let managed = root.appendingPathComponent("managed", isDirectory: true)
        let helper = root.appendingPathComponent("CodexMeterClaudeBridge")
        try FileManager.default.createDirectory(at: settings.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(#"{"theme":"dark","statusLine":{"type":"command","command":"existing-status"}}"#.utf8)
            .write(to: settings)
        FileManager.default.createFile(atPath: helper.path, contents: Data("helper".utf8), attributes: [.posixPermissions: 0o700])
        let installer = ClaudeStatusLineInstaller(
            settingsURL: settings,
            managedDirectory: managed,
            bridgeSource: { helper }
        )

        try installer.install()
        let installed = try XCTUnwrap(
            try JSONSerialization.jsonObject(with: Data(contentsOf: settings)) as? [String: Any]
        )
        XCTAssertEqual(installed["theme"] as? String, "dark")
        let command = try XCTUnwrap((installed["statusLine"] as? [String: Any])?["command"] as? String)
        XCTAssertTrue(command.contains("CodexMeterClaudeBridge"))
        XCTAssertTrue(command.contains("ClaudeLimits.json"))

        try installer.install()
        try Data("updated-helper".utf8).write(to: helper, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: helper.path)
        try installer.install()
        let installedHelper = managed.appendingPathComponent("CodexMeterClaudeBridge")
        XCTAssertEqual(try Data(contentsOf: installedHelper), Data("updated-helper".utf8))
        let permissions = try XCTUnwrap(
            FileManager.default.attributesOfItem(atPath: installedHelper.path)[.posixPermissions] as? NSNumber
        )
        XCTAssertEqual(permissions.intValue & 0o777, 0o700)

        try installer.uninstall()
        let restored = try XCTUnwrap(
            try JSONSerialization.jsonObject(with: Data(contentsOf: settings)) as? [String: Any]
        )
        XCTAssertEqual((restored["statusLine"] as? [String: Any])?["command"] as? String, "existing-status")
        XCTAssertEqual(restored["theme"] as? String, "dark")
    }

    func testInstallStripsQuarantineFromTheDeployedHelper() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let settings = root.appendingPathComponent(".claude/settings.json")
        let managed = root.appendingPathComponent("managed", isDirectory: true)
        let helper = root.appendingPathComponent("CodexMeterClaudeBridge")
        try FileManager.default.createDirectory(at: settings.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: settings)
        FileManager.default.createFile(atPath: helper.path, contents: Data("helper".utf8), attributes: [.posixPermissions: 0o700])
        // Stand in for the Homebrew-quarantined app bundle: the bundled helper
        // carries com.apple.quarantine, and copyItem would carry it across.
        let flag = Data("0081;00000000;Homebrew;".utf8)
        try flag.withUnsafeBytes { bytes in
            let rc = helper.path.withCString { setxattr($0, "com.apple.quarantine", bytes.baseAddress, bytes.count, 0, 0) }
            try XCTSkipIf(rc != 0, "filesystem does not support xattrs")
        }
        let installer = ClaudeStatusLineInstaller(
            settingsURL: settings,
            managedDirectory: managed,
            bridgeSource: { helper }
        )

        try installer.install()

        let installedHelper = managed.appendingPathComponent("CodexMeterClaudeBridge")
        var buffer = [CChar](repeating: 0, count: 512)
        let size = installedHelper.path.withCString { getxattr($0, "com.apple.quarantine", &buffer, buffer.count, 0, 0) }
        XCTAssertEqual(size, -1, "the deployed helper must not be quarantined")
        XCTAssertEqual(errno, ENOATTR)
    }

    func testReinstallKeepsTheFirstCapturedStatusLineWhenTheUserChangesItWhileManaged() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let settings = root.appendingPathComponent(".claude/settings.json")
        let managed = root.appendingPathComponent("managed", isDirectory: true)
        let helper = root.appendingPathComponent("CodexMeterClaudeBridge")
        try FileManager.default.createDirectory(at: settings.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(#"{"statusLine":{"type":"command","command":"original-bar"}}"#.utf8).write(to: settings)
        FileManager.default.createFile(atPath: helper.path, contents: Data("helper".utf8), attributes: [.posixPermissions: 0o700])
        let installer = ClaudeStatusLineInstaller(
            settingsURL: settings,
            managedDirectory: managed,
            bridgeSource: { helper }
        )

        try installer.install()
        // The user swaps in their own status line while CodeRim manages it.
        try Data(#"{"statusLine":{"type":"command","command":"user-edited-bar"}}"#.utf8).write(to: settings)
        try installer.install()
        try installer.uninstall()

        let restored = try XCTUnwrap(
            try JSONSerialization.jsonObject(with: Data(contentsOf: settings)) as? [String: Any]
        )
        XCTAssertEqual((restored["statusLine"] as? [String: Any])?["command"] as? String, "original-bar")
    }
}

private struct StaticClaudeAuthenticator: ClaudeAuthenticating {
    let account: ClaudeAccount?
    func accountStatus() async throws -> ClaudeAccount? { account }
}

private actor SwitchableClaudeAuthenticator: ClaudeAuthenticating {
    let account: ClaudeAccount
    private var shouldFail = false

    init(account: ClaudeAccount) { self.account = account }

    func setShouldFail(_ shouldFail: Bool) { self.shouldFail = shouldFail }

    func accountStatus() async throws -> ClaudeAccount? {
        if shouldFail { throw ClaudeIntegrationError.processFailed }
        return account
    }
}

private actor DelayedClaudeAuthenticator: ClaudeAuthenticating {
    let account: ClaudeAccount
    private var started = false
    private var startedWaiter: CheckedContinuation<Void, Never>?
    private var completion: CheckedContinuation<Void, Never>?
    init(account: ClaudeAccount) { self.account = account }
    func waitUntilStarted() async {
        if started { return }
        await withCheckedContinuation { startedWaiter = $0 }
    }
    func release() { completion?.resume(); completion = nil }
    func accountStatus() async throws -> ClaudeAccount? {
        await withCheckedContinuation { continuation in
            completion = continuation
            started = true
            startedWaiter?.resume(); startedWaiter = nil
        }
        return account
    }
}

private final class RecordingClaudeInstaller: ClaudeStatusLineInstalling, @unchecked Sendable {
    private(set) var installCount = 0
    private(set) var uninstallCount = 0
    func install() throws { installCount += 1 }
    func uninstall() throws { uninstallCount += 1 }
}

private struct ThrowingUninstallClaudeInstaller: ClaudeStatusLineInstalling {
    func install() throws {}
    func uninstall() throws { throw ClaudeIntegrationError.invalidSettings }
}

private struct ThrowingInstallClaudeInstaller: ClaudeStatusLineInstalling {
    func install() throws { throw ClaudeIntegrationError.bridgeNotFound }
    func uninstall() throws {}
}

private struct ClaudeIntegrationFixture {
    let root: URL
    let suiteName: String
    let defaults: UserDefaults
    let limitsURL: URL

    init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        suiteName = "CodeRim.ClaudeTests.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        limitsURL = root.appendingPathComponent("ClaudeLimits.json")
    }

    func cleanup() {
        try? FileManager.default.removeItem(at: root)
        defaults.removePersistentDomain(forName: suiteName)
    }
}
