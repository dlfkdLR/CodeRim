import Charts
import SwiftUI

/// The palette the Usage screen's charts share, so a model keeps its colour
/// from one card to the next.
enum UsageActivityPalette {
    static let colors: [Color] = [.accentColor, .orange, .green, .pink, .teal, .yellow]

    static func color(for series: ActivitySeries, at index: Int) -> Color {
        series.id == ActivityChart.otherID ? .gray : colors[index % colors.count]
    }
}

/// Where the period's tokens went: chats, the sub-agents they started, and
/// whatever the session list does not account for.
struct UsageTypeBreakdown: Equatable {
    struct Part: Equatable, Identifiable {
        let id: String
        let title: String
        let tokens: Int64
    }

    let parts: [Part]
    let total: Int64

    init(snapshot: AnalyticsSnapshot) {
        var chats: Int64 = 0
        var subagents: Int64 = 0
        for session in snapshot.sessions {
            if session.parentSessionID == nil { chats += session.usage.totalTokens }
            else { subagents += session.usage.totalTokens }
        }
        total = snapshot.usage.totalTokens
        let other = max(0, total - chats - subagents)
        // The session list is capped; a share it cannot place is shown as such
        // rather than spread over the others.
        parts = [Part(id: "chats", title: "Chats", tokens: chats),
                 Part(id: "subagents", title: "Sub-agents", tokens: subagents),
                 Part(id: "other", title: "Other", tokens: other)]
            .filter { $0.tokens > 0 }
    }

    func share(_ part: Part) -> Double {
        total > 0 ? Double(part.tokens) / Double(total) : 0
    }
}

