import Charts
import SwiftUI

/// GPT-style analytics within the Settings pane's single outer scroll view.
struct UsageSettingsAnalytics: View {
    @EnvironmentObject private var store: UsageStore
    @EnvironmentObject private var navigation: MenuNavigation
    @AppStorage("numberStyle") private var numberStyle = TokenNumberStyle.compact.rawValue
    @AppStorage("showCachedInput") private var showsCachedInput = true
    @AppStorage("sessionsEnabled") private var sessionsEnabled = AppPreferences.defaultSessionsEnabled
    @AppStorage("projectsEnabled") private var projectsEnabled = AppPreferences.defaultProjectsEnabled
    @EnvironmentObject private var state: SettingsUsageAnalyticsState

    private var range: AnalyticsRange { navigation.usageRange == .today ? .sevenDays : navigation.usageRange }
    private var snapshot: AnalyticsSnapshot? { store.analyticsSnapshots[range] }
    private let colors: [Color] = [.accentColor, .orange, .green, .pink, .teal, .yellow]

    var body: some View {
        VStack(alignment: .leading, spacing: 28) {
            totalUsage
            if let snapshot, snapshot.quality != .unavailable, snapshot.quality != .error {
                if sessionsEnabled { topSessions(snapshot) }
                if projectsEnabled { topProjects(snapshot) }
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
                            if let day = data.bucket(at: state.selectedDate), !day.models.isEmpty {
                                dayModels(day)
                            }
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
                    tableHeader("Session", count: nil)
                    if sessions.isEmpty {
                        Divider()
                        Text("No sessions in this period").font(.callout).foregroundStyle(.secondary).padding(20)
                    }
                    ForEach(Array(sessions.prefix(state.showsAllSessions ? sessions.count : 5))) { session in
                        Divider().opacity(0.6)
                        DisclosureGroup(isExpanded: Binding(
                            get: { state.expandedSessions.contains(session.id) },
                            set: { if $0 { state.expandedSessions.insert(session.id) } else { state.expandedSessions.remove(session.id) } }
                        )) {
                            VStack(alignment: .leading, spacing: 14) {
                                Text(session.id).font(.caption2.monospaced()).foregroundStyle(.tertiary)
                                    .lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                                HStack(alignment: .top, spacing: 14) {
                                    detailPanel("Tokens") { tokenBreakdown(session.usage) }
                                    detailPanel("Activity") {
                                        sessionFacts(session)
                                        textRow("Last active", session.lastActivityAt.formatted(date: .abbreviated, time: .shortened))
                                    }
                                }
                                if !session.models.isEmpty {
                                    detailPanel("Models") {
                                        ForEach(session.models.sorted(by: ModelUsageSummary.byUsageThenName)) { model in
                                            valueRow(model.displayName, value: model.usage.totalTokens)
                                        }
                                    }
                                }
                                MenuLink(destination: .session(id: session.id, range: range)) {
                                    Label("Session details", systemImage: "arrow.up.right").font(.callout)
                                }
                            }.padding(.top, 8).padding(.bottom, 6)
                        } label: {
                            HStack(spacing: 12) {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(session.displayName).lineLimit(1).truncationMode(.middle).help(session.displayName)
                                    if let summary = sessionSubtitle(session) {
                                        Text(summary).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                    }
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                shareCell(snapshot.usage.totalTokens > 0
                                          ? Double(session.usage.totalTokens) / Double(snapshot.usage.totalTokens) : 0)
                                Text(formatted(session.usage.totalTokens)).fontWeight(.medium)
                                    .frame(width: 84, alignment: .trailing)
                            }.font(.callout).monospacedDigit()
                        }
                        .padding(.horizontal, 16).padding(.vertical, 10)
                    }
                }
            }
            if sessions.count > 5 {
                Button(state.showsAllSessions ? "Show less" : "Show more") { state.showsAllSessions.toggle() }
                    .buttonStyle(SettingsPillButtonStyle(compact: true))
                    .accessibilityIdentifier("settings.usage.moreSessions")
            }
        }
    }

    /// Every model that ran on the selected day, whichever grouping the chart uses.
    private func dayModels(_ day: UsageBucket) -> some View {
        detailPanel("Models that day") {
            ForEach(day.models.sorted(by: ModelUsageSummary.byUsageThenName)) { model in
                valueRow(model.displayName, value: model.usage.totalTokens)
            }
        }
    }

    private struct RankedRow: Identifiable {
        let id: String
        let title: String
        var subtitle: String?
        var tint: Color?
        var count: Int?
        let tokens: Int64
    }

    private func tableHeader(_ first: String, count: String?) -> some View {
        HStack(spacing: 12) {
            Text(first).frame(maxWidth: .infinity, alignment: .leading)
            if let count { Text(count).frame(width: 64, alignment: .trailing) }
            Text("Share").frame(width: Self.shareWidth, alignment: .trailing)
            Text("Tokens").frame(width: 84, alignment: .trailing)
        }
        .font(.caption.weight(.medium)).foregroundStyle(.secondary)
        .padding(.horizontal, 16).padding(.vertical, 10)
    }

    private static let shareWidth: CGFloat = 112

    /// A percentage with a thin proportional bar, so the ranking reads at a glance.
    private func shareCell(_ fraction: Double, tint: Color = .accentColor) -> some View {
        HStack(spacing: 8) {
            Capsule().fill(Color.primary.opacity(0.08))
                .frame(width: 44, height: 5)
                .overlay(alignment: .leading) {
                    GeometryReader { proxy in
                        Capsule().fill(tint.opacity(0.85))
                            .frame(width: max(fraction > 0 ? 3 : 0, proxy.size.width * min(fraction, 1)))
                    }
                }
            Text(fraction.formatted(.percent.precision(.fractionLength(1)))).frame(width: 52, alignment: .trailing)
        }
        .frame(width: Self.shareWidth, alignment: .trailing)
    }

    private func rankedRow(_ row: RankedRow, total: Int64, showsCount: Bool) -> some View {
        HStack(spacing: 12) {
            HStack(spacing: 10) {
                if let tint = row.tint { Circle().fill(tint).frame(width: 8, height: 8) }
                VStack(alignment: .leading, spacing: 2) {
                    Text(row.title).lineLimit(1).truncationMode(.middle)
                    if let subtitle = row.subtitle {
                        Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .help(row.subtitle.map { "\(row.title) · \($0)" } ?? row.title)
            if showsCount { Text((row.count ?? 0).formatted()).foregroundStyle(.secondary).frame(width: 64, alignment: .trailing) }
            shareCell(total > 0 ? Double(row.tokens) / Double(total) : 0, tint: row.tint ?? .accentColor)
            Text(formatted(row.tokens)).fontWeight(.medium).frame(width: 84, alignment: .trailing)
        }
        .font(.callout).monospacedDigit()
        .padding(.horizontal, 16).padding(.vertical, 11)
    }

    private func rankedTable(first: String, count: String?, rows: [RankedRow], total: Int64, empty: String) -> some View {
        VStack(spacing: 0) {
            tableHeader(first, count: count)
            if rows.isEmpty {
                Divider()
                Text(empty).font(.callout).foregroundStyle(.secondary).padding(20)
            }
            ForEach(rows) { row in
                Divider().opacity(0.6)
                rankedRow(row, total: total, showsCount: count != nil)
            }
        }
    }

    private func leadModel(_ models: [ModelUsageSummary]) -> String? {
        let sorted = models.sorted(by: ModelUsageSummary.byUsageThenName)
        guard let lead = sorted.first else { return nil }
        return sorted.count > 1 ? "\(lead.displayName) +\(sorted.count - 1)" : lead.displayName
    }

    private func modelList(_ snapshot: AnalyticsSnapshot) -> some View {
        let models = snapshot.models.sorted(by: ModelUsageSummary.byUsageThenName)
        let rows = models.enumerated().map { index, model in
            RankedRow(id: model.id, title: model.displayName, tint: colors[index % colors.count],
                      count: snapshot.sessions.filter { $0.models.contains { $0.id == model.id } }.count,
                      tokens: model.usage.totalTokens)
        }
        return VStack(spacing: 0) {
            Divider()
            rankedTable(first: "Model", count: "Sessions", rows: rows, total: snapshot.usage.totalTokens,
                        empty: "No models in this period")
                .padding(.horizontal, -16)
        }
    }

    private func topProjects(_ snapshot: AnalyticsSnapshot) -> some View {
        let rows = snapshot.projects.sorted(by: ProjectUsageSummary.byUsageThenName).prefix(5).map {
            RankedRow(id: $0.id, title: $0.name, subtitle: leadModel($0.models), count: $0.sessionCount,
                      tokens: $0.usage.totalTokens)
        }
        return VStack(alignment: .leading, spacing: 12) {
            heading("Top projects", subtitle: "Projects ranked by tokens in this period")
            card {
                rankedTable(first: "Project", count: "Sessions", rows: Array(rows),
                            total: snapshot.usage.totalTokens, empty: "No projects in this period")
            }
        }
    }

    /// The model that carried most of the session, with how many others joined it.
    private func sessionSubtitle(_ session: SessionUsageSummary) -> String? {
        guard let lead = session.models.sorted(by: ModelUsageSummary.byUsageThenName).first else { return nil }
        let others = session.models.count - 1
        return others > 0 ? "\(lead.displayName) +\(others)" : lead.displayName
    }

    @ViewBuilder
    private func sessionFacts(_ session: SessionUsageSummary) -> some View {
        VStack(spacing: 8) {
            if let started = session.startedAt {
                textRow("Started", started.formatted(date: .abbreviated, time: .shortened))
                let duration = session.lastActivityAt.timeIntervalSince(started)
                if duration >= 60 {
                    textRow("Duration", Duration.seconds(duration).formatted(.units(allowed: [.days, .hours, .minutes], width: .abbreviated)))
                }
            }
            if session.usage.inputTokens > 0 {
                textRow("Cache hit", (Double(session.usage.cachedInputTokens) / Double(session.usage.inputTokens))
                    .formatted(.percent.precision(.fractionLength(1))))
            }
            if session.directSubagentCount > 0 { textRow("Sub-agents", session.directSubagentCount.formatted()) }
            if session.imageAttachmentCount > 0 { textRow("Images", session.imageAttachmentCount.formatted()) }
        }
    }

    private func detailPanel<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
            content()
        }
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .padding(12)
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10))
    }

    private func textRow(_ title: String, _ value: String) -> some View {
        HStack { Text(title).foregroundStyle(.secondary); Spacer(); Text(value).monospacedDigit() }
            .font(.callout).accessibilityElement(children: .combine)
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
                        modelList(snapshot)
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
