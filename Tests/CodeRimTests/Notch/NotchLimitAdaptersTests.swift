import AppKit
import Foundation
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class NotchLimitAdaptersTests: XCTestCase {

    // MARK: - Pure mapping

    func testWindowMapsPercentToFractionAndLabel() {
        let mapped = try! XCTUnwrap(
            NotchLimitMapping.windows([win(id: "codex", minutes: 300, usedPercent: 73)]).first
        )
        XCTAssertEqual(mapped.id, "codex")
        XCTAssertEqual(mapped.label, "5 hours")
        XCTAssertEqual(mapped.usedFraction ?? -1, 0.73, accuracy: 0.0001)
        XCTAssertEqual(mapped.duration ?? -1, 300 * 60, accuracy: 0.5)
    }

    func testWindowFractionClampsAtZeroAndAllowsSlightOverspend() {
        XCTAssertEqual(NotchLimitMapping.windows([win(id: "a", minutes: 300, usedPercent: -5)])[0].usedFraction ?? -1,
                       0, accuracy: 0.0001)
        XCTAssertEqual(NotchLimitMapping.windows([win(id: "b", minutes: 300, usedPercent: 210)])[0].usedFraction ?? -1,
                       1.5, accuracy: 0.0001)
    }

    /// The bug behind two identical "5 hours" rows: every window of a limit
    /// group shared `limitID`, and `ForEach(id:)` renders the first element
    /// once per collision.
    func testEveryMappedWindowKeepsItsOwnIdentity() {
        let mapped = NotchLimitMapping.windows([
            windowNamed("Claude", id: "claude-five-hour", limitID: "claude", minutes: 300, usedPercent: 10),
            windowNamed("Claude", id: "claude-seven-day", limitID: "claude", minutes: 10_080, usedPercent: 17)
        ])
        XCTAssertEqual(mapped.map(\.id), ["claude-five-hour", "claude-seven-day"])
        XCTAssertEqual(Set(mapped.map(\.id)).count, 2)
        XCTAssertEqual(mapped.map(\.label), ["5 hours", "Weekly"])
        // One limit group, so no group heading to disambiguate.
        XCTAssertEqual(mapped.compactMap(\.group), [])
    }

    /// Two limit groups can each report a five-hour window; the group name is
    /// what tells the rows apart.
    func testWindowsFromSeveralLimitGroupsAreNamed() {
        let mapped = NotchLimitMapping.windows([
            windowNamed("Codex", id: "a", limitID: "codex", minutes: 300, usedPercent: 6),
            windowNamed("GPT-5.3-Codex-Spark", id: "b", limitID: "codex_bengalfox", minutes: 300, usedPercent: 6)
        ])
        XCTAssertEqual(mapped.map(\.group), ["Codex", "GPT-5.3-Codex-Spark"])
        XCTAssertEqual(Set(mapped.map(\.id)).count, 2)
    }

    // MARK: - Codex plan shaping

    func testProHasNoFiveHourWindowButOtherPlansDo() {
        let windows = [win(id: "five", minutes: 300, usedPercent: 6),
                       win(id: "weekly", minutes: 10_080, usedPercent: 48)]

        XCTAssertEqual(CodexPlanLimits.visibleWindows(windows, plan: "pro").map(\.id), ["weekly"])
        XCTAssertEqual(CodexPlanLimits.visibleWindows(windows, plan: "Pro").map(\.id), ["weekly"])
        XCTAssertEqual(CodexPlanLimits.visibleWindows(windows, plan: "plus").map(\.id), ["five", "weekly"])
        XCTAssertEqual(CodexPlanLimits.visibleWindows(windows, plan: nil).map(\.id), ["five", "weekly"])
    }

    /// Hiding the only window there is would blank the cell — worse than an
    /// unhelpful row.
    func testProKeepsAFiveHourWindowWhenItIsTheOnlyOne() {
        let only = [win(id: "five", minutes: 300, usedPercent: 6)]
        XCTAssertEqual(CodexPlanLimits.visibleWindows(only, plan: "pro").map(\.id), ["five"])
    }

    func testHeadlineIsTheTightestWindow() {
        let windows = [win(id: "five", minutes: 300, usedPercent: 20),
                       win(id: "weekly", minutes: 10_080, usedPercent: 80)]
        XCTAssertEqual(NotchLimitMapping.headlineID(windows), "weekly")
    }

    func testHeadlineIsNilWithoutWindows() {
        XCTAssertNil(NotchLimitMapping.headlineID([]))
    }

    /// The tooltip's "Today" line — CodeRim's local token count, which the
    /// ring's percentage never gives. Nil for no store and for a zero total.
    func testTodaysTokensReflectsLocalAccounting() {
        XCTAssertNil(NotchLimitMapping.todaysTokens(nil))
        XCTAssertNil(NotchLimitMapping.todaysTokens(UsageStore(automaticallyRefresh: false)),
                     "a fresh store has spent nothing")

        var snapshot = UsageSnapshot.empty
        snapshot.today = TokenUsage(inputTokens: 1000, cachedInputTokens: 100, outputTokens: 350)
        let store = UsageStore(initialSnapshot: snapshot, automaticallyRefresh: false)
        XCTAssertEqual(NotchLimitMapping.todaysTokens(store), 1350)
    }

    // MARK: - Codex adapter over a stub store

    func testCodexAdapterMapsAReadySnapshot() async throws {
        let defaults = try makeDefaults()
        defaults.set(true, forKey: "accountLimitsEnabled")
        let snapshot = AccountLimitsSnapshot(
            windows: [win(id: "primary", minutes: 300, usedPercent: 40),
                      win(id: "secondary", minutes: 10_080, usedPercent: 12)],
            resetCredits: nil,
            fetchedAt: Date()
        )
        let store = AccountLimitStore(provider: OneShotLimitProvider(snapshot),
                                      defaults: defaults, pollingInterval: nil)
        await store.refresh()

        let provider = CodexNotchProvider(limits: store, accounts: isolatedAccounts())
        let ps = try await provider.fetchSnapshot()

        XCTAssertEqual(ps.id, "codex")
        XCTAssertEqual(ps.windows.count, 2)
        XCTAssertEqual(ps.headlineID, "primary")
        XCTAssertEqual(ps.headline?.usedFraction ?? -1, 0.40, accuracy: 0.0001)
        XCTAssertEqual(ps.status, .ok)
    }

    func testCodexAdapterReportsNeedsAuthWhenLimitsDisabled() async throws {
        let defaults = try makeDefaults()
        defaults.set(false, forKey: "accountLimitsEnabled")
        let store = AccountLimitStore(provider: OneShotLimitProvider(nil),
                                      defaults: defaults, pollingInterval: nil)
        let provider = CodexNotchProvider(limits: store, accounts: isolatedAccounts())
        let ps = try await provider.fetchSnapshot()
        XCTAssertTrue(ps.windows.isEmpty)
        XCTAssertEqual(ps.status, .needsAuth)
        XCTAssertFalse(ps.hasReading)
    }

    /// The plan has to come from the *live* login, not the saved vault.
    ///
    /// The vault is empty for anyone who never added an account to CodeRim —
    /// which is most people — and reading the plan from there would quietly
    /// leave Pro's phantom five-hour window on screen.
    func testTheCodexAdapterReadsThePlanFromTheLiveLoginNotTheVault() async throws {
        let defaults = try makeDefaults()
        defaults.set(true, forKey: "accountLimitsEnabled")
        let snapshot = AccountLimitsSnapshot(
            windows: [win(id: "five", minutes: 300, usedPercent: 6),
                      win(id: "weekly", minutes: 10_080, usedPercent: 48)],
            resetCredits: nil,
            fetchedAt: Date()
        )
        let store = AccountLimitStore(provider: OneShotLimitProvider(snapshot),
                                      defaults: defaults, pollingInterval: nil)
        await store.refresh()

        // Nothing saved in the vault; the signed-in login says Pro.
        let accounts = CodexAccountStore(vault: EmptyVault(), login: StubLogin(plan: "pro"))
        let ps = try await CodexNotchProvider(limits: store, accounts: accounts).fetchSnapshot()

        XCTAssertEqual(ps.windows.map(\.id), ["weekly"],
                       "Pro's five-hour window is still on screen")
        XCTAssertEqual(ps.headlineID, "weekly")
        XCTAssertEqual(ps.accountPlanLabel, "Pro 20x")
        XCTAssertEqual(accounts.currentPlanType, "pro", "Display formatting must not change quota metadata")
    }

    func testProLiteDisplaysFiveTimesAndKeepsItsOwnLimitWindows() async throws {
        let defaults = try makeDefaults()
        defaults.set(true, forKey: "accountLimitsEnabled")
        let snapshot = AccountLimitsSnapshot(
            windows: [win(id: "five", minutes: 300, usedPercent: 6),
                      win(id: "weekly", minutes: 10_080, usedPercent: 48)],
            resetCredits: nil, fetchedAt: Date()
        )
        let store = AccountLimitStore(provider: OneShotLimitProvider(snapshot), defaults: defaults, pollingInterval: nil)
        await store.refresh()
        let accounts = CodexAccountStore(vault: EmptyVault(), login: StubLogin(plan: "prolite"))

        let ps = try await CodexNotchProvider(limits: store, accounts: accounts).fetchSnapshot()

        XCTAssertEqual(ps.accountPlanLabel, "Pro 5x")
        XCTAssertEqual(ps.windows.map(\.id), ["five", "weekly"])
        XCTAssertEqual(accounts.currentPlanType, "prolite")
    }

    func testUnrecognizedCodexPlanDoesNotAcquireAProMultiplier() {
        let accounts = CodexAccountStore(vault: EmptyVault(), login: StubLogin(plan: "future_plan"))
        accounts.refreshCurrentPlanType()
        XCTAssertEqual(accounts.currentPlanName, "Future Plan")
        XCTAssertNil(isolatedAccounts().currentPlanName)
        XCTAssertEqual(ProviderAccount(label: nil, plan: "Pro 20x", source: "Codex", manageURL: nil).summary,
                       "Pro 20x · via Codex")
    }

    func testSettingsAccountSurfacesDoNotOpenTheSavedCredentialVault() async throws {
        _ = NSApplication.shared
        let defaults = try makeDefaults()
        defaults.set("codex", forKey: "usageProvider")
        defaults.set(false, forKey: "profileSyncEnabled")
        defaults.set(false, forKey: "claudeEnabled")
        let vault = RecordingReadVault()
        let accounts = CodexAccountStore(vault: vault, login: StubLogin(plan: "pro"))
        let env = SettingsEnvironment(
            codexStore: UsageStore(automaticallyRefresh: false, defaults: defaults),
            limitStore: AccountLimitStore(defaults: defaults, pollingInterval: nil),
            claude: ClaudeIntegrationStore(defaults: defaults, automaticallyRefresh: false),
            profileStore: ProfileUsageStore(defaults: defaults), codexAccounts: accounts
        )
        let surfaces: [AnyView] = [AnyView(ProvidersSettingsView()), AnyView(ProviderSettingsView(provider: .codex)),
            AnyView(MenuPopoverView(accounts: accounts, embedded: true)
                .environmentObject(env.codexStore).environmentObject(env.profileStore)
                .environmentObject(env.limitStore).environmentObject(env.claude))]
        for surface in surfaces {
            let host = NSHostingView(rootView: surface.environmentObject(env).defaultAppStorage(defaults))
            host.sizingOptions = []
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 600),
                                  styleMask: [.borderless], backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.contentView = host
            host.layoutSubtreeIfNeeded()
            // Let SwiftUI's appearance tasks run, as they do when Settings opens.
            try await Task.sleep(for: .milliseconds(50))
            XCTAssertEqual(vault.reads, 0, "Opening Settings must not query saved credentials")
            window.close()
        }
        XCTAssertEqual(accounts.currentAccountDisplayName, "person@example.test")
        XCTAssertEqual(accounts.currentPlanName, "Pro 20x")
        XCTAssertTrue(accounts.accounts.isEmpty)
        let notch = CodexNotchProvider(limits: env.limitStore, accounts: accounts)
        XCTAssertEqual(notch.account()?.plan, "Pro 20x")
        XCTAssertEqual(notch.account()?.label, "person@example.test")
    }

    func testAPlusLoginKeepsItsFiveHourWindow() async throws {
        let defaults = try makeDefaults()
        defaults.set(true, forKey: "accountLimitsEnabled")
        let snapshot = AccountLimitsSnapshot(
            windows: [win(id: "five", minutes: 300, usedPercent: 6),
                      win(id: "weekly", minutes: 10_080, usedPercent: 48)],
            resetCredits: nil,
            fetchedAt: Date()
        )
        let store = AccountLimitStore(provider: OneShotLimitProvider(snapshot),
                                      defaults: defaults, pollingInterval: nil)
        await store.refresh()

        let accounts = CodexAccountStore(vault: EmptyVault(), login: StubLogin(plan: "plus"))
        let ps = try await CodexNotchProvider(limits: store, accounts: accounts).fetchSnapshot()
        XCTAssertEqual(ps.windows.map(\.id), ["five", "weekly"])
    }

    // MARK: - Helpers

    private func win(id: String, minutes: Int, usedPercent: Double) -> AccountLimitWindow {
        AccountLimitWindow(id: id, limitID: id, displayName: id,
                           windowDurationMinutes: minutes, usedPercent: usedPercent, resetsAt: nil)
    }

    private func windowNamed(_ name: String, id: String, limitID: String,
                             minutes: Int, usedPercent: Double) -> AccountLimitWindow {
        AccountLimitWindow(id: id, limitID: limitID, displayName: name,
                           windowDurationMinutes: minutes, usedPercent: usedPercent, resetsAt: nil)
    }

    /// An account store wired to nothing on disk.
    ///
    /// `CodexAccountStore`'s default login is the real `~/.codex/auth.json`, so
    /// a test that leaves it alone reads whichever plan the developer happens
    /// to be on — and a Pro machine hides the five-hour window mid-assertion.
    private func isolatedAccounts() -> CodexAccountStore {
        CodexAccountStore(vault: EmptyVault(), login: EmptyLogin())
    }

    private func makeDefaults() throws -> UserDefaults {
        let name = "NotchLimitAdaptersTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defaults.removePersistentDomain(forName: name)
        return defaults
    }
}

