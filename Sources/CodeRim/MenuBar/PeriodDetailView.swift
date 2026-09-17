import SwiftUI

struct PeriodDetailView: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @EnvironmentObject private var store: UsageStore
    @EnvironmentObject private var profileStore: ProfileUsageStore
    @AppStorage("numberStyle") private var numberStyleRawValue = TokenNumberStyle.compact.rawValue
    @AppStorage("showCachedInput") private var showCachedInput = true

    let period: UsagePeriod
    var scope: UsageHistoryScope = .local
    private let formatter = TokenFormatter()

    var body: some View {
        let usage = store.snapshot.totals(for: period)
        let total = displayedTotal
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text(scope.title)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                Text(total.map(formatted) ?? "—")
                    .font(.system(size: 30, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .contentTransition(.numericText())
                    .animation(reduceMotion ? nil : .easeOut(duration: 0.24), value: total)
                Text(usesProfileTotalForPeriod ? profileTotalLabel : "This Mac total tokens")
                    .foregroundStyle(.secondary)
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("\(scope.title) \(title) total tokens, \(total.map(formatted) ?? "unavailable")")
            .help(
                usesProfileTotalForPeriod
                    ? UsageDisplayPolicy.accountHistoryHelp
                    : UsageDisplayPolicy.localHistoryHelp + " Cached input is already included in Input. Total equals Input plus Output."
            )

            Divider()

            if usesProfileTotalForPeriod, store.snapshot.updatedAt == nil {
                HStack(spacing: 10) {
                    if store.isRefreshing || !store.hasLoadedSnapshot {
                        ProgressView()
                            .controlSize(.small)
                    } else {
                        Image(systemName: "externaldrive.badge.questionmark")
                            .foregroundStyle(.secondary)
                    }
                    VStack(alignment: .leading, spacing: 2) {
                        Text(store.hasLoadedSnapshot ? "No local usage found" : "Reading this Mac's usage")
                            .font(.subheadline.weight(.semibold))
                        Text(store.statusMessage)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                }
                .accessibilityElement(children: .combine)
            } else {
                if usesProfileTotalForPeriod {
                    HStack {
                        Text(localBreakdownLabel)
                            .font(.subheadline.weight(.semibold))
                        Spacer()
                        Text(formatted(usage.totalTokens))
                            .font(.subheadline)
                            .monospacedDigit()
                    }
                    .help(UsageDisplayPolicy.localHistoryHelp)
                }
                detailRow("Input", usage.inputTokens)
                if showCachedInput {
                    detailRow("Cached input", usage.cachedInputTokens)
                }
                detailRow("Output", usage.outputTokens)
            }

            statusLabel

            Spacer(minLength: 0)
        }
        .padding(18)
        .modifier(UsageDetailWidth())
        .frame(minHeight: 240, alignment: .topLeading)
    }

    private var statusSymbol: String {
        store.operationAwareStatusSymbol
    }

    @ViewBuilder
    private var statusLabel: some View {
        if usesProfileTotalForPeriod, let profileSnapshot {
            Label(
                profileStore.status == .ready
                    ? "Profile through \(profileDate(profileSnapshot.statsAsOf))"
                    : "Profile through \(profileDate(profileSnapshot.statsAsOf)) · \(profileStore.statusMessage)",
                systemImage: profileStore.status == .ready
                    ? "checkmark.circle"
                    : "exclamationmark.triangle"
            )
            .font(.caption)
            .foregroundStyle(.secondary)
        } else if usesProfileTotalForPeriod {
            Label(profileStore.statusMessage, systemImage: "person.crop.circle.badge.questionmark")
                .font(.caption)
                .foregroundStyle(.secondary)
        } else if !store.isRefreshing,
                  !store.isImportingHistory,
                  let lastSourceRefreshAt = store.lastSourceRefreshAt,
                  store.snapshot.quality != .stale,
                  store.snapshot.quality != .error {
            Label {
                HStack(spacing: 3) {
                    Text("Updated")
                    Text(lastSourceRefreshAt, style: .relative)
                }
            } icon: {
                Image(systemName: statusSymbol)
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        } else {
            Label(store.statusMessage, systemImage: statusSymbol)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var title: String {
        switch period {
        case .today: "Today"
        case .week: "This Week"
        case .month: "This Month"
        case .allTime: usesProfileTotalForPeriod ? "Lifetime" : "Local History"
        }
    }

    private var localBreakdownLabel: String {
        switch period {
        case .today: "This Mac today"
        case .week: "This Mac this week"
        case .month: "This Mac this month"
        case .allTime: "This Mac local history"
        }
    }

    private var profileSnapshot: ProfileUsageSnapshot? {
        store.provider.supportsAccountTotals && profileStore.isEnabled ? profileStore.snapshot : nil
    }

    private var usesProfileTotalForPeriod: Bool {
        scope == .account
    }

    private var profileTotalLabel: String {
        guard let snapshot = profileSnapshot else { return "Account totals unavailable" }
        return "Profile total through \(profileDate(snapshot.statsAsOf))"
    }

    private var displayedTotal: Int64? {
        UsageDisplayPolicy.displayedTotal(
            for: period,
            scope: scope,
            localSnapshot: store.snapshot,
            profileSnapshot: profileSnapshot
        )
    }

    private func profileDate(_ date: Date) -> String {
        date.formatted(.dateTime.month(.abbreviated).day())
    }

    private func detailRow(_ title: String, _ value: Int64) -> some View {
        HStack {
            Text(title).foregroundStyle(.secondary)
            Spacer()
            Text(formatted(value))
                .monospacedDigit()
                .contentTransition(.numericText())
                .animation(reduceMotion ? nil : .easeOut(duration: 0.2), value: value)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(title), \(formatted(value)) tokens")
    }

    private func formatted(_ value: Int64) -> String {
        formatter.string(
            from: value,
            style: TokenNumberStyle(rawValue: numberStyleRawValue) ?? .compact
        )
    }
}
