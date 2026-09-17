import Foundation
import CodexBarCore

// The bridge: CodeRim already polls Codex limits (`AccountLimitStore`, via the
// signed app-server RPC) and Claude limits (`ClaudeIntegrationStore`, via the
// status-line helper), both publishing `AccountLimitsSnapshot?`. These adapters
// reflect that published state into the notch's `ProviderSnapshot` shape — they
// reuse those stores. Explicit Codex refreshes ask its store for a new reading;
// background bridge updates only map the published state.

// MARK: - Shared mapping

enum NotchLimitMapping {
    /// `AccountLimitWindow` (CodeRim) → `LimitWindow` (notch).
    ///
    /// Takes the whole set rather than one window: the id has to be unique
    /// across it, and whether a row needs its limit group named depends on how
    /// many groups there are.
    static func windows(_ source: [AccountLimitWindow]) -> [LimitWindow] {
        // One provider can report the same window length under more than one
        // limit — Codex meters a per-model allowance beside the plan's own. The
        // group name only earns its space when there is more than one.
        let named = Set(source.map(\.displayName)).count > 1
        return source.map { w in
            LimitWindow(
                // `w.id` — not `w.limitID`, which every window of a limit group
                // shares. `ForEach(…, id: \.id)` over duplicates renders the
                // first element once per collision, which is what put two
                // identical "5 hours" / "Weekly" rows in the tooltip.
                id: w.id,
                group: named ? w.displayName : nil,
                label: w.windowLabel,
                usedFraction: max(0, min(1.5, w.usedPercent / 100)),
                resetsAt: w.resetsAt,
                duration: TimeInterval(w.windowDurationMinutes) * 60
            )
        }
    }

    /// The tightest window's id — the one with the least remaining — so the ring
    /// means what actually constrains you. Matches
    /// `MenuBarLimitMeter.remainingFraction`'s "tightest window" choice.
    ///
    /// Returns the same `id` the mapping above uses, or the headline lookup
    /// finds nothing and the cell falls back to "whichever window came first".
    static func headlineID(_ windows: [AccountLimitWindow]) -> String? {
        windows.max(by: { $0.usedPercent < $1.usedPercent })?.id
    }
}

// MARK: - Codex

@MainActor
final class CodexNotchProvider: NotchProvider {
    let id = "codex"
    let displayName = "Codex"
    let glyph: ProviderGlyph = .openai

    private let limits: AccountLimitStore
    private let accounts: CodexAccountStore
    /// CodeRim's own local token accounting, for the tooltip's "today" line.
    private let usage: UsageStore?

    init(limits: AccountLimitStore, accounts: CodexAccountStore = .shared, usage: UsageStore? = nil) {
        self.limits = limits
        self.accounts = accounts
        self.usage = usage
    }

    var signInRoute: SignInRoute { .openApp(bundleID: "com.openai.chat", name: "Codex") }

    func account() -> ProviderAccount? {
        guard let email = accounts.currentAccountEmail else { return nil }
        return ProviderAccount(label: email, plan: accounts.currentPlanName,
                               source: "Codex", manageURL: nil)
    }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        // A reset credit changes the server quota immediately. A user refresh
        // must read it instead of re-rendering the pre-reset cached snapshot.
        // Keep background bridge updates read-only to avoid a polling loop.
        if ProviderInteractionContext.current == .userInitiated {
            await limits.refresh()
        }
        // From the live login, not the saved vault: most people never save an
        // account into CodeRim, and reading the plan from an empty vault
        // would silently draw Pro's phantom five-hour window anyway.
        accounts.refreshCurrentPlanType()
        let source = CodexPlanLimits.visibleWindows(
            limits.snapshot?.windows ?? [], plan: accounts.currentPlanType
        )
        let windows = NotchLimitMapping.windows(source)
        let status: ProviderStatus
        switch limits.status {
        case .ready:       status = .ok
        case .stale:       status = .stale(since: limits.snapshot?.fetchedAt ?? Date())
        case .loading:     status = windows.isEmpty ? .stale(since: .distantPast) : .ok
        case .disabled:    status = .needsAuth
        case .unavailable: status = .error(limits.statusMessage)
        }

