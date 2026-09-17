import SwiftUI
#if canImport(CodeRimShared)
import CodeRimShared
#endif

public enum CompanionWidgetMode: String, Sendable { case overview, usage, history, metric }
public enum CompanionWidgetSize: Sendable { case small, medium, large }
public enum CompanionWidgetMetric: String, CaseIterable, Codable, Sendable {
    case automatic, todayTokens, todayCost, monthCost, credits
}

/// Compact native layouts: quota rows, daily history, and a single useful metric.
public struct CompanionWidgetView: View {
    public let provider: CompanionProvider?
    public let providerName: String
    public let mode: CompanionWidgetMode
    public let size: CompanionWidgetSize
    public let metric: CompanionWidgetMetric

    public init(provider: CompanionProvider?, providerName: String, showsLimits: Bool, isMedium: Bool) {
        self.init(provider: provider, providerName: providerName,
                  mode: showsLimits ? .usage : .overview, size: isMedium ? .medium : .small)
    }

    public init(provider: CompanionProvider?, providerName: String, mode: CompanionWidgetMode,
                size: CompanionWidgetSize, metric: CompanionWidgetMetric = .automatic) {
        self.provider = provider
        self.providerName = providerName
        self.mode = mode
        self.size = size
        self.metric = metric
    }

    private var isSmall: Bool { size == .small }
    private var isLarge: Bool { size == .large }
    private var local: CompanionLocalUsage? { provider?.readableLocalUsage }
    private var history: CompanionHistory? {
        guard let history = provider?.history, history.isReadable else { return nil }
        return history
    }
    private var limits: CompanionLimits? {
        guard let limits = provider?.limits, [.ready, .partial, .stale].contains(limits.state),
              !limits.windows.isEmpty else { return nil }
        return limits
    }
    var state: CompanionState? {
        switch mode {
        case .history: history?.state ?? provider?.history?.state ?? provider?.limits.state
        case .metric: metricReading.state ?? provider?.limits.state
        case .overview: local?.state ?? limits?.state ?? provider?.limits.state
        case .usage: limits?.state ?? local?.state ?? provider?.limits.state
        }
    }
    var updatedAt: Date? {
        switch mode {
        case .history: history?.updatedAt
        case .metric: metricReading.updatedAt
        case .overview: local?.updatedAt ?? limits?.updatedAt
        case .usage: limits?.updatedAt ?? local?.updatedAt
        }
    }

