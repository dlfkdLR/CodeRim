import Charts
import SwiftUI

/// GPT-style analytics within the Settings pane's single outer scroll view.
struct UsageSettingsAnalytics: View {
    @EnvironmentObject private var store: UsageStore
    @EnvironmentObject private var navigation: MenuNavigation
    @AppStorage("numberStyle") private var numberStyle = TokenNumberStyle.compact.rawValue
    @AppStorage("showCachedInput") private var showsCachedInput = true
    @AppStorage("sessionsEnabled") private var sessionsEnabled = AppPreferences.defaultSessionsEnabled
    @EnvironmentObject private var state: SettingsUsageAnalyticsState

    private var range: AnalyticsRange { navigation.usageRange == .today ? .sevenDays : navigation.usageRange }
    private var snapshot: AnalyticsSnapshot? { store.analyticsSnapshots[range] }
    private let colors: [Color] = [.accentColor, .orange, .green, .pink, .teal, .yellow]

    var body: some View {
        VStack(alignment: .leading, spacing: 28) {
            totalUsage
            if let snapshot, snapshot.quality != .unavailable, snapshot.quality != .error {
                if sessionsEnabled { topSessions(snapshot) }
                localHistory
                modelActivity(snapshot)
            }
        }
        .padding(24)
        .frame(maxWidth: 960, alignment: .leading)
        .frame(maxWidth: .infinity, alignment: .center)
        .task(id: range) { await store.refreshAnalytics(range: range) }
        .onChange(of: range) { _, _ in
            state.selectedDate = nil
            state.showsAllSessions = false
            state.expandedSessions = []
        }
        .accessibilityIdentifier("settings.usage.analytics")
    }

