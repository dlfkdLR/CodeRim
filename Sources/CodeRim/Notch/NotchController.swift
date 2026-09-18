import AppKit
import Combine
import CodexBarCore

/// Owns the edge notch: one `NotchWindowController`, a `NotchUsageStore` fed by
/// the Codex and Claude adapters, the session monitors that light the activity
/// arcs, and the wiring between them. Modelled on `SettingsWindowController` —
/// a `@MainActor` singleton created from the app's `init`, shown or hidden from
/// a preference.
///
/// Phase 1–2: single screen, hover-to-expand, live session activity + a peek
/// and a chime when an agent finishes. Multi-monitor and the full notch
/// settings pane come later.
@MainActor
final class NotchController: ObservableObject {
    static let shared = NotchController()

    /// The latest reading for every provider, mirrored so the Settings
    /// Providers list can show connection state and limits without owning a
    /// second store.
    @Published private(set) var snapshots: [ProviderSnapshot] = []

    /// Whose credential each provider borrows, and its sign-in route — the
    /// Settings Providers list's row model. Cached rather than recomputed on
    /// every SwiftUI render: `providerSummaries` calls `account()` on every
    /// provider, and several of those open a file or a SQLite database on the
    /// main thread. Refreshed after each fetch and when a settings pane opens.
    @Published private(set) var providerSummaries: [ProviderSummary] = []
    @Published private(set) var selectedProviderIDs: Set<String> = ["codex"]

    private let window = NotchWindowController()
    private var store: NotchUsageStore?
    private var providers: [any NotchProvider] = []
    private var monitors: [String: any AgentActivityMonitor] = [:]
    private var completions = SessionCompletionWatcher()
    private let thresholds = ThresholdNotifier(
        isMuted: { id in
            let defaults = UserDefaults.standard
            let enabled = defaults.object(forKey: "notchThresholdAlerts") as? Bool
                ?? AppPreferences.defaultNotchThresholdAlerts
            return !enabled || AppPreferences.isAlertMuted(id)
        },
        deliver: { ThresholdAlerts.deliver($0) }
    )
    private var cancellables = Set<AnyCancellable>()
    /// Quota subscriptions remain active for CLI and widgets when the notch is hidden.
    private var whileVisible = Set<AnyCancellable>()
    private var configured = false
    private var visible = false

    /// Upstream stores also feed the CLI and WidgetKit while the notch is hidden.
    private var codexLimits: AccountLimitStore?
    private var claudeIntegration: ClaudeIntegrationStore?

    private let offsetKey = "notchAlongOffset"

    private init() {}

