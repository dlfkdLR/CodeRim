import SwiftUI

/// A window-sized summary. Totals use the same display policy as the compact
/// menu: every token total uses the live local session history.
struct UsageSettingsOverview: View {
    @EnvironmentObject private var store: UsageStore
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @AppStorage("numberStyle") private var numberStyle = TokenNumberStyle.compact.rawValue
    @AppStorage("showCachedInput") private var showCachedInput = true
    @AppStorage("analyticsEnabled") private var analyticsEnabled = AppPreferences.defaultAnalyticsEnabled
    @AppStorage("costEstimatesEnabled") private var costEstimatesEnabled = AppPreferences.defaultCostEstimatesEnabled
    @AppStorage("projectsEnabled") private var projectsEnabled = AppPreferences.defaultProjectsEnabled
    @AppStorage("sessionsEnabled") private var sessionsEnabled = AppPreferences.defaultSessionsEnabled

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            today

            if store.provider == .codex || store.snapshot.updatedAt != nil {
                Divider()
                history
            }

            if analyticsEnabled {
                Divider()
                HStack(spacing: 12) {
                    detailLink("Usage", symbol: "chart.xyaxis.line", destination: .usage)
                    if projectsEnabled {
                        detailLink("Projects", symbol: "folder", destination: .projects)
                    }
                    if sessionsEnabled {
                        detailLink("Sessions", symbol: "text.bubble", destination: .sessions)
                    }
                }
            }
        }
        .padding(24)
        .frame(maxWidth: .infinity, alignment: .topLeading)
    }

    private var today: some View {
        VStack(alignment: .leading, spacing: 18) {
            sectionHeading("Today", context: "This Mac")
                .help(UsageDisplayPolicy.localHistoryHelp)
            if store.snapshot.updatedAt != nil {
                ViewThatFits(in: .horizontal) {
                    HStack(alignment: .center, spacing: 32) {
                        todayTotal.frame(minWidth: 220, maxWidth: .infinity, alignment: .leading)
                        breakdown.frame(width: 240)
                    }
                    VStack(alignment: .leading, spacing: 20) {
                        todayTotal
                        breakdown
                    }
                }
                if analyticsEnabled, costEstimatesEnabled, store.provider.supportsCostEstimates,
                   let analytics = store.analyticsSnapshots[.today] {
                    EstimatedCostLabel(snapshot: analytics, showsUnavailable: false)
                }
            } else {
                HStack(spacing: 12) {
                    if store.isRefreshing {
                        ProgressView().controlSize(.small)
                    } else {
                        Image(systemName: "chart.bar.xaxis").foregroundStyle(.secondary)
                    }
                    VStack(alignment: .leading, spacing: 5) {
                        Text(store.isRefreshing ? "Reading local usage" : "No local usage yet")
                            .font(.headline)
                        Text(store.provider == .claude && store.hasLoadedSnapshot
                             && store.snapshot.quality == .unavailable && !store.isRefreshing
                             ? "Start a Claude Code session, then Refresh." : store.statusMessage)
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity, minHeight: 100, alignment: .leading)
                .accessibilityElement(children: .combine)
            }
        }
    }

    private var todayTotal: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(formatted(store.snapshot.today.totalTokens))
                .font(.system(size: 42, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.55)
                .contentTransition(.numericText())
                .animation(reduceMotion ? nil : .easeOut(duration: 0.24), value: store.snapshot.today.totalTokens)
            Text("tokens").font(.callout).foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("This Mac total tokens today, \(formatted(store.snapshot.today.totalTokens))")
        .help("Cached input is already included in Input. Total equals Input plus Output.")
    }

    private var breakdown: some View {
        VStack(spacing: 10) {
            metric("Input", value: store.snapshot.today.inputTokens, symbol: "arrow.up")
            if showCachedInput {
                metric("Cached input", value: store.snapshot.today.cachedInputTokens, symbol: "bolt.horizontal")
            }
            metric("Output", value: store.snapshot.today.outputTokens, symbol: "arrow.down")
        }
        .help("Cached input is included in Input; it is not added again to the total.")
    }

    private func metric(_ title: String, value: Int64, symbol: String) -> some View {
        HStack(spacing: 12) {
            Label(title, systemImage: symbol).foregroundStyle(.secondary)
            Spacer(minLength: 8)
            Text(formatted(value)).monospacedDigit()
        }
        .font(.callout)
        .accessibilityElement(children: .combine)
    }

    private var history: some View {
        VStack(alignment: .leading, spacing: 14) {
            sectionHeading("History", context: "This Mac")
                .help(UsageDisplayPolicy.localHistoryHelp)
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 16) {
                    periodLink("This Week", period: .week)
                    Divider().frame(height: 64)
                    periodLink("This Month", period: .month)
                    Divider().frame(height: 64)
                    periodLink("Local History", period: .allTime)
                }
                VStack(alignment: .leading, spacing: 12) {
                    periodLink("This Week", period: .week)
                    periodLink("This Month", period: .month)
                    periodLink("Local History", period: .allTime)
                }
            }
        }
    }

    private func periodLink(_ title: String, period: UsagePeriod) -> some View {
        let total = store.snapshot.totals(for: period).totalTokens
        let text = formatted(total)
        return MenuLink(destination: .period(period)) {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 8) {
                    Text(title).font(.callout).foregroundStyle(.secondary)
                    Spacer(minLength: 0)
                    Image(systemName: "chevron.right")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundStyle(.secondary)
                        .accessibilityHidden(true)
                }
                Text(text)
                    .font(.system(size: 20, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
            }
            .padding(.vertical, 6)
            .frame(minWidth: 140, maxWidth: .infinity, alignment: .leading)
        }
        .disabled(store.snapshot.updatedAt == nil)
        .accessibilityLabel("This Mac \(title), \(text) tokens")
        .accessibilityHint("Open \(title.lowercased()) details")
        .accessibilityIdentifier("settings.usage.local.\(period.rawValue)")
    }

    private func sectionHeading(_ title: String, context: String) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title).font(.headline).accessibilityAddTraits(.isHeader)
            Spacer(minLength: 12)
            Text(context).font(.caption).foregroundStyle(.secondary)
        }
    }

    private func detailLink(_ title: String, symbol: String, destination: MenuDestination) -> some View {
        MenuLink(destination: destination) {
            HStack(spacing: 8) {
                Label(title, systemImage: symbol)
                Spacer(minLength: 0)
                Image(systemName: "chevron.right")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
            }
            .font(.callout)
            .padding(12)
            .frame(maxWidth: .infinity)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
        }
        .accessibilityLabel("Open \(title)")
        .accessibilityHint(store.provider.analyticsHint(for: title))
    }

    private func formatted(_ value: Int64) -> String {
        TokenFormatter().string(from: value, style: TokenNumberStyle(rawValue: numberStyle) ?? .compact)
    }
}