    public var body: some View {
        VStack(alignment: .leading, spacing: isLarge ? 12 : 9) {
            header
            if provider?.enabled == false || (provider?.limits.state == .accessDenied && local == nil && history == nil) {
                emptyContent()
            } else {
            switch mode {
            case .history:
                if let history { historyContent(history) } else { emptyContent(historyUnavailable: true) }
            case .metric:
                metricContent
            case .overview, .usage:
                if mode == .overview, let local, let tokens = local.totals["today"] {
                    tokenMetric(tokens, state: local.state)
                    if isLarge, let history { historyBars(history, height: 100) }
                } else if let limits { usageContent(limits) }
                else if let local, let tokens = local.totals["today"] { tokenMetric(tokens, state: local.state) }
                else { emptyContent() }
            }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(providerName).font(.system(size: 12, weight: .semibold))
                .lineLimit(1).minimumScaleFactor(0.7).layoutPriority(1)
            Spacer(minLength: 4)
            if state == .stale {
                Text("Last known").foregroundStyle(.orange)
            } else if state == .partial {
                Text("Partial").foregroundStyle(.secondary)
            } else if let updatedAt {
                Text(updatedAt, style: .relative).foregroundStyle(.secondary)
            } else {
                Image(systemName: "arrow.up.right").foregroundStyle(.secondary)
            }
        }.font(.system(size: 9)).lineLimit(1)
    }

    private func usageContent(_ limits: CompanionLimits) -> some View {
        let ordered = orderedWindows(limits)
        let maximum = isLarge ? 4 : 2
        return VStack(alignment: .leading, spacing: isLarge ? 10 : 8) {
            ForEach(Array(ordered.prefix(maximum))) { window in
                usageRow(window)
            }
            if ordered.count > maximum {
                Text("+\(ordered.count - maximum) more in CodeRim")
                    .font(.system(size: 9)).foregroundStyle(.secondary)
            }
            if isLarge, let history {
                Spacer(minLength: 0)
                historyBars(history, height: 62)
                historyTotals(history, compact: true)
            } else if !isSmall, let local, let tokens = local.totals["today"] {
                Spacer(minLength: 0)
                HStack(spacing: 4) {
                    Text(local.state == .stale ? "Last known" : "Today")
                        .foregroundStyle(.secondary)
                    Text(Self.compact(tokens.totalTokens) + " tokens").fontWeight(.medium)
                    Text("· This Mac").foregroundStyle(.secondary)
                }.font(.system(size: 10)).lineLimit(1).minimumScaleFactor(0.8)
            }
            Spacer(minLength: 0)
            if provider?.fidelity != "official" {
                Text("~ Estimated reading").font(.system(size: 9)).foregroundStyle(.secondary)
            } else if state == .stale {
                Text("Open CodeRim to refresh").font(.system(size: 9)).foregroundStyle(.secondary)
            }
        }
    }

    private func orderedWindows(_ limits: CompanionLimits) -> [CompanionLimitWindow] {
        // Session/weekly order remains familiar; only move an explicitly selected
        // headline when the original list would otherwise hide it.
        guard let headline = limits.headline, limits.windows.first != headline,
              limits.windows.count > (isLarge ? 4 : 2) else { return limits.windows }
        return [headline] + limits.windows.filter { $0.id != headline.id }
    }

    private func usageRow(_ window: CompanionLimitWindow) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(window.name).foregroundStyle(.secondary).lineLimit(1)
                Spacer(minLength: 2)
                Text(windowValue(window)).fontWeight(.medium).monospacedDigit()
                    .lineLimit(1).minimumScaleFactor(0.65).layoutPriority(1)
            }.font(.system(size: isLarge ? 12 : 10))
            if let remaining = window.remainingPercent {
                GeometryReader { geometry in
                    ZStack(alignment: .leading) {
                        Capsule().fill(.quaternary)
                        Capsule().fill(tint(remaining))
                            .frame(width: max(0, geometry.size.width * remaining / 100))
                    }
                }.frame(height: isLarge ? 5 : 4)
                    .accessibilityLabel("\(window.name), \(Int(remaining)) percent remaining")
            }
            HStack(spacing: 3) {
                if let remaining = window.remainingPercent, remaining <= 25 {
                    Text(remaining <= 10 ? "Critical" : "Low").foregroundStyle(tint(remaining))
                } else if window.remainingPercent != nil {
                    Text("remaining").foregroundStyle(.secondary)
                } else if window.displayValue == nil {
                    Text(window.remainingCount != nil ? "remaining" : window.unit ?? "used")
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
                if let reset = window.resetsAt, state != .stale {
                    Text("Resets").foregroundStyle(.secondary)
                    Text(reset, style: .relative).foregroundStyle(.secondary)
                }
            }.font(.system(size: 8)).lineLimit(1).minimumScaleFactor(0.8)
        }
    }

    private func historyContent(_ history: CompanionHistory) -> some View {
        VStack(alignment: .leading, spacing: isLarge ? 9 : 8) {
            historyBars(history, height: isLarge ? 120 : 56)
            if isLarge {
                HStack {
                    if let date = history.days.first?.date {
                        Text(date, format: .dateTime.month(.abbreviated).day())
                    }
                    Spacer()
                    Text(history.days.last?.date ?? history.updatedAt, format: .dateTime.month(.abbreviated).day())
                }.font(.system(size: 9)).foregroundStyle(.secondary)
            }
            historyTotals(history, compact: !isLarge)
            if isLarge, let peak = history.days.max(by: { $0.tokens < $1.tokens }) {
                Divider()
                HStack {
                    Text("Peak day").foregroundStyle(.secondary)
                    Spacer()
                    Text(Self.compact(peak.tokens) + " tokens").monospacedDigit()
                }.font(.system(size: 11))
            }
            Spacer(minLength: 0)
            Text(history.state == .stale ? "Last known history · This Mac" : "This Mac · 30 days")
                .font(.system(size: 8)).foregroundStyle(.secondary)
        }
    }

    private func historyBars(_ history: CompanionHistory, height: CGFloat) -> some View {
        let maximum = Double(history.days.map(\.tokens).max() ?? 0)
        return GeometryReader { geometry in
            HStack(alignment: .bottom, spacing: isLarge ? 3 : 2) {
                ForEach(history.days) { day in
                    RoundedRectangle(cornerRadius: 1)
                        .fill(history.state == .stale ? Color.secondary.opacity(0.65) : Color.accentColor.opacity(0.85))
                        .frame(maxWidth: .infinity)
                        .frame(height: day.tokens == 0 || maximum == 0 ? 1
                               : max(2, geometry.size.height * Double(day.tokens) / maximum))
                }
            }.frame(maxHeight: .infinity, alignment: .bottom)
        }
        .frame(height: height)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Daily token usage over \(history.days.count) days. \(history.totalTokens) tokens in total.")
    }

    private func historyTotals(_ history: CompanionHistory, compact: Bool) -> some View {
        VStack(alignment: .leading, spacing: compact ? 4 : 8) {
            if let today = history.days.last {
                historyTotalRow(label: history.state == .stale ? "Latest" : "Today", tokens: today.tokens,
                    cost: today.estimatedCostUSD, partial: today.costIsPartial, compact: compact)
            }
            historyTotalRow(label: "30 days", tokens: history.totalTokens,
                cost: history.estimatedCostUSD, partial: history.costIsPartial, compact: compact)
            if history.estimatedCostUSD != nil {
                Text(history.costIsPartial ? "Est. API subtotal · unpriced usage excluded" : "Estimated API cost")
                    .font(.system(size: 8)).foregroundStyle(.secondary).lineLimit(1).minimumScaleFactor(0.8)
            }
        }
    }

    private func historyTotalRow(label: String, tokens: Int64, cost: Double?, partial: Bool, compact: Bool) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            Text(label).foregroundStyle(.secondary).frame(width: compact ? 40 : 50, alignment: .leading)
            if let cost { Text((partial ? "≥ " : "~ ") + Self.money(cost)).monospacedDigit() }
            Text(Self.compact(tokens) + " tokens").monospacedDigit().lineLimit(1).minimumScaleFactor(0.8)
        }.font(.system(size: compact ? 9 : 12, weight: .medium))
    }

    private var metricContent: some View {
        let reading = metricReading
        return VStack(alignment: .leading, spacing: 5) {
            Spacer(minLength: 0)
            Text(reading.value).font(.system(size: 32, weight: .semibold, design: .rounded))
                .monospacedDigit().lineLimit(1).minimumScaleFactor(0.5)
            Text(reading.label).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(2)
            if let note = reading.note {
                Text(note).font(.system(size: 9)).foregroundStyle(.secondary).lineLimit(2)
            }
            Spacer(minLength: 0)
        }
    }

    struct MetricReading {
        var value: String
        var label: String
        var note: String?
        var state: CompanionState?
        var updatedAt: Date?
    }

    var metricReading: MetricReading {
        let chosen: CompanionWidgetMetric = metric == .automatic
            ? (limits?.windows.contains { $0.remainingCount != nil || $0.id == "credits" || $0.id == "balance" } == true
                ? .credits : local != nil ? .todayTokens : .automatic)
            : metric
        switch chosen {
        case .todayTokens:
            return MetricReading(value: local?.totals["today"].map { Self.compact($0.totalTokens) } ?? "—",
                label: local?.state == .stale ? "Last known tokens" : "Today’s tokens", note: "This Mac",
                state: local?.state, updatedAt: local?.updatedAt)
        case .todayCost, .monthCost:
            let amount = chosen == .todayCost ? history?.days.last?.estimatedCostUSD : history?.estimatedCostUSD
            let partial = chosen == .todayCost ? history?.days.last?.costIsPartial == true : history?.costIsPartial == true
            return MetricReading(value: amount.map { (partial ? "≥ " : "~ ") + Self.money($0) } ?? "—",
                label: chosen == .todayCost
                    ? (history?.state == .stale ? "Latest · estimated API cost" : "Today · estimated API cost")
                    : "30 days · estimated API cost",
                note: amount == nil ? "No estimate available" : partial ? "Unpriced usage excluded" : "This Mac",
                state: history?.state, updatedAt: history?.updatedAt)
        case .credits, .automatic:
            let window = chosen == .credits
                ? limits?.windows.first { $0.remainingCount != nil || $0.id == "credits" || $0.id == "balance" }
                : limits?.headline
            var value = window.map(windowValue) ?? "—"
            var label = window?.name ?? (chosen == .credits ? "Credits not reported" : "No reading available")
            // Keep an ISO currency code visible even when a balance has many digits.
            if let range = value.range(of: #"\s[A-Z]{3}$"#, options: .regularExpression) {
                label += " · " + value[range].trimmingCharacters(in: .whitespaces)
                value = String(value[..<range.lowerBound])
            }
            return MetricReading(value: value,
                label: label,
                note: window == nil ? "Open provider settings" : nil,
                state: limits?.state, updatedAt: limits?.updatedAt)
        }
    }

    private func tokenMetric(_ tokens: CompanionTokens, state: CompanionState) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Spacer(minLength: 0)
            Text(Self.compact(tokens.totalTokens))
                .font(.system(size: isSmall ? 32 : 38, weight: .semibold, design: .rounded))
                .monospacedDigit().lineLimit(1).minimumScaleFactor(0.6)
            Text(state == .stale ? "Last known tokens · This Mac" : "Today’s tokens · This Mac")
                .font(.system(size: 10)).foregroundStyle(.secondary)
            Spacer(minLength: 0)
        }
    }

    private func emptyContent(historyUnavailable: Bool = false) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Spacer(minLength: 0)
            Text(historyUnavailable ? "No local history" : emptyTitle)
                .font(.system(size: 14, weight: .semibold, design: .rounded)).lineLimit(2)
            Text(historyUnavailable
                 ? "History is available for Codex and Claude local sessions."
                 : provider?.limits.message ?? "Open CodeRim to refresh usage.")
                .font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(isSmall ? 3 : 4)
            Spacer(minLength: 0)
        }
    }

    private var emptyTitle: String {
        switch provider?.limits.state {
        case .disabled: "Provider is off"
        case .loading: "Checking usage…"
        case .needsAuth: "Sign in to continue"
        case .accessDenied: "Usage file unavailable"
        case .unsupported: "Check provider setup"
        default: "Waiting for usage"
        }
    }

    private func tint(_ remaining: Double) -> Color {
        if state == .stale { return .secondary }
        return remaining <= 10 ? .red : remaining <= 25 ? .orange : .accentColor
    }

    private func windowValue(_ window: CompanionLimitWindow) -> String {
        let qualifier = provider?.fidelity == "official" ? "" : "~"
        if let text = window.displayValue { return qualifier + text }
        if let percent = window.remainingPercent {
            return qualifier + String(format: percent > 0 && percent < 1 ? "%.1f%%" : "%.0f%%", percent)
        }
        if let count = window.remainingCount ?? window.usedCount { return qualifier + Self.compact(Int64(count)) }
        return "—"
    }

    private static func money(_ amount: Double) -> String {
        String(format: "$%.2f", locale: Locale(identifier: "en_US_POSIX"), amount)
    }
    private static func compact(_ value: Int64) -> String {
        let magnitude: Double
        let suffix: String
        switch value {
        case 1_000_000_000...: magnitude = 1_000_000_000; suffix = "B"
        case 1_000_000...: magnitude = 1_000_000; suffix = "M"
        case 10_000...: magnitude = 1_000; suffix = "K"
        default: return value.formatted()
        }
        return String(format: "%.1f%@", Double(value) / magnitude, suffix)
    }
}