    /// Called once from `CodeRimApp.init`, after the stores exist.
    func configure(codexLimits: AccountLimitStore,
                   claudeIntegration: ClaudeIntegrationStore,
                   codexAccounts: CodexAccountStore,
                   codexUsage: UsageStore? = nil,
                   claudeUsage: UsageStore? = nil) {
        guard !configured else { return }
        configured = true
        self.codexLimits = codexLimits
        self.claudeIntegration = claudeIntegration

        providers = [
            CodexNotchProvider(limits: codexLimits, accounts: codexAccounts, usage: codexUsage),
            ClaudeNotchProvider(claude: claudeIntegration, usage: claudeUsage),
            // Borrows a token from GitHub CLI; its ring only appears once one
            // turns up (`isVisibleWhenAbsent == false`).
            CopilotNotchProvider(),
            // Borrows the Cursor editor's session from its SQLite store.
            CursorNotchProvider(),
            // Borrows the Grok CLI's `~/.grok/auth.json` session.
            GrokNotchProvider(),
            // Borrows the OpenCode Go key from `~/.local/share/opencode/auth.json`.
            OpenCodeNotchProvider(),
            // Borrows the Command Code desktop key from `~/.commandcode/auth.json`.
            CommandCodeNotchProvider(),
            // Borrows a Z.ai GLM Coding Plan key from Claude Code / ZCode / OpenCode.
            GLMNotchProvider(),
            // Ollama Cloud, keyed by OLLAMA_API_KEY in the environment.
            OllamaNotchProvider(),
            // Antigravity's own local language server (IDE or agy CLI must be running).
            AntigravityNotchProvider(),
            // Local `ollama serve` model listing (no ring — a local server has no quota).
            OllamaLocalProvider(),
        ] + ExtendedProviderCatalog.makeProviders()
        var existing = Set(UsageArchive().load().values.filter { $0.snapshot.hasReading }.map { $0.snapshot.id })
        existing.insert("codex")
        if claudeIntegration.isEnabled { existing.insert("claude") }
        selectedProviderIDs = NotchProviderSelection.load(existing: existing)
        let store = NotchUsageStore(providers: providers,
            disconnected: Set(providers.map(\.id)).subtracting(selectedProviderIDs), order: Self.storedProviderOrder())
        self.store = store
        if let codexUsage { store.bindLocalUsage(codexUsage, providerID: "codex") }
        if let claudeUsage { store.bindLocalUsage(claudeUsage, providerID: "claude") }

        ClaudeAccountStore.shared.onWillSwitch = { [weak claudeIntegration, weak store] in
            claudeIntegration?.beginAccountSwitch()
            store?.invalidateAccount(providerID: "claude")
        }
        ClaudeAccountStore.shared.onDidSwitch = { [weak claudeIntegration, weak store] in
            await claudeIntegration?.finishAccountSwitch()
            store?.invalidateAccount(providerID: "claude")
            store?.refresh(providerID: "claude")
        }

        window.onRefresh = { [weak store] in
            ProviderInteractionContext.$current.withValue(.userInitiated) {
                store?.refreshNow()
            }
        }
        window.onRefreshProvider = { [weak self] id in self?.refresh(providerID: id) }
        window.onOpenSettings = { SettingsWindowController.shared.present() }
        window.accountOptions = { [weak self, weak codexAccounts] in
            guard let self else { return [] }
            return NotchAccountOption.addedProviders(
                selectedIDs: self.selectedProviderIDs,
                summaries: self.store?.providerSummaries ?? self.providerSummaries,
                order: self.providerOrder,
                accountDisplayNames: ["codex": codexAccounts?.currentAccountDisplayName].compactMapValues { $0 }
            )
        }
        window.onManageAccountProviders = {
            SettingsWindowController.shared.present(selecting: .category(.providers))
        }
        window.onSwitchAccount = { id in
            switch NotchAccountDestination(providerID: id) {
            case .codex:
                CodexAccountsWindowController.shared.show()
            case .claude:
                ClaudeAccountsWindowController.shared.show()
            case .providerSettings(let providerID):
                SettingsWindowController.shared.present(selecting: .notchProvider(id: providerID))
            }
        }
        window.onOpenUsage = {
            SettingsWindowController.shared.present(selecting: .category(.usage))
        }
        window.onReposition = { [offsetKey] offset in
            UserDefaults.standard.set(Double(offset), forKey: offsetKey)
        }

        store.$snapshots
            .receive(on: RunLoop.main)
            .sink { [weak self] snapshots in
                guard let self else { return }
                self.snapshots = snapshots
                self.providerSummaries = self.store?.providerSummaries ?? []
                self.window.model.snapshots = snapshots
                self.window.model.now = Date()
                if self.visible { self.window.relocate(cellCount: snapshots.count) }
                if self.visible { self.thresholds.observe(snapshots) }
            }
            .store(in: &cancellables)
        providerSummaries = store.providerSummaries

        // Live agent activity — one monitor per provider ring. Built once and
        // started/stopped with the notch's visibility.
        let claudeHome = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".claude")
        monitors = [
            "claude": ClaudeSessionMonitor(
                directory: claudeHome.appendingPathComponent("sessions"),
                projects: claudeHome.appendingPathComponent("projects")
            ),
            "codex": CodexActivityMonitor(),
        ]
        for (id, monitor) in monitors {
            monitor.sessionsPublisher
                .receive(on: RunLoop.main)
                .sink { [weak self] live in
                    guard let self, self.selectedProviderIDs.contains(id) else { return }
                    self.window.model.sessions[id] = live
                    self.window.model.now = Date()
                    self.announceCompletions()
                }
                .store(in: &cancellables)
        }
        store.isBusy = { [weak self] in
            self?.monitors.values.contains { m in m.sessions.contains { $0.state == .busy } } ?? false
        }

