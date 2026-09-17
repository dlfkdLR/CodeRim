import AppKit
import SwiftUI

enum MenuPopoverMetrics {
    static let width: CGFloat = 372
    static let detailMaximumHeight: CGFloat = 520
    static let analyticsViewportMaximumHeight: CGFloat = 440
    static let detailHeaderHeight: CGFloat = 44
}

enum MenuPopoverSection: String, CaseIterable, Identifiable {
    case overview
    case codex

    var id: String { rawValue }

    var title: String {
        switch self {
        case .overview: "Token Usage"
        case .codex: "Codex Limits"
        }
    }

    func title(for provider: UsageProvider) -> String {
        self == .codex ? "\(provider.tabTitle) Limits" : title
    }

    var symbol: String {
        switch self {
        case .overview: "square.grid.2x2"
        case .codex: "terminal"
        }
    }

    var categories: [MenuPopoverCategory] {
        switch self {
        case .overview: [.localUsage, .tokenHistory, .explore]
        case .codex: [.accountLimits]
        }
    }
}

enum MenuPopoverCategory: String, CaseIterable, Identifiable {
    case accountLimits
    case localUsage
    case tokenHistory
    case explore

    var id: String { rawValue }

    var title: String {
        switch self {
        case .accountLimits: "Usage limits"
        case .localUsage: "Today"
        case .tokenHistory: "History"
        case .explore: "Details"
        }
    }

    var symbol: String {
        switch self {
        case .localUsage: "chart.bar"
        case .accountLimits: "gauge.with.dots.needle.50percent"
        case .tokenHistory: "clock.arrow.circlepath"
        case .explore: "square.grid.2x2"
        }
    }
}

enum MenuProviderSelection {
    static func apply(_ provider: UsageProvider, selection: inout String,
                      section _: inout MenuPopoverSection) {
        selection = provider.rawValue
    }
}

struct MenuPopoverView: View {
    @StateObject private var navigation: MenuNavigation
    private let accounts: CodexAccountStore
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @EnvironmentObject private var store: UsageStore
    @EnvironmentObject private var limitStore: AccountLimitStore
    @EnvironmentObject private var claude: ClaudeIntegrationStore
    @AppStorage("numberStyle") private var numberStyleRawValue = TokenNumberStyle.compact.rawValue
    @AppStorage("showCachedInput") private var showCachedInput = true
    @AppStorage("showLastUpdated") private var showLastUpdated = true
    @AppStorage("analyticsEnabled") private var analyticsEnabled = AppPreferences.defaultAnalyticsEnabled
    @AppStorage("costEstimatesEnabled") private var costEstimatesEnabled = AppPreferences.defaultCostEstimatesEnabled
    @AppStorage("accountLimitsEnabled") private var accountLimitsEnabled = AppPreferences.defaultAccountLimitsEnabled
    @AppStorage("additionalLimitsEnabled") private var additionalLimitsEnabled = AppPreferences.defaultAdditionalLimitsEnabled
    @AppStorage("resetCreditsEnabled") private var resetCreditsEnabled = AppPreferences.defaultResetCreditsEnabled
    @AppStorage("projectsEnabled") private var projectsEnabled = AppPreferences.defaultProjectsEnabled
    @AppStorage("sessionsEnabled") private var sessionsEnabled = AppPreferences.defaultSessionsEnabled
    @State private var selectedSection = MenuPopoverSection.overview
    @State private var refreshTurns = 0
    @AppStorage("usageProvider") private var usageProvider = UsageProvider.codex.rawValue

    private let formatter = TokenFormatter()

    /// True when the view is hosted in the Settings window's Usage pane rather
    /// than in the menu-bar popover: it then fills the pane instead of pinning
    /// to 372pt, and the footer drops the Quit / Settings / More actions that
    /// only make sense from the menu bar.
    private let embedded: Bool

    init(accounts: CodexAccountStore, navigation: MenuNavigation = MenuNavigation(),
         section: MenuPopoverSection = .overview, embedded: Bool = false) {
        self.accounts = accounts
        self.embedded = embedded
        _navigation = StateObject(wrappedValue: navigation)
        _selectedSection = State(initialValue: section)
    }