        return ProviderSnapshot(
            id: id, displayName: displayName, glyph: glyph,
            fidelity: .official, status: status,
            windows: windows,
            headlineID: NotchLimitMapping.headlineID(source),
            todaysTokens: NotchLimitMapping.todaysTokens(usage),
            accountPlan: accounts.currentPlanName
        )
    }
}

/// What a Codex plan actually meters.
///
/// ChatGPT Pro has no five-hour allowance — only the weekly one. The
/// app-server still reports a five-hour window for those accounts, and drawing
/// it puts a limit on screen that can never bind and whose reset time is not a
/// five-hour reset. Hide it, and only for Pro: every other plan really does
/// have one.
enum CodexPlanLimits {
    /// The five-hour band `AccountLimitWindow.windowLabel` recognises.
    static let fiveHourMinutes: ClosedRange<Int> = 270...330

    static func hasFiveHourLimit(plan: String?) -> Bool {
        plan?.lowercased().trimmingCharacters(in: .whitespaces) != "pro"
    }

    static func visibleWindows(_ windows: [AccountLimitWindow], plan: String?) -> [AccountLimitWindow] {
        guard !hasFiveHourLimit(plan: plan) else { return windows }
        let kept = windows.filter { !fiveHourMinutes.contains($0.windowDurationMinutes) }
        // Never blank the cell: if five hours is somehow all this account
        // reports, an unhelpful window beats no reading at all.
        return kept.isEmpty ? windows : kept
    }
}

extension NotchLimitMapping {
    /// Today's local token total, or nil when there is nothing to show.
    @MainActor
    static func todaysTokens(_ usage: UsageStore?) -> Int? {
        guard let total = usage?.snapshot.today.totalTokens, total > 0 else { return nil }
        return Int(total)
    }
}

// MARK: - Claude

@MainActor
final class ClaudeNotchProvider: NotchProvider {
    let id = "claude"
    let displayName = "Claude Code"
    let glyph: ProviderGlyph = .claude

    private let claude: ClaudeIntegrationStore
    private let usage: UsageStore?

    init(claude: ClaudeIntegrationStore, usage: UsageStore? = nil) {
        self.claude = claude
        self.usage = usage
    }

    /// No ring at all when the whole Claude integration is switched off.
    var isVisibleWhenAbsent: Bool { claude.isEnabled }

    var signInRoute: SignInRoute {
        .guidance("Enable Claude Code in Settings and add the account signed in to the claude CLI.")
    }

    func account() -> ProviderAccount? {
        guard let account = claude.account else { return nil }
        return ProviderAccount(label: account.email, plan: account.planName,
                               source: "Claude Code", manageURL: nil)
    }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        let windows = NotchLimitMapping.windows(claude.snapshot?.windows ?? [])
        let status: ProviderStatus
        switch claude.status {
        case .ready:            status = .ok
        case .stale:            status = .stale(since: claude.snapshot?.fetchedAt ?? Date())
        case .checking:         status = windows.isEmpty ? .stale(since: .distantPast) : .ok
        case .waitingForLimits: status = windows.isEmpty ? .stale(since: .distantPast) : .ok
        case .needsAccount:     status = .needsAuth
        case .disabled:         status = .needsAuth
        case .unavailable:      status = .error(claude.statusMessage)
        }

        return ProviderSnapshot(
            id: id, displayName: displayName, glyph: glyph,
            fidelity: .official, status: status,
            windows: windows,
            headlineID: NotchLimitMapping.headlineID(claude.snapshot?.windows ?? []),
            todaysTokens: NotchLimitMapping.todaysTokens(usage),
            accountPlan: claude.account?.planName
        )
    }
}