        if let saved = UserDefaults.standard.object(forKey: offsetKey) as? Double {
            window.model.alongOffset = CGFloat(saved)
        }
        // Appearance that lives on the model rather than the panel is safe to
        // set now, before the panel exists.
        window.model.controlsPosition = .stored()
        window.model.accentColor = storedAccent()
        window.model.ringAppearance = .stored()
        window.model.resetTimeFormat = storedResetTimeFormat()
        window.model.percentageMode = NotchPercentageMode(
            rawValue: UserDefaults.standard.string(forKey: "notchPercentageMode") ?? ""
        ) ?? .used
        store.start()
        wireLimitBridge()
    }

    func lastUpdatedAt(providerID: String) -> Date? {
        store?.lastUpdatedAt(providerID: providerID)
    }

    /// Bound to `@AppStorage("showEdgeNotch")`.
    func setVisible(_ on: Bool) {
        guard configured, on != visible else { return }
        visible = on
        if on {
            window.model.edge = storedEdge()
            window.show()
            window.apply(size: storedSize())
            window.apply(storedVisibility())
            store?.start()
            for (id, monitor) in monitors where selectedProviderIDs.contains(id) { monitor.start() }
            wireLimitBridge()
        } else {
            monitors.values.forEach { $0.stop() }
            window.apply(.hidden)
        }
    }

    /// Refresh on quota or freshness changes, but ignore timestamps/defaults
    /// writes so saving the notch cache cannot feed a polling loop.
    private func wireLimitBridge() {
        guard whileVisible.isEmpty, let codexLimits, let claudeIntegration else { return }
        let codex = codexLimits.$snapshot.map { $0?.windows ?? [] }
            .combineLatest(codexLimits.$status)
            .removeDuplicates { $0.0 == $1.0 && $0.1 == $1.1 }
            .map { _ in () }
        let claude = claudeIntegration.$snapshot.map { $0?.windows ?? [] }
            .combineLatest(claudeIntegration.$status)
            .removeDuplicates { $0.0 == $1.0 && $0.1 == $1.1 }
            .map { _ in () }
        codex.merge(with: claude)
            .debounce(for: .seconds(1), scheduler: RunLoop.main)
            .sink { [weak store] _ in store?.refreshNow() }
            .store(in: &whileVisible)
    }

    /// Bound to `@AppStorage("notchEdge")`.
    func apply(edge: NotchEdge) {
        guard configured else { return }
        window.apply(edge: edge)
    }

    // MARK: - Appearance (the Notch settings pane)

    /// Pinned-open vs hover. Only meaningful while the notch is shown; `hidden`
    /// is reached through the master toggle, not this.
    func apply(visibility: NotchVisibility) {
        guard configured, visible else { return }
        window.apply(visibility)
    }

    func apply(size: NotchSize) {
        guard configured else { return }
        window.apply(size: size)
    }

    func apply(controlsPosition: NotchControlsPosition) {
        guard configured else { return }
        window.model.controlsPosition = controlsPosition
        window.relocate()
    }

    func apply(accent: NotchAccentChoice) {
        guard configured else { return }
        window.model.accentColor = accent
    }

    func apply(ringAppearance: NotchRingAppearance) {
        guard configured else { return }
        window.model.ringAppearance = ringAppearance
    }

    func apply(resetTimeFormat: ResetTimeFormat) {
        guard configured else { return }
        window.model.resetTimeFormat = resetTimeFormat
    }

    func apply(percentageMode: NotchPercentageMode) {
        guard configured else { return }
        window.model.percentageMode = percentageMode
    }

    /// Drop the ⌥-drag offset and sit the notch back at the centre of its edge.
    func recentre() {
        guard configured else { return }
        window.model.alongOffset = 0
        UserDefaults.standard.set(0.0, forKey: offsetKey)
    }

    // MARK: - The Settings ▸ Providers list

    func snapshot(for id: String) -> ProviderSnapshot? {
        snapshots.first { $0.id == id }
    }

    /// The Codenotch-style row model for one provider — glyph, account, sign-in
    /// route, and whether macOS refused the credential on the last read. Reads
    /// the cache; `refreshProvidersForSettings()` rebuilds it.
    func summary(for id: String) -> ProviderSummary? {
        providerSummaries.first { $0.id == id }
    }

    /// The provider's mark, so a settings pane can draw it even before a
    /// snapshot has arrived.
    func glyph(for id: String) -> ProviderGlyph {
        providers.first { $0.id == id }?.glyph ?? NotchProviderCatalog.glyph(for: id)
    }

    /// One fetch, so a provider pane opened while the notch is hidden shows a
    /// live reading rather than nothing. The shared timer also supplies
    /// CLI and widgets while hidden. This also rebuilds account summaries.
    func refreshProvidersForSettings() {
        providerSummaries = store?.providerSummaries ?? []
        store?.refreshNow()
    }

    /// Re-ask for one provider — its "Try again" / "Allow access…" button.
    func refresh(providerID: String) {
        ProviderInteractionContext.$current.withValue(.userInitiated) {
            BrowserCookieAccessGate.withExplicitRetry { store?.refresh(providerID: providerID) }
        }
    }

    /// Switching must open the account source even while already signed in.
    @discardableResult
    func openAccountSource(providerID: String) -> Bool {
        store?.openAccountSource(providerID: providerID) ?? false
    }

    func providerConfigurationDidChange(_ id: String) {
        providers.first { $0.id == id }?.forgetCachedCredential()
        store?.invalidateAccount(providerID: id)
        providerSummaries = store?.providerSummaries ?? []
        refresh(providerID: id)
    }

    func addProvider(_ id: String) {
        guard NotchProviderCatalog.all.contains(where: { $0.id == id }),
              selectedProviderIDs.insert(id).inserted else { return }
        applyProviderSelection()
        if visible { monitors[id]?.start() }
        store?.refresh(providerID: id)
    }

    func removeProvider(_ id: String) {
        guard selectedProviderIDs.remove(id) != nil else { return }
        monitors[id]?.stop()
        window.model.sessions.removeValue(forKey: id)
        window.model.hoveredIndex = nil
        applyProviderSelection()
    }

    private func applyProviderSelection() {
        NotchProviderSelection.save(selectedProviderIDs)
        store?.disconnected = Set(providers.map(\.id)).subtracting(selectedProviderIDs)
        providerSummaries = store?.providerSummaries ?? []
    }

    // MARK: - Ring order (the Providers pane's drag handles)

    private static let providerOrderKey = "notchProviderOrder"

    static func storedProviderOrder() -> [String] {
        (UserDefaults.standard.string(forKey: providerOrderKey) ?? "")
            .split(separator: ",")
            .map { String($0).trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }

    /// The order the notch draws its rings, as provider ids. Empty until the
    /// user drags one — the store then falls back to registration order.
    var providerOrder: [String] { store?.order ?? Self.storedProviderOrder() }

    /// Persist a new ring order and apply it to the live notch without a poll.
    func setProviderOrder(_ ids: [String]) {
        store?.order = ids
        UserDefaults.standard.set(ids.joined(separator: ","), forKey: Self.providerOrderKey)
    }

    // MARK: - Completion peek + chime

    private func announceCompletions() {
        let events = completions.absorb(window.model.sessions)
        guard let event = events.first else { return }
        NotchLog.sessions.info("session \(event.session.name, privacy: .public) \(String(describing: event.reason), privacy: .public)")

        let defaults = UserDefaults.standard
        if defaults.object(forKey: "notchSessionEndSound") as? Bool ?? AppPreferences.defaultNotchSessionEndSound {
            let name = event.reason == .blocked
                ? (defaults.string(forKey: "notchSessionBlockedSoundName") ?? "Funk")
                : (defaults.string(forKey: "notchSessionEndSoundName") ?? "Glass")
            SessionChime.play(name)
        }
        guard defaults.object(forKey: "notchAnnounceSessionEnd") as? Bool
            ?? AppPreferences.defaultNotchAnnounceSessionEnd else { return }
        window.peek(for: 5, focusing: event.session.processID)
    }

    private func storedEdge() -> NotchEdge {
        NotchEdge(rawValue: UserDefaults.standard.string(forKey: "notchEdge") ?? "")
            ?? NotchEdge(rawValue: AppPreferences.defaultNotchEdge) ?? .right
    }

    private func storedVisibility() -> NotchVisibility {
        NotchVisibility(rawValue: UserDefaults.standard.string(forKey: "notchVisibility") ?? "")
            ?? NotchVisibility(rawValue: AppPreferences.defaultNotchVisibility) ?? .onHover
    }

    private func storedSize() -> NotchSize {
        NotchSize(rawValue: UserDefaults.standard.string(forKey: "notchSize") ?? "")
            ?? NotchSize(rawValue: AppPreferences.defaultNotchSize) ?? .medium
    }

    private func storedAccent() -> NotchAccentChoice {
        NotchAccentChoice(rawValue: UserDefaults.standard.string(forKey: "notchAccent") ?? "")
            ?? NotchAccentChoice(rawValue: AppPreferences.defaultNotchAccent) ?? .system
    }

    private func storedResetTimeFormat() -> ResetTimeFormat {
        ResetTimeFormat(rawValue: UserDefaults.standard.string(forKey: "notchResetTimeFormat") ?? "")
            ?? .automatic
    }
}