struct UsageTypeCard: View {
    let breakdown: UsageTypeBreakdown
    let formatted: (Int64) -> String

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Usage by type").font(.headline).accessibilityAddTraits(.isHeader)
                Text("Where this period's tokens went").font(.callout).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 18) {
                if breakdown.parts.isEmpty {
                    Text("No usage in this period").font(.callout).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, minHeight: 60)
                } else {
                    bar
                    legend
                }
            }
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 14))
            .overlay { RoundedRectangle(cornerRadius: 14).strokeBorder(Color.primary.opacity(0.06)) }
        }
    }

    private func color(_ index: Int, _ part: UsageTypeBreakdown.Part) -> Color {
        part.id == "other" ? .gray : UsageActivityPalette.colors[index % UsageActivityPalette.colors.count]
    }

    private var bar: some View {
        GeometryReader { proxy in
            HStack(spacing: 2) {
                ForEach(Array(breakdown.parts.enumerated()), id: \.element.id) { index, part in
                    Capsule().fill(color(index, part))
                        .frame(width: max(4, (proxy.size.width - 2 * CGFloat(breakdown.parts.count - 1)) * breakdown.share(part)))
                }
            }
        }
        .frame(height: 10)
        .accessibilityHidden(true)
    }

    private var legend: some View {
        ViewThatFits(in: .horizontal) {
            legendItems(HStackLayout(alignment: .top, spacing: 28))
            legendItems(VStackLayout(alignment: .leading, spacing: 14))
        }
    }

    private func legendItems<L: Layout>(_ layout: L) -> some View {
        layout {
            ForEach(Array(breakdown.parts.enumerated()), id: \.element.id) { index, part in
                HStack(spacing: 10) {
                    Capsule().fill(color(index, part)).frame(width: 3, height: 34)
                    VStack(alignment: .leading, spacing: 5) {
                        Text(part.title).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        Text(breakdown.share(part).formatted(.percent.precision(.fractionLength(1))))
                            .font(.body).monospacedDigit()
                    }
                }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("\(part.title), \(formatted(part.tokens)) tokens, \(breakdown.share(part).formatted(.percent.precision(.fractionLength(1))))")
                .help("\(part.tokens.formatted()) tokens")
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// A count over time, split into a few named series: the shape of the Messages
/// and Tool activity cards.
struct UsageActivityChartCard: View {
    let title: String
    let subtitle: String
    let metric: String
    let unit: String
    let chart: ActivityChart
    let empty: String
    let isLoading: Bool
    let identifier: String

    private struct Point: Identifiable {
        let id: String
        let seriesID: String
        let title: String
        let day: Date
        let count: Int
    }

    private var points: [Point] {
        chart.series.flatMap { series in
            zip(chart.days, series.counts).map { day, count in
                Point(id: "\(series.id)|\(day.timeIntervalSinceReferenceDate)", seriesID: series.id,
                      title: series.title, day: day, count: count)
            }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(.headline).accessibilityAddTraits(.isHeader)
                Text(subtitle).font(.callout).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 18) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(metric).font(.caption).foregroundStyle(.secondary)
                    Text(chart.total.formatted()).font(.system(size: 28, weight: .semibold, design: .rounded)).monospacedDigit()
                }
                .accessibilityElement(children: .combine)
                if chart.total == 0 {
                    HStack(spacing: 10) {
                        if isLoading { ProgressView().controlSize(.small) } else { Image(systemName: "chart.xyaxis.line") }
                        Text(isLoading ? "Reading logs on this Mac" : empty)
                    }
                    .font(.callout).foregroundStyle(.secondary).frame(maxWidth: .infinity, minHeight: 120)
                } else {
                    plot
                    legend
                }
            }
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 14))
            .overlay { RoundedRectangle(cornerRadius: 14).strokeBorder(Color.primary.opacity(0.06)) }
        }
        .accessibilityIdentifier(identifier)
    }

    private var plot: some View {
        Chart(points) { point in
            LineMark(x: .value("Date", point.day, unit: .day), y: .value(unit, point.count))
                .foregroundStyle(by: .value("Series", point.seriesID))
                .lineStyle(StrokeStyle(lineWidth: 2))
                .symbol(by: .value("Series", point.seriesID))
                .symbolSize(14)
                .accessibilityLabel("\(point.title), \(point.day.formatted(date: .abbreviated, time: .omitted))")
                .accessibilityValue("\(point.count.formatted()) \(unit)")
        }
        .chartForegroundStyleScale(
            domain: chart.series.map(\.id),
            range: chart.series.enumerated().map { UsageActivityPalette.color(for: $0.element, at: $0.offset) })
        .chartLegend(.hidden)
        .chartXAxis {
            AxisMarks(values: .stride(by: .day, count: chart.days.count > 10 ? 10 : 3)) {
                AxisValueLabel(format: .dateTime.month(.abbreviated).day())
            }
        }
        .chartYAxis {
            AxisMarks(position: .leading, values: .automatic(desiredCount: 4)) { _ in
                AxisGridLine().foregroundStyle(Color.primary.opacity(0.07))
                AxisValueLabel()
            }
        }
        .frame(height: 200)
    }

    private var legend: some View {
        Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 16) {
            ForEach(Array(stride(from: 0, to: chart.series.count, by: 3)), id: \.self) { start in
                GridRow {
                    ForEach(start..<min(start + 3, chart.series.count), id: \.self) { index in
                        let series = chart.series[index]
                        HStack(spacing: 10) {
                            Capsule().fill(UsageActivityPalette.color(for: series, at: index)).frame(width: 3, height: 34)
                            VStack(alignment: .leading, spacing: 5) {
                                Text(series.title).font(.caption).foregroundStyle(.secondary)
                                    .lineLimit(1).truncationMode(.middle).help(series.title)
                                Text(series.total.formatted()).font(.body).monospacedDigit()
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .accessibilityElement(children: .ignore)
                        .accessibilityLabel("\(series.title), \(series.total.formatted()) \(unit)")
                    }
                }
            }
        }
    }
}

/// How hard and how fast a session ran, as shares of its turns, from the
/// agent's own log. The models it used are already listed with their tokens.
struct SessionTraitsPanels: View {
    let traits: SessionTraits

    var body: some View {
        let panels = [("Reasoning", traits.efforts), ("Speed", traits.speeds)]
            .filter { !$0.1.isEmpty }
        if !panels.isEmpty {
            ViewThatFits(in: .horizontal) {
                panelRow(panels, HStackLayout(alignment: .top, spacing: 14))
                panelRow(panels, VStackLayout(alignment: .leading, spacing: 10))
            }
        }
    }

    private func panelRow<L: Layout>(_ panels: [(String, [String: Int])], _ layout: L) -> some View {
        layout {
            ForEach(panels, id: \.0) { title, counts in
                    VStack(alignment: .leading, spacing: 8) {
                        Text(title).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                        ForEach(SessionTraits.shares(counts), id: \.name) { item in
                            HStack(spacing: 8) {
                                Text(item.name).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                                Spacer(minLength: 4)
                                Text(item.share.formatted(.percent.precision(.fractionLength(0)))).monospacedDigit()
                            }
                            .font(.callout).accessibilityElement(children: .combine)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .topLeading)
                    .padding(12)
                    .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10))
            }
        }
    }
}