    var body: some View {
        VStack(spacing: 0) {
            if let destination = navigation.destination {
                detailHeader(destination)
                Divider()
                destinationView(destination)
                    .id(destination)
            } else {
                if embedded { settingsHeader } else { header }
                Group {
                    switch selectedSection {
                    case .overview:
                        if embedded { UsageSettingsOverview() } else { overviewContent }
                    case .codex:
                        codexContent
                    }
                }
                .id(selectedSection)
                .fixedSize(horizontal: false, vertical: true)
                .transition(.opacity)
                if !embedded || shouldShowStatus {
                    Divider()
                    footer
                }
            }
        }
        .frame(width: embedded ? nil : MenuPopoverMetrics.width,
               alignment: .topLeading)
        .frame(maxWidth: embedded ? .infinity : nil, alignment: .topLeading)
        .fixedSize(horizontal: false, vertical: true)
        .background(.background)
        .environmentObject(navigation)
        .environment(\.usageDetailUsesWindowWidth, embedded)
        .onChange(of: isRefreshing) { _, isRefreshing in
            guard isRefreshing, !reduceMotion else { return }
            refreshTurns += 1
        }
    }

    /// Settings has room for a compact toolbar and a full-width overview.
    /// Keep the account actions and navigation owned by the existing host.
    private var settingsHeader: some View {
        VStack(spacing: 8) {
            HStack(spacing: 16) {
                Menu {
                    ForEach(availableProviders) { provider in
                        Button {
                            MenuProviderSelection.apply(provider, selection: &usageProvider, section: &selectedSection)
                        } label: {
                            Label(provider.tabTitle, systemImage: usageProvider == provider.rawValue ? "checkmark" : provider.symbol)
                        }
                        .keyboardShortcut(provider == .codex ? "1" : "2", modifiers: [.command, .shift])
                        .accessibilityIdentifier("menu.provider.\(provider.rawValue)")
                    }
                } label: {
                    Text(store.provider.tabTitle).fontWeight(.semibold)
                }
                .fixedSize()
                .accessibilityLabel("Usage provider")
                .accessibilityValue(store.provider.tabTitle)
                .accessibilityIdentifier("settings.usage.provider")

                Spacer(minLength: 0)

                Picker("Usage section", selection: $selectedSection) {
                    ForEach(MenuPopoverSection.allCases) { section in
                        Text(section.title(for: store.provider)).tag(section)
                            .keyboardShortcut(section == .overview ? "1" : "2", modifiers: .command)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 246)
                .accessibilityIdentifier("settings.usage.section")
            }
            .controlSize(.regular)
            .padding(.horizontal, 24)

            if store.provider == .codex {
                CodexAccountSwitcher(accounts: accounts, opensManagementDirectly: true)
                    .padding(.horizontal, 6)
            } else {
                claudeAccountBadge.padding(.horizontal, 6)
            }
            Divider()
        }
        .padding(.top, 20)
    }

    private func detailHeader(_ destination: MenuDestination) -> some View {
        HStack(spacing: 8) {
            Button {
                navigation.back()
            } label: {
                Image(systemName: "chevron.left")
                    .font(.subheadline.weight(.medium))
                    .frame(width: 28, height: 28)
                    .contentShape(Rectangle())
            }
            .keyboardShortcut("[", modifiers: .command)
            .accessibilityLabel("Back")
            .accessibilityIdentifier("menu.navigation.back")
            .help("Back")
            Text(destination.title(usesProfileTotals: false))
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
                .accessibilityAddTraits(.isHeader)
            Spacer(minLength: 0)
            Text(store.provider.title)
                .font(.caption)
                .foregroundStyle(.secondary)
            if destination == .limits {
                Button {
                    Task { await refreshCurrentLimits() }
                } label: {
                    Image(systemName: "arrow.clockwise")
                        .frame(width: 28, height: 28)
                }
                .disabled(currentLimitsRefreshing)
                .accessibilityLabel("Refresh account limits")
                .help("Refresh account limits")
            }
        }
        .buttonStyle(MenuInteractionStyle())
        .padding(.horizontal, 12)
        .frame(height: MenuPopoverMetrics.detailHeaderHeight)
    }

    @ViewBuilder
    private func destinationView(_ destination: MenuDestination) -> some View {
        switch destination {
        case .limits:
            AccountLimitsView(
                snapshot: currentLimitSnapshot,
                status: currentLimitStatus,
                statusMessage: currentLimitStatusMessage,
                isRefreshing: currentLimitsRefreshing,
                provider: store.provider,
                refresh: refreshCurrentLimits
            )
        case .usage: UsageAnalyticsView()
        case .projects: ProjectsAnalyticsView()
        case .sessions: SessionsAnalyticsView()
        case let .period(period, scope): PeriodDetailView(period: period, scope: scope)
        case let .project(id, range): ProjectDetailView(id: id, range: range)
        case let .session(id, range): SessionDetailView(id: id, range: range)
        case let .model(id, range): ModelDetailView(id: id, range: range)
        }
    }

    private var overviewContent: some View {
        VStack(spacing: 0) {
            localUsageSection
            if analyticsEnabled {
                Divider()
                exploreSection
            }
        }
    }

    @ViewBuilder
    private var codexContent: some View {
        if currentLimitsEnabled {
            accountLimitsPreview
        } else {
            VStack(alignment: .leading, spacing: 8) {
                Label("\(store.provider.tabTitle) Limits are turned off", systemImage: "gauge.with.dots.needle.0percent")
                    .font(.headline)
                Text(store.provider == .codex
                     ? "Enable Account Limits in Settings."
                     : "Enable Claude and add an account in Settings.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Button("Open Settings") {
                    SettingsWindowController.shared.present(selecting: .provider(store.provider))
                }
                .buttonStyle(.link)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(18)
        }
    }

    private var accountLimitsPreview: some View {
        VStack(alignment: .leading, spacing: 0) {
            MenuLink(destination: .limits) {
                categoryHeader(.accountLimits, showsDisclosure: true)
            }
            .accessibilityIdentifier("menu.category.accountLimits")
            .accessibilityHint("Open all account limit details")

            VStack(alignment: .leading, spacing: 12) {
                if let snapshot = currentLimitSnapshot {
                    let windows = visibleAccountLimitWindows(snapshot.windows)
                    if windows.isEmpty {
                        compactLimitStatus(
                            "No \(store.provider.tabTitle) usage limits were reported.",
                            symbol: "gauge.with.dots.needle.0percent"
                        )
                    } else {
                        ForEach(Array(windows.prefix(3))) { window in
                            compactLimitRow(window)
                        }
                        if windows.count > 3 {
                            MenuLink(destination: .limits) {
                                Text("View \(windows.count - 3) more")
                                    .font(.caption)
                                    .padding(.vertical, 6)
                            }
                        }
                        if currentLimitStatus == .stale {
                            compactLimitStatus(
                                store.provider == .codex
                                    ? "Offline · showing last known limits"
                                    : "Last known limits · use Claude Code to update",
                                symbol: store.provider == .codex
                                    ? "wifi.slash"
                                    : "clock.badge.exclamationmark"
                            )
                        }
                    }
                    if store.provider == .codex, resetCreditsEnabled, let credits = snapshot.resetCredits {
                        resetCreditsSummary(credits)
                    }
                } else if currentLimitsRefreshing || currentLimitStatus == .loading {
                    HStack(spacing: 9) {
                        ProgressView().controlSize(.small)
                        Text("Reading account limits…")
                            .foregroundStyle(.secondary)
                    }
                    .font(.caption)
                    .frame(minHeight: 36)
                } else {
                    compactLimitStatus(currentLimitStatusMessage, symbol: "exclamationmark.triangle")
                }
            }
            .padding(.horizontal, 18)
            .padding(.bottom, 16)
        }
    }

    private var header: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                ForEach(availableProviders) { provider in
                    providerTab(provider)
                }
            }
            .padding(.horizontal, 12)
            .padding(.top, 12)
            .padding(.bottom, 8)

            if store.provider == .codex {
                CodexAccountSwitcher(accounts: accounts)
            } else {
                claudeAccountBadge
            }

            HStack(spacing: 8) {
                ForEach(MenuPopoverSection.allCases) { section in
                    sectionTab(section)
                }
            }
            .padding(.horizontal, 18)
            .padding(.bottom, 8)

            Divider()
        }
    }

    // Mirrors CodexAccountSwitcher's row so the connected account reads the same
    // on both providers. Claude has no account switching, so the trailing slot
    // shows the plan instead of a Switch menu.
    private var claudeAccountBadge: some View {
        HStack(spacing: 8) {
            Image(systemName: "person.crop.circle")
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text(claude.account?.displayName ?? "Claude account")
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: .infinity, alignment: .leading)
                .help(claude.account?.displayName ?? "Claude account")
            if let plan = claude.account?.planName {
                Text(plan)
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("Plan, \(plan)")
            }
            Button("Switch") { ClaudeAccountsWindowController.shared.show() }
                .buttonStyle(.borderless)
                .frame(minHeight: 28)
                .accessibilityLabel("Switch Claude account")
                .accessibilityIdentifier("menu.claudeAccountSwitcher")
        }
        .font(.subheadline)
        .padding(.horizontal, 6)
        .frame(minHeight: 36)
        .padding(.horizontal, 12)
        .padding(.vertical, 4)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("menu.claudeAccount")
        .accessibilityLabel("Claude account, \(claude.account?.displayName ?? "not connected")")
    }

    private func providerTab(_ provider: UsageProvider) -> some View {
        let isSelected = usageProvider == provider.rawValue
        return Button {
            withAnimation(reduceMotion ? nil : .easeOut(duration: 0.18)) {
                MenuProviderSelection.apply(provider, selection: &usageProvider, section: &selectedSection)
            }
        } label: {
            VStack(spacing: 1) {
                ProviderLogo(provider: provider)
                    .accessibilityHidden(true)
                Text(provider.tabTitle)
                    .font(.subheadline.weight(isSelected ? .semibold : .medium))
                    .lineLimit(1)
            }
            .foregroundStyle(
                isSelected ? Color(nsColor: .alternateSelectedControlTextColor) : Color.secondary
            )
            .frame(maxWidth: .infinity, minHeight: 36)
            .background(
                isSelected ? Color(nsColor: .controlAccentColor) : Color.clear,
                in: RoundedRectangle(cornerRadius: 10, style: .continuous)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(MenuInteractionStyle())
        .keyboardShortcut(provider == .codex ? "1" : "2", modifiers: [.command, .shift])
        .accessibilityLabel(provider.title)
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
        .accessibilityHint("Show \(provider.title) usage")
        .accessibilityIdentifier("menu.provider.\(provider.rawValue)")
    }

    private func sectionTab(_ section: MenuPopoverSection) -> some View {
        let isSelected = selectedSection == section
        return Button {
            withAnimation(reduceMotion ? nil : .easeOut(duration: 0.18)) {
                selectedSection = section
            }
        } label: {
            HStack(spacing: 6) {
                Image(systemName: section.symbol)
                    .font(.system(size: 14, weight: .medium))
                Text(section.title(for: store.provider))
                    .font(.subheadline.weight(isSelected ? .semibold : .medium))
                    .lineLimit(1)
                    .minimumScaleFactor(0.86)
            }
            .foregroundStyle(isSelected ? Color.primary : Color.secondary)
            .frame(maxWidth: .infinity, minHeight: 36)
            .overlay(alignment: .bottom) {
                Capsule()
                    .fill(isSelected ? Color.accentColor : Color.clear)
                    .frame(width: 52, height: 2)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(MenuInteractionStyle())
        .keyboardShortcut(section == .overview ? "1" : "2", modifiers: .command)
        .accessibilityLabel(section.title(for: store.provider))
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
        .accessibilityHint("Show \(section.title(for: store.provider).lowercased())")
        .accessibilityIdentifier("menu.section.\(section.rawValue)")
    }

    private var localUsageSection: some View {
        VStack(spacing: 0) {
            categoryHeader(.localUsage, context: "This Mac")
                .help(UsageDisplayPolicy.localHistoryHelp)
            localUsageSummary
            if store.provider == .codex || store.snapshot.updatedAt != nil {
                Divider().padding(.leading, 18)
                categoryHeader(.tokenHistory, context: "This Mac")
                    .help(UsageDisplayPolicy.localHistoryHelp)
                localPeriodLinks

            }
        }
    }

    private var localUsageSummary: some View {
        Group {
            if store.snapshot.updatedAt == nil {
                HStack(spacing: 12) {
                    if store.isRefreshing {
                        ProgressView()
                            .controlSize(.small)
                    } else {
                        Image(systemName: "chart.bar.xaxis")
                            .foregroundStyle(.secondary)
                    }
                    VStack(alignment: .leading, spacing: 3) {
                        Text(store.isRefreshing ? "Reading local usage" : "No local usage yet")
                            .font(.headline)
                        Text(store.provider == .claude && store.hasLoadedSnapshot
                             && store.snapshot.quality == .unavailable && !store.isRefreshing
                             ? "Start a Claude Code session, then Refresh."
                             : store.statusMessage)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                }
                .frame(minHeight: 96)
                .accessibilityElement(children: .combine)
            } else {
                VStack(alignment: .leading, spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(formatted(store.snapshot.today.totalTokens))
                            .font(.system(size: 32, weight: .semibold, design: .rounded))
                            .monospacedDigit()
                            .contentTransition(.numericText())
                            .animation(
                                reduceMotion ? nil : .easeOut(duration: 0.24),
                                value: store.snapshot.today.totalTokens
                            )
                    }
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel("This Mac total tokens today, \(formatted(store.snapshot.today.totalTokens))")
                    .help("Cached input is already included in Input. Total equals Input plus Output.")

                    HStack(spacing: 0) {
                        metricSummary("Input", value: store.snapshot.today.inputTokens, symbol: "arrow.up")
                        if showCachedInput {
                            Divider().frame(height: 34)
                            metricSummary(
                                "Cached input",
                                value: store.snapshot.today.cachedInputTokens,
                                symbol: "bolt.horizontal"
                            )
                        }
                        Divider().frame(height: 34)
                        metricSummary("Output", value: store.snapshot.today.outputTokens, symbol: "arrow.down")
                    }
                    if analyticsEnabled, costEstimatesEnabled, store.provider.supportsCostEstimates,
                       let analytics = store.analyticsSnapshots[.today] {
                        EstimatedCostLabel(snapshot: analytics, showsUnavailable: false)
                    }
                }
            }
        }
        .padding(.horizontal, 18)
        .padding(.bottom, 12)
    }

    private var localPeriodLinks: some View {
        HStack(spacing: 8) {
            periodRowLink("This Week", period: .week)
            periodRowLink("This Month", period: .month)
            periodRowLink("Local History", period: .allTime)
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 12)
    }

    private var exploreSection: some View {
        exploreLinks
            .padding(.top, 8)
    }

    private var exploreLinks: some View {
        VStack(spacing: 0) {
            exploreRowLink("Usage", symbol: "chart.xyaxis.line", destination: .usage)
            if projectsEnabled {
                exploreRowLink("Projects", symbol: "folder", destination: .projects)
            }
            if sessionsEnabled {
                exploreRowLink("Sessions", symbol: "text.bubble", destination: .sessions)
            }
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 12)
    }

    private var footer: some View {
        VStack(spacing: 8) {
            if shouldShowStatus {
                HStack {
                    Label {
                        // Token totals and freshness always describe the same local scan.
                        if selectedSection == .codex {
                            if let snapshot = currentLimitSnapshot {
                                TimelineView(.periodic(from: .now, by: 30)) { context in
                                    Text(LimitFreshness.text(fetchedAt: snapshot.fetchedAt, now: context.date,
                                                            stale: currentLimitStatus == .stale))
                                }
                            } else {
                                Text(currentLimitStatusMessage)
                            }
                        } else if showsRelativeUpdate, let lastSourceRefreshAt = store.lastSourceRefreshAt {
                            HStack(spacing: 3) {
                                Text("Updated")
                                Text(lastSourceRefreshAt, style: .relative)
                            }
                        } else {
                            Text(store.statusMessage)
                        }
                    } icon: {
                        Image(systemName: statusSymbol)
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .contentTransition(.opacity)
                    .animation(
                        reduceMotion ? nil : .easeOut(duration: 0.18),
                        value: store.statusMessage
                    )
                    Spacer()
                }
            }

            if !embedded {
            HStack(spacing: 18) {
                Button {
                    Task {
                        guard store.provider == .codex else {
                            async let localRefresh: Void = store.refresh()
                            async let limitsRefresh: Void = claude.refresh()
                            _ = await (localRefresh, limitsRefresh)
                            return
                        }
                        async let localRefresh: Void = store.refresh()
                        async let limitsRefresh: Void = limitStore.refresh()
                        _ = await (localRefresh, limitsRefresh)
                    }
                } label: {
                    Label {
                        Text("Refresh")
                    } icon: {
                        Image(systemName: "arrow.clockwise")
                            .rotationEffect(.degrees(Double(refreshTurns) * 360))
                            .animation(
                                reduceMotion ? nil : .easeInOut(duration: 0.5),
                                value: refreshTurns
                            )
                    }
                    .padding(.horizontal, 6)
                    .frame(minHeight: 28)
                }
                .disabled(isRefreshing || store.isMaintainingData || store.isImportingHistory)
                .keyboardShortcut("r", modifiers: .command)
                .help("Refresh usage")

                Button {
                    SettingsWindowController.shared.present()
                } label: {
                    Label("Settings", systemImage: "gearshape")
                        .padding(.horizontal, 6)
                        .frame(minHeight: 28)
                }
                .keyboardShortcut(",", modifiers: .command)
                .help("Open Settings")

                Spacer()

                Menu {
                    if store.provider == .codex {
                        Button("Open OpenAI Status") {
                            open("https://status.openai.com")
                        }
                    }
                    Button("Open CodeRim on GitHub") {
                        open("https://github.com/dlfkdLR/CodeRim")
                    }
                    Button("Check for Updates…") {
                        UpdateService.shared.checkForUpdates()
                    }
                    .disabled(!UpdateService.shared.isAvailable)
                    Divider()
                    Button("Quit CodeRim") {
                        NSApplication.shared.terminate(nil)
                    }
                    .keyboardShortcut("q", modifiers: .command)
                } label: {
                    Label("More", systemImage: "ellipsis.circle")
                        .font(.caption.weight(.medium))
                        .frame(minHeight: 28)
                }
                .menuStyle(.borderlessButton)
                .controlSize(.small)
                .menuIndicator(.hidden)
                .help("More actions")
            }
            .buttonStyle(MenuInteractionStyle())
            .font(.caption.weight(.medium))
            .frame(minHeight: 28)
            }
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 10)
    }

    private var shouldShowStatus: Bool {
        if selectedSection == .codex {
            return currentLimitsEnabled
        }
        let qualityNeedsStatus = switch store.snapshot.quality {
        case .stale, .unavailable, .error: true
        case .exact, .partial: false
        }
        return showLastUpdated || isRefreshing || store.isImportingHistory || qualityNeedsStatus
    }

    private var statusSymbol: String {
        if isRefreshing { return "arrow.triangle.2.circlepath" }
        if selectedSection == .codex {
            return switch currentLimitStatus {
            case .ready: "checkmark.circle"
            case .stale: "clock.badge.exclamationmark"
            case .disabled: "gauge.with.dots.needle.0percent"
            case .loading: "arrow.triangle.2.circlepath"
            case .unavailable: "exclamationmark.triangle"
            }
        }
        // Match the footer: the "Updated <relative>" line owns the icon when it shows.
        if showsRelativeUpdate { return store.operationAwareStatusSymbol }
        return store.operationAwareStatusSymbol
    }

    /// The shared "Updated <relative>" freshness line shows whenever a healthy
    /// local scan is on record, on both providers and regardless of profile mode.
    private var showsRelativeUpdate: Bool {
        showLastUpdated
            && !store.isRefreshing
            && !store.isImportingHistory
            && store.lastSourceRefreshAt != nil
            && store.snapshot.quality != .stale
            && store.snapshot.quality != .error
    }

    private var isRefreshing: Bool {
        selectedSection == .overview ? store.isRefreshing : currentLimitsRefreshing
    }

    private func categoryHeader(
        _ category: MenuPopoverCategory,
        context: String? = nil,
        showsDisclosure: Bool = false
    ) -> some View {
        HStack(spacing: 8) {
            Image(systemName: category.symbol)
                .frame(width: 14)
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text(category.title)
                .font(.subheadline.weight(.semibold))
            Spacer(minLength: 8)
            if let context {
                Text(context)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            if showsDisclosure {
                Image(systemName: "chevron.right")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(.tertiary)
                    .accessibilityHidden(true)
            }
        }
        .padding(.horizontal, 18)
        .padding(.top, 14)
        .padding(.bottom, 9)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isHeader)
        .accessibilityIdentifier("menu.category.\(category.rawValue)")
    }

    private func metricSummary(_ title: String, value: Int64, symbol: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 4) {
                Image(systemName: symbol)
                    .accessibilityHidden(true)
                Text(title)
                    .lineLimit(1)
            }
            .font(.caption2)
            .foregroundStyle(.secondary)
            Text(formatted(value))
                .font(.subheadline.weight(.medium))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.72)
                .contentTransition(.numericText())
                .animation(reduceMotion ? nil : .easeOut(duration: 0.2), value: value)
        }
        .padding(.horizontal, 7)
        .frame(maxWidth: .infinity, minHeight: 38, alignment: .leading)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(title), \(formatted(value)) tokens")
    }

    private func periodRowLink(_ title: String, period: UsagePeriod) -> some View {
        let value = store.snapshot.totals(for: period).totalTokens
        let text = formatted(value)
        return MenuLink(destination: .period(period)) {
            VStack(alignment: .leading, spacing: 7) {
                HStack(spacing: 4) {
                    Text(title)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                    Spacer(minLength: 0)
                    Image(systemName: "chevron.right")
                        .font(.system(size: 8, weight: .semibold))
                        .foregroundStyle(.tertiary)
                        .accessibilityHidden(true)
                }
                Text(text)
                    .font(.subheadline.weight(.medium))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                    .contentTransition(.numericText())
                    .animation(reduceMotion ? nil : .easeOut(duration: 0.2), value: value)
            }
            .padding(.horizontal, 6)
            .frame(maxWidth: .infinity, minHeight: 54, alignment: .leading)
            .contentShape(Rectangle())
        }
        .disabled(store.snapshot.updatedAt == nil)
        .accessibilityLabel("This Mac \(title), \(text) tokens")
        .accessibilityHint("Open \(title.lowercased()) details")
        .accessibilityIdentifier("menu.usage.local.\(period.rawValue)")
    }

    private func formatted(_ value: Int64) -> String {
        formatter.string(
            from: value,
            style: TokenNumberStyle(rawValue: numberStyleRawValue) ?? .compact
        )
    }

    private func visibleAccountLimitWindows(_ windows: [AccountLimitWindow]) -> [AccountLimitWindow] {
        guard store.provider == .codex else { return windows }
        return AccountLimitPresentation.visibleWindows(windows, includesAdditional: additionalLimitsEnabled)
    }

    private var availableProviders: [UsageProvider] {
        claude.isAvailable ? UsageProvider.allCases : [.codex]
    }

    private var currentLimitsEnabled: Bool {
        store.provider == .codex ? accountLimitsEnabled : claude.isAvailable
    }

    private var currentLimitSnapshot: AccountLimitsSnapshot? {
        store.provider == .codex ? limitStore.snapshot : claude.snapshot
    }

    private var currentLimitStatus: AccountLimitStatus {
        guard store.provider == .claude else { return limitStore.status }
        return switch claude.status {
        case .disabled: .disabled
        case .checking: .loading
        case .ready: .ready
        case .stale: .stale
        case .waitingForLimits, .needsAccount, .unavailable: .unavailable
        }
    }

    private var currentLimitStatusMessage: String {
        store.provider == .codex ? limitStore.statusMessage : claude.statusMessage
    }

    private var currentLimitsRefreshing: Bool {
        store.provider == .codex ? limitStore.isRefreshing : claude.isRefreshing
    }

    private func refreshCurrentLimits() async {
        if store.provider == .codex {
            await limitStore.refresh()
        } else {
            await claude.refresh()
        }
    }

    @ViewBuilder
    private func compactLimitRow(_ window: AccountLimitWindow) -> some View {
        let remaining = window.remainingPercent
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 6) {
                Text(limitTitle(window))
                    .font(.caption.weight(.semibold))
                    .lineLimit(1)
                if remaining <= 25 {
                    Label(remaining <= 10 ? "Critical" : "Low", systemImage: "exclamationmark.triangle.fill")
                        .labelStyle(.titleOnly)
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(remaining <= 10 ? .red : .orange)
                }
                Spacer()
                Text("\(Int(remaining.rounded()))% left")
                    .font(.caption.weight(.semibold).monospacedDigit())
            }
            ProgressView(value: remaining, total: 100)
                .tint(limitAccent(remaining))
                .accessibilityLabel("\(limitTitle(window)) remaining")
                .accessibilityValue("\(Int(remaining.rounded())) percent")
            HStack(spacing: 4) {
                if let reset = window.resetsAt {
                    TimelineView(.periodic(from: .now, by: 30)) { context in
                        Text(ResetCopy.text(for: reset, now: context.date, format: .remaining))
                    }
                }
            }
            .font(.caption2)
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .minimumScaleFactor(0.82)
        }
        .accessibilityElement(children: .combine)
    }

    private func compactLimitStatus(_ message: String, symbol: String) -> some View {
        Label(message, systemImage: symbol)
            .font(.caption)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, minHeight: 36, alignment: .leading)
    }

    private func resetCreditsSummary(_ credits: ResetCreditSummary) -> some View {
        HStack(spacing: 8) {
            Image(systemName: "arrow.counterclockwise.circle")
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text("Reset credits")
                .font(.caption.weight(.semibold))
            Spacer()
            Text(credits.unlimited ? "Unlimited" : credits.availableCount?.formatted() ?? "Available")
                .font(.caption.weight(.semibold).monospacedDigit())
            if let expiration = credits.expiresAt {
                Text("· \(expiration, format: .dateTime.month(.abbreviated).day())")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.top, 2)
        .accessibilityElement(children: .combine)
    }

    private func limitTitle(_ window: AccountLimitWindow) -> String {
        // A provider's own primary limit ("Codex" / "Claude") adds nothing beyond
        // the window label; only genuinely distinct sub-limits keep their name.
        ["codex", "claude"].contains(window.displayName.lowercased())
            ? window.windowLabel
            : "\(window.displayName) · \(window.windowLabel)"
    }

    private func limitAccent(_ remaining: Double) -> Color {
        if remaining <= 10 { return .red }
        if remaining <= 25 { return .orange }
        return .accentColor
    }

    private func exploreRowLink(
        _ title: String,
        symbol: String,
        destination: MenuDestination
    ) -> some View {
        MenuLink(destination: destination) {
            HStack(spacing: 10) {
                Image(systemName: symbol)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(.secondary)
                    .frame(width: 18)
                    .accessibilityHidden(true)
                Text(title)
                    .font(.subheadline)
                Spacer(minLength: 12)
                Image(systemName: "chevron.right")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(.tertiary)
                    .accessibilityHidden(true)
            }
            .padding(.horizontal, 6)
            .frame(maxWidth: .infinity, minHeight: 38)
            .contentShape(Rectangle())
        }
        .accessibilityLabel("Open \(title)")
        .accessibilityHint(store.provider.analyticsHint(for: title))
    }

    private func open(_ rawURL: String) {
        guard let url = URL(string: rawURL) else { return }
        NSWorkspace.shared.open(url)
    }
}