    private var totalUsage: some View {
        VStack(alignment: .leading, spacing: 12) {
            ViewThatFits(in: .horizontal) {
                HStack(alignment: .bottom, spacing: 16) {
                    heading("Total usage", subtitle: "Token activity on this Mac")
                    Spacer(minLength: 12)
                    chartControls
                }
                VStack(alignment: .leading, spacing: 12) {
                    heading("Total usage", subtitle: "Token activity on this Mac")
                    chartControls
                }
            }
            if let snapshot, snapshot.quality != .unavailable, snapshot.quality != .error {
                let data = UsageAnalyticsPresentation(snapshot: snapshot, grouping: state.grouping,
                                                      showsCachedInput: showsCachedInput)
                card {
                    VStack(alignment: .leading, spacing: 18) {
                        HStack(alignment: .firstTextBaseline) {
                            metric(snapshot.usage.totalTokens, label: "Tokens")
                            Spacer()
                            Text(snapshot.through, format: .dateTime.month(.abbreviated).day().hour().minute())
                                .font(.caption).foregroundStyle(.secondary)
                                .help("Updated \(snapshot.through.formatted())")
                        }
                        if snapshot.usage.isZero {
                            placeholder("No usage in this period", symbol: "chart.bar.xaxis", loading: false)
                        } else {
                            usageChart(data, line: false)
                            HStack {
                                Text(data.bucket(at: state.selectedDate)?.start.formatted(date: .abbreviated, time: .omitted)
                                     ?? "\(range == .sevenDays ? "7" : "30")-day total")
                                Spacer()
                                if state.selectedDate != nil {
                                    Button("Clear selection") { state.selectedDate = nil }.buttonStyle(.plain)
                                }
                                Text("Share of tokens")
                            }
                            .font(.caption).foregroundStyle(.secondary)
                            legend(data.series(for: data.bucket(at: state.selectedDate)))
                        }
                        if let status = snapshotStatus(snapshot) {
                            Label(status, systemImage: "exclamationmark.triangle")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .padding(18)
                }
            } else {
                card {
                    placeholder(store.isAnalyticsRefreshing ? "Reading usage…" : "Usage unavailable",
                                symbol: "chart.bar.xaxis", loading: store.isAnalyticsRefreshing)
                        .overlay(alignment: .bottom) {
                            if !store.isAnalyticsRefreshing {
                                Text(store.analyticsStatusMessage)
                                    .font(.caption).foregroundStyle(.secondary).padding(.bottom, 18)
                            }
                        }
                }
            }
        }
    }

    private var chartControls: some View {
        HStack(spacing: 8) {
            Picker("Date range", selection: Binding(get: { range }, set: { navigation.usageRange = $0 })) {
                Text("7 days").tag(AnalyticsRange.sevenDays)
                Text("30 days").tag(AnalyticsRange.thirtyDays)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 128)
            .accessibilityIdentifier("settings.usage.range")
            Picker("Group usage", selection: $state.grouping) {
                ForEach(SettingsUsageGrouping.allCases) { Text($0.title).tag($0) }
            }
            .labelsHidden().frame(width: 148)
            .accessibilityIdentifier("settings.usage.grouping")
        }
        .controlSize(.small)
    }

    private func usageChart(_ data: UsageAnalyticsPresentation, line: Bool) -> some View {
        Chart(data.points) { point in
            if line {
                LineMark(x: .value("Date", point.date, unit: .day), y: .value("Tokens", point.tokens))
                    .foregroundStyle(by: .value("Series", point.seriesID))
                    .lineStyle(StrokeStyle(lineWidth: 2))
                    .symbol(by: .value("Series", point.seriesID))
                    .symbolSize(14)
                    .accessibilityLabel("\(point.title), \(point.date.formatted(date: .abbreviated, time: .omitted))")
                    .accessibilityValue("\(point.tokens.formatted()) tokens")
            } else {
                BarMark(x: .value("Date", point.date, unit: .day), y: .value("Tokens", point.tokens), width: .ratio(0.55))
                    .foregroundStyle(by: .value("Series", point.seriesID))
                    .cornerRadius(2)
                    .accessibilityLabel("\(point.title), \(point.date.formatted(date: .abbreviated, time: .omitted))")
                    .accessibilityValue("\(point.tokens.formatted()) tokens")
            }
        }
        .chartForegroundStyleScale(domain: data.series.map(\.id), range: Array(colors.prefix(data.series.count)))
        .chartLegend(.hidden)
        .chartXAxis {
            AxisMarks(values: .stride(by: .day, count: range == .sevenDays ? 3 : 10)) {
                AxisValueLabel(format: .dateTime.month(.abbreviated).day())
            }
        }
        .chartYAxis {
            AxisMarks(position: .leading, values: .automatic(desiredCount: 4)) { value in
                AxisGridLine().foregroundStyle(Color.primary.opacity(0.07))
                AxisValueLabel {
                    if let count = value.as(Int64.self) { Text(formatted(count)) }
                }
            }
        }
        .chartXSelection(value: $state.selectedDate)
        .frame(height: line ? 160 : 190)
        .accessibilityLabel(line ? "Daily tokens by model" : "Daily token usage")
    }

    private func legend(_ series: [SettingsUsageSeries]) -> some View {
        let total = series.reduce(0.0) { $0 + Double($1.tokens) }
        return Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 16) {
            ForEach(Array(stride(from: 0, to: series.count, by: 3)), id: \.self) { start in
                GridRow {
                    ForEach(start..<min(start + 3, series.count), id: \.self) { index in
                        let item = series[index]
                        HStack(spacing: 10) {
                            Capsule().fill(colors[index % colors.count]).frame(width: 3, height: 34)
                            VStack(alignment: .leading, spacing: 5) {
                                Text(item.title).font(.caption).foregroundStyle(.secondary).lineLimit(1).help(item.title)
                                Text(total > 0 ? (Double(item.tokens) / total).formatted(.percent.precision(.fractionLength(1))) : "—")
                                    .font(.body).monospacedDigit()
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .accessibilityElement(children: .ignore)
                        .accessibilityLabel("\(item.title), \(item.tokens.formatted()) tokens, \(total > 0 ? (Double(item.tokens) / total).formatted(.percent.precision(.fractionLength(1))) : "no usage")")
                        .help("\(item.tokens.formatted()) tokens")
                    }
                }
            }
        }
    }

    private func topSessions(_ snapshot: AnalyticsSnapshot) -> some View {
        let sessions = UsageAnalyticsPresentation.rankedSessions(snapshot.sessions)
        return VStack(alignment: .leading, spacing: 12) {
            heading("Top sessions", subtitle: "Sessions ranked by tokens in this period")
            card {
                VStack(spacing: 0) {
                    HStack {
                        Text("Session").frame(maxWidth: .infinity, alignment: .leading)
                        Text("Share").frame(width: 64, alignment: .trailing)
                        Text("Tokens").frame(width: 100, alignment: .trailing)
                    }
                    .font(.caption).foregroundStyle(.secondary).padding(.horizontal, 16).padding(.vertical, 10)
                    if sessions.isEmpty {
                        Divider()
                        Text("No sessions in this period").font(.callout).foregroundStyle(.secondary).padding(20)
                    }
                    ForEach(Array(sessions.prefix(state.showsAllSessions ? sessions.count : 5))) { session in
                        Divider()
                        DisclosureGroup(isExpanded: Binding(
                            get: { state.expandedSessions.contains(session.id) },
                            set: { if $0 { state.expandedSessions.insert(session.id) } else { state.expandedSessions.remove(session.id) } }
                        )) {
                            VStack(alignment: .leading, spacing: 12) {
                                Text(session.id).font(.caption.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                                tokenBreakdown(session.usage)
                                Text("Last active \(session.lastActivityAt.formatted(date: .abbreviated, time: .shortened))")
                                    .font(.caption).foregroundStyle(.secondary)
                                MenuLink(destination: .session(id: session.id, range: range)) {
                                    Label("Session details", systemImage: "arrow.up.right").font(.callout)
                                }
                            }.padding(.vertical, 10)
                        } label: {
                            HStack(spacing: 12) {
                                Text(session.displayName).lineLimit(1).truncationMode(.middle)
                                    .frame(maxWidth: .infinity, alignment: .leading).help(session.displayName)
                                Text(snapshot.usage.totalTokens > 0
                                     ? (Double(session.usage.totalTokens) / Double(snapshot.usage.totalTokens)).formatted(.percent.precision(.fractionLength(1))) : "—")
                                    .frame(width: 64, alignment: .trailing)
                                Text(formatted(session.usage.totalTokens)).frame(width: 100, alignment: .trailing)
                            }.font(.callout).monospacedDigit()
                        }
                        .padding(.horizontal, 16).padding(.vertical, 10)
                    }
                }
            }
            if sessions.count > 5 {
                Button(state.showsAllSessions ? "Show less" : "Show more") { state.showsAllSessions.toggle() }
                    .controlSize(.small).accessibilityIdentifier("settings.usage.moreSessions")
            }
        }
    }

    private var localHistory: some View {
        VStack(alignment: .leading, spacing: 12) {
            heading("Token history", subtitle: "This Mac · calendar periods")
            card {
                VStack(spacing: 0) {
                    ForEach(Array(UsagePeriod.allCases.enumerated()), id: \.element) { index, period in
                        if index > 0 { Divider() }
                        DisclosureGroup(isExpanded: Binding(
                            get: { state.expandedPeriods.contains(period) },
                            set: { if $0 { state.expandedPeriods.insert(period) } else { state.expandedPeriods.remove(period) } }
                        )) {
                            VStack(alignment: .leading, spacing: 12) {
                                tokenBreakdown(store.snapshot.totals(for: period))
                                MenuLink(destination: .period(period, scope: .local)) {
                                    Label("View details", systemImage: "arrow.up.right").font(.callout)
                                }
                            }.padding(.vertical, 12)
                        } label: {
                            HStack {
                                Text(MenuDestination.period(period, scope: .local).title(usesProfileTotals: false))
                                Spacer()
                                Text(store.snapshot.updatedAt == nil ? "—" : formatted(store.snapshot.totals(for: period).totalTokens))
                                    .fontWeight(.medium).monospacedDigit()
                            }.font(.callout)
                        }
                        .disabled(store.snapshot.updatedAt == nil)
                        .padding(.horizontal, 16).padding(.vertical, 12)
                    }
                }
            }
        }
    }

    private func modelActivity(_ snapshot: AnalyticsSnapshot) -> some View {
        let data = UsageAnalyticsPresentation(snapshot: snapshot, grouping: .model, showsCachedInput: showsCachedInput)
        return VStack(alignment: .leading, spacing: 12) {
            heading("Model activity", subtitle: "Daily token usage by model · This Mac")
            card {
                VStack(alignment: .leading, spacing: 18) {
                    metric(Int64(snapshot.models.count), label: "Models used")
                    if snapshot.usage.isZero {
                        placeholder("No model activity in this period", symbol: "chart.xyaxis.line", loading: false)
                    } else {
                        usageChart(data, line: true)
                        legend(data.series)
                    }
                }.padding(18)
            }
        }
    }

    private func tokenBreakdown(_ usage: TokenUsage) -> some View {
        VStack(spacing: 8) {
            valueRow("Input", value: usage.inputTokens)
            if showsCachedInput { valueRow("Cached input · included in Input", value: usage.cachedInputTokens) }
            valueRow("Output", value: usage.outputTokens)
        }
    }

    private func valueRow(_ title: String, value: Int64) -> some View {
        HStack { Text(title).foregroundStyle(.secondary); Spacer(); Text(formatted(value)).monospacedDigit() }
            .font(.callout).accessibilityElement(children: .combine)
    }

    private func heading(_ title: String, subtitle: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.headline).accessibilityAddTraits(.isHeader)
            Text(subtitle).font(.callout).foregroundStyle(.secondary)
        }
    }

    private func metric(_ count: Int64, label: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text(formatted(count)).font(.system(size: 28, weight: .semibold, design: .rounded)).monospacedDigit()
        }.accessibilityElement(children: .combine)
    }

    private func card<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        content().frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 14))
            .overlay { RoundedRectangle(cornerRadius: 14).strokeBorder(Color.primary.opacity(0.06)) }
    }

    private func placeholder(_ title: String, symbol: String, loading: Bool) -> some View {
        HStack(spacing: 10) {
            if loading { ProgressView().controlSize(.small) } else { Image(systemName: symbol) }
            Text(title)
        }.font(.callout).foregroundStyle(.secondary).frame(maxWidth: .infinity, minHeight: 180)
    }

    private func snapshotStatus(_ snapshot: AnalyticsSnapshot) -> String? {
        if store.analyticsStatusMessage == "Showing the last analytics snapshot" { return store.analyticsStatusMessage }
        switch snapshot.quality {
        case .partial: return "Some usage could not be read. Totals may be incomplete."
        case .stale: return "Showing the last available usage."
        default: return nil
        }
    }

    private func formatted(_ value: Int64) -> String {
        TokenFormatter().string(from: value, style: TokenNumberStyle(rawValue: numberStyle) ?? .compact)
    }
}
