import AppKit
import SwiftUI
import WidgetKit

// Only explicit in-memory Readings are passed to the widget's production entry factory.
private let asOf = Date(timeIntervalSince1970: 1_719_835_200) // 2024-07-01 12:00:00 UTC

private struct Scenario {
    var name: String
    var id: CompanionProviderID = .codex
    var snapshot: CompanionSnapshot?
    var state: CompanionState = .unavailable
    var message: String?
    var placeholder = false
}

private func scenarios() -> [Scenario] {
    let days: [CompanionHistoryDay] = (0..<30).map { index in
        let date = asOf.addingTimeInterval(Double(index - 29) * 86_400)
        let tokens = Int64((index % 9) * (index % 5) * 18_271)
        return CompanionHistoryDay(date: date, tokens: tokens, estimatedCostUSD: Double(index % 7) * 0.47, costIsPartial: false)
    }
    let local = CompanionLocalUsage(state: .ready, updatedAt: asOf, periodsAsOf: asOf,
        totals: ["today": CompanionTokens(inputTokens: 1_023_456, cachedInputTokens: 1024, outputTokens: 18_290)])
    let history = CompanionHistory(state: .ready, updatedAt: asOf, days: days,
        totalTokens: days.reduce(0) { $0 + $1.tokens },
        estimatedCostUSD: days.compactMap(\.estimatedCostUSD).reduce(0, +), costIsPartial: false)
    let limits = CompanionLimits(state: .ready, updatedAt: asOf, windows: [
        .init(id: "session", name: "Session", usedPercent: 6, resetsAt: asOf.addingTimeInterval(7200)),
        .init(id: "week", name: "Weekly", usedPercent: 94),
        .init(id: "month", name: "Monthly", usedPercent: 79),
        .init(id: "requests", name: "Requests", usedCount: 143, unit: "requests"),
        .init(id: "credits", name: "Credits", remainingCount: 90_432)
    ], headlineID: "week")
    let populated = CompanionProvider(id: "codex", name: "Codex", localUsage: local,
        limits: limits, history: history, plan: "Synthetic")
    func scenario(_ name: String, _ provider: CompanionProvider) -> Scenario {
        // First match wins, even when a later record with the same ID has different readings.
        var duplicate = populated
        duplicate.limits.windows = []
        duplicate.localUsage = nil
        duplicate.history = nil
        return Scenario(name: name, snapshot: .init(generatedAt: asOf, providers: [provider, duplicate]))
    }
    var stale = populated
    stale.localUsage?.updatedAt = asOf.addingTimeInterval(-301)
    stale.history?.updatedAt = asOf.addingTimeInterval(-601)
    stale.limits.updatedAt = asOf.addingTimeInterval(-301)
    var reset = populated
    reset.limits.windows[0].resetsAt = asOf
    var partial = populated
    partial.history?.state = .partial
    partial.history?.costIsPartial = true
    partial.history?.days[29].costIsPartial = true
    partial.localUsage?.state = .partial
    partial.limits.state = .partial
    partial.limits.updatedAt = nil
    var currency = populated
    currency.id = "moonshot"
    currency.localUsage = nil
    currency.history = nil
    currency.fidelity = "estimated"
    currency.limits.windows = [.init(id: "balance", name: "Balance", displayValue: "12,345.6789 CNY")]
    var free = populated
    free.history?.days = [.init(date: asOf, tokens: 0, estimatedCostUSD: nil, costIsPartial: true)]
    free.history?.totalTokens = 0
    free.history?.estimatedCostUSD = nil
    free.limits.windows = []
    var disabled = populated
    disabled.enabled = false
    disabled.localUsage = nil
    disabled.history = nil
    disabled.limits = .init(state: .disabled, updatedAt: nil, windows: [], message: "Enable this provider in Settings.")
    var missing = currency
    missing.id = "other"
    return [scenario("populated", populated), scenario("aged", stale), scenario("reset-due", reset),
        scenario("partial", partial), scenario("zero-history", free), scenario("disabled", disabled),
        Scenario(name: "currency", id: .moonshot, snapshot: .init(generatedAt: asOf, providers: [currency])),
        Scenario(name: "missing-provider", snapshot: .init(generatedAt: asOf, providers: [missing])),
        Scenario(name: "missing-file", message: "Open CodeRim to refresh usage."),
        Scenario(name: "unreadable", state: .accessDenied, message: "The widget could not read usage. Update or relaunch CodeRim."),
        Scenario(name: "needs-auth", state: .needsAuth), Scenario(name: "loading", state: .loading),
        Scenario(name: "placeholder", placeholder: true)]
}

