import AppKit
import SwiftUI
import Vision
import XCTest
@testable import CodexMeter

@MainActor
final class AnalyticsMetricLayoutTests: XCTestCase {
    func testUnifiedUsageShowsBothMetricsForSummarySelectedIntervalAndModels() async throws {
        let fixture = try await Fixture()
        defer { fixture.cleanup() }
        for (width, dark) in [(CGFloat(372), false), (CGFloat(900), true)] {
            let navigation = MenuNavigation()
            let host = hosting(UsageAnalyticsView(), fixture: fixture, width: width, dark: dark)
            host.rootView = AnyView(host.rootView.environmentObject(navigation))
            let window = window(for: host, width: width, dark: dark)
            defer { window.contentView = nil; window.close() }
            await layout(host)
            navigation.selectedBucketDate = fixture.eventDate
            await layout(host)
            let text = try recognizedText(host, name: "analytics-unified-\(Int(width))")
            let compact = normalized(text)
            XCTAssertEqual(segmentControls(in: host).count, 1, "Only the period selector should remain")
            for expected in ["660,000", "330,000", "220,000", "110,000", "Cached input",
                             "$1.89", "$1.05", "$0.84", "Estimated API cost subtotal", "Pricing unavailable"] {
                XCTAssertTrue(compact.contains(normalized(expected)), text)
            }
            XCTAssertTrue(text.contains("$1.89"), text)
            XCTAssertEqual(compact.components(separatedBy: "660000").count - 1, 2,
                           "The range and selected interval must both display the total: \(text)")
            for model in fixture.models {
                XCTAssertTrue(compact.contains(normalized(model.displayName)), text)
            }
        }
    }

    func testUnifiedModelDetailRetainsTokensAndLeavesUnknownCostUnavailable() async throws {
        let fixture = try await Fixture()
        defer { fixture.cleanup() }
        for model in [fixture.models[0], fixture.models[2]] {
            let host = hosting(ModelDetailView(id: model.id, range: .sevenDays),
                               fixture: fixture, width: 600, dark: true)
            let window = window(for: host, width: 600, dark: true)
            defer { window.contentView = nil; window.close() }
            await layout(host)
            let text = try recognizedText(host, name: "model-unified-\(model.id)")
            let compact = normalized(text)
            XCTAssertTrue(compact.contains(normalized(model.usage.totalTokens.formatted())), text)
            XCTAssertTrue(compact.contains("cachedinput"), text)
            if model.id == "gpt-6-astra" {
                XCTAssertTrue(text.contains("$1.05"), text)
            } else {
                XCTAssertTrue(compact.contains("estimateunavailable"), text)
                XCTAssertFalse(text.contains("$"), text)
            }
        }
    }

    func testDisablingEstimatesKeepsTokenSummaryChartAndRows() async throws {
        let fixture = try await Fixture()
        defer { fixture.cleanup() }
        fixture.defaults.set(false, forKey: "costEstimatesEnabled")
        let navigation = MenuNavigation()
        let host = hosting(UsageAnalyticsView().environmentObject(navigation),
                           fixture: fixture, width: 600, dark: true)
        let window = window(for: host, width: 600, dark: true)
        defer { window.contentView = nil; window.close() }
        await layout(host)
        let text = try recognizedText(host, name: "analytics-cost-disabled")
        let compact = normalized(text)
        for expected in ["660,000", "330,000", "220,000", "110,000", "Token activity"] {
            XCTAssertTrue(compact.contains(normalized(expected)), text)
        }
        XCTAssertEqual(segmentControls(in: host).count, 1)
        XCTAssertFalse(text.contains("$"), text)
        XCTAssertFalse(compact.contains("estimated"), text)
        XCTAssertFalse(compact.contains("pricingunavailable"), text)
    }

    private func segmentControls(in view: NSView) -> [NSSegmentedControl] {
        (view as? NSSegmentedControl).map { [$0] } ?? view.subviews.flatMap { segmentControls(in: $0) }
    }