private struct OneShotLimitProvider: AccountLimitProviding {
    let snapshot: AccountLimitsSnapshot?
    init(_ snapshot: AccountLimitsSnapshot?) { self.snapshot = snapshot }
    func readLimits() async throws -> AccountLimitsSnapshot {
        guard let snapshot else { throw AccountLimitError.malformedResponse }
        return snapshot
    }
}

private struct EmptyVault: AccountVault {
    func load() throws -> [SavedCodexAccount] { [] }
    func save(_ accounts: [SavedCodexAccount]) throws {}
}

private final class RecordingReadVault: AccountVault {
    var reads = 0
    func load() throws -> [SavedCodexAccount] { reads += 1; return [] }
    func save(_ accounts: [SavedCodexAccount]) throws { XCTFail("Settings must not write saved credentials") }
}

private struct EmptyLogin: CodexLoginStoring {
    func read() throws -> Data? { nil }
    func replace(with data: Data, expecting original: Data?) throws {}
}

/// A signed-in `auth.json` on a named plan, shaped like the real one: the plan
/// lives in the id token's own claims.
private struct StubLogin: CodexLoginStoring {
    let plan: String

    func read() throws -> Data? {
        let claims: [String: Any] = [
            "sub": "subject-a",
            "email": "person@example.test",
            "https://api.openai.com/auth": [
                "chatgpt_account_id": "workspace-a",
                "chatgpt_plan_type": plan
            ]
        ]
        let payload = try JSONSerialization.data(withJSONObject: claims, options: [.sortedKeys])
            .base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        return try JSONSerialization.data(withJSONObject: [
            "auth_mode": "chatgpt",
            "OPENAI_API_KEY": NSNull(),
            "tokens": ["access_token": "synthetic-access",
                       "refresh_token": "synthetic-refresh",
                       "id_token": "e30.\(payload).synthetic-signature",
                       "account_id": "workspace-a"]
        ], options: [.sortedKeys])
    }

    func replace(with data: Data, expecting original: Data?) throws {}
}