private struct Layout {
    let mode: CompanionWidgetMode
    let family: WidgetFamily
    var metric: CompanionWidgetMetric = .automatic

    var label: String { family == .systemLarge ? "large" : family == .systemMedium ? "medium" : "small" }
    var width: CGFloat { family == .systemSmall ? 164 : 344 }
    var height: CGFloat { family == .systemLarge ? 344 : 164 }
}

private func layouts() -> [Layout] {
    let families: [WidgetFamily] = [.systemSmall, .systemMedium, .systemLarge]
    return families.map { Layout(mode: .overview, family: $0) } +
        families.map { Layout(mode: .usage, family: $0) } +
        [.systemMedium, .systemLarge].map { Layout(mode: .history, family: $0) } +
        CompanionWidgetMetric.allCases.map { Layout(mode: .metric, family: .systemSmall, metric: $0) }
}

@main
private enum Capture {
    @MainActor static func main() throws {
        guard CommandLine.arguments.count == 2 else { throw CocoaError(.fileNoSuchFile) }
        let directory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        _ = NSApplication.shared
        for item in scenarios() {
            for layout in layouts() {
                let entry = item.placeholder ? UsageEntry(date: asOf, provider: nil, providerName: "Codex", metric: layout.metric)
                    : fixtureEntry(item.snapshot, id: item.id, state: item.state, message: item.message, at: asOf, metric: layout.metric)
                for dark in [false, true] {
                    guard readLine() == "render" else { throw CocoaError(.userCancelled) }
                    let name = [item.name, layout.mode.rawValue, layout.label, layout.metric.rawValue, dark ? "dark" : "light"].joined(separator: "-")
                    try autoreleasepool {
                        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)!
                        var outcome: Result<Void, Error> = .success(())
                        appearance.performAsCurrentDrawingAppearance {
                            outcome = Result {
                                try draw(entry: entry, layout: layout, dark: dark, name: name, directory: directory)
                            }
                        }
                        try outcome.get()
                    }
                }
            }
        }
    }

    @MainActor private static func draw(entry: UsageEntry, layout: Layout, dark: Bool, name: String, directory: URL) throws {
        let utc = TimeZone(secondsFromGMT: 0)!
        let locale = Locale(identifier: "en_US")
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let view = WidgetEntryView(family: layout.family, entry: entry, mode: layout.mode)
            .padding(16).frame(width: layout.width, height: layout.height)
            .background(Color(nsColor: .windowBackgroundColor))
            .environment(\.colorScheme, dark ? .dark : .light)
            .environment(\.locale, locale).environment(\.timeZone, utc)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        renderer.proposedSize = .init(width: layout.width, height: layout.height)
        let w = Int(layout.width), h = Int(layout.height)
        var pixels = Data(count: w * h * 4)
        let png: Data = try pixels.withUnsafeMutableBytes { buffer in
            let format = CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue
            guard let context = CGContext(data: buffer.baseAddress, width: w, height: h,
                bitsPerComponent: 8, bytesPerRow: w * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                bitmapInfo: format) else { throw CocoaError(.fileWriteUnknown) }
            renderer.render { size, paint in
                precondition(size == CGSize(width: w, height: h))
                paint(context)
            }
            guard let image = context.makeImage(),
                  let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
            else { throw CocoaError(.fileWriteUnknown) }
            return data
        }
        try png.write(to: directory.appendingPathComponent(name + ".png"))
        try pixels.write(to: directory.appendingPathComponent("frame.rgba"))
        let wire = try encoder.encode(entry.provider)
        let receipt = [name, String(w), String(h), wire.base64EncodedString()]
        FileHandle.standardOutput.write(Data((receipt.joined(separator: "\t") + "\n").utf8))
    }
}