    private func hosting(_ view: some View, fixture: Fixture, width: CGFloat, dark: Bool) -> NSHostingView<AnyView> {
        let host = NSHostingView(rootView: AnyView(view
            .environmentObject(fixture.store)
            .defaultAppStorage(fixture.defaults)
            .environment(\.usageDetailUsesWindowWidth, true)
            .environment(\.colorScheme, dark ? .dark : .light)
            .frame(width: width, height: 900, alignment: .topLeading)
            .background(.background)))
        host.sizingOptions = []
        return host
    }

    private func window(for host: NSView, width: CGFloat, dark: Bool) -> NSWindow {
        _ = NSApplication.shared
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: width, height: 900),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        return window
    }

    private func layout(_ host: NSView) async {
        for _ in 0..<12 {
            host.layoutSubtreeIfNeeded()
            await Task.yield()
        }
    }

    private func recognizedText(_ host: NSView, name: String) throws -> String {
        let size = host.bounds.size
        let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil,
            pixelsWide: Int(size.width * 3), pixelsHigh: Int(size.height * 3),
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0))
        bitmap.size = size
        host.cacheDisplay(in: host.bounds, to: bitmap)
        if let path = ProcessInfo.processInfo.environment["CODEXMETER_METRIC_LAYOUT_DIR"] {
            let directory = URL(fileURLWithPath: path, isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: directory.appendingPathComponent("\(name).png"))
        }
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        // Keep exact assertions, but give Vision the vocabulary used by this UI.
        // macOS runners can confuse "unavailable" and model IDs at small sizes.
        request.usesLanguageCorrection = true
        request.customWords = ["Pricing unavailable", "Estimate unavailable",
                               "gpt-6-astra", "gpt-5.6-sol", "gpt-5.3-codex-spark"]
        request.recognitionLanguages = ["en-US"]
        try VNImageRequestHandler(cgImage: XCTUnwrap(bitmap.cgImage), options: [:]).perform([request])
        return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: " ")
    }

    private func normalized(_ text: String) -> String {
        text.lowercased().filter { $0.isLetter || $0.isNumber }
    }

    @MainActor
    private struct Fixture {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let suite = "CodexMeter.AnalyticsMetricLayout.\(UUID().uuidString)"
        let eventDate = Date().addingTimeInterval(-60)
        let defaults: UserDefaults
        let store: UsageStore
        let models: [ModelUsageSummary]

        init() async throws {
            defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
            defaults.set(true, forKey: "costEstimatesEnabled")
            let database = try SQLiteDatabase(url: directory.appendingPathComponent("fixture.sqlite"))
            models = ["gpt-6-astra", "gpt-5.6-sol", "gpt-5.3-codex-spark"].enumerated().map { index, id in
                let scale = Int64(index + 1)
                return ModelUsageSummary(modelID: id, usage: TokenUsage(inputTokens: 100_000 * scale,
                    cachedInputTokens: 50_000 * scale, cacheWriteInputTokens: 0, outputTokens: 10_000 * scale))
            }
            for model in models {
                let source = directory.appendingPathComponent("\(model.id).jsonl").path
                try await database.commit(events: [UsageEvent(eventKey: model.id, occurredAt: eventDate,
                    sessionID: model.id, model: model.id, projectPath: "fixture", usage: model.usage,
                    sourcePath: source, sourcePosition: 1, pricingContext: .standard)],
                    checkpoint: SourceCheckpoint(sourcePath: source, fileIdentity: model.id, generation: 0,
                        committedOffset: 1, sessionID: model.id, inheritsHistory: false, sessionStartedAt: eventDate,
                        historyReplayComplete: true, model: model.id),
                    normalizationState: UsageNormalizationState(cumulativeHighWaterMark: model.usage,
                        lastObservedAt: eventDate, quality: .exact))
            }
            store = UsageStore(automaticallyRefresh: false,
                               collector: CodexUsageCollector(database: database, roots: []), defaults: defaults)
            await store.refreshAnalytics(range: .sevenDays)
        }

        func cleanup() {
            defaults.removePersistentDomain(forName: suite)
            try? FileManager.default.removeItem(at: directory)
        }
    }
}
