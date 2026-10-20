import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class UsageSettingsAnalyticsTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_790_496_000)

    func testTokenSegmentsConserveTheTotalWithAndWithoutCachedInput() {
        let snapshot = fixture()
        for showsCached in [false, true] {
            let data = UsageAnalyticsPresentation(snapshot: snapshot, grouping: .tokenType, showsCachedInput: showsCached)
            XCTAssertEqual(data.series.reduce(0) { $0 + $1.tokens }, snapshot.usage.totalTokens)
            for bucket in snapshot.buckets {
                XCTAssertEqual(data.points.filter { $0.date == bucket.start }.reduce(0) { $0 + $1.tokens }, bucket.usage.totalTokens)
            }
            XCTAssertEqual(data.series.count, showsCached ? 3 : 2)
        }
        let data = UsageAnalyticsPresentation(snapshot: snapshot, grouping: .tokenType, showsCachedInput: true)
        XCTAssertEqual(data.series.first?.tokens, snapshot.usage.inputTokens - snapshot.usage.cachedInputTokens)
    }

    func testGroupedModelsKeepStableIdentityAndConserveEveryDay() {
        let snapshot = fixture()
        let data = UsageAnalyticsPresentation(snapshot: snapshot, grouping: .model, showsCachedInput: true)
        XCTAssertEqual(data.series.count, 6)
        XCTAssertEqual(data.series.last?.title, "Other models")
        XCTAssertEqual(data.series.reduce(0) { $0 + $1.tokens }, snapshot.usage.totalTokens)
        for bucket in snapshot.buckets {
            let points = data.points.filter { $0.date == bucket.start }
            XCTAssertEqual(points.map(\.seriesID), data.series.map(\.id))
            XCTAssertEqual(points.reduce(0) { $0 + $1.tokens }, bucket.usage.totalTokens)
        }
        XCTAssertEqual(Set(data.points.map(\.id)).count, data.points.count)
    }

    func testSelectionUsesTheCalendarBucketAndRankingIsDeterministic() {
        let snapshot = fixture()
        let data = UsageAnalyticsPresentation(snapshot: snapshot, grouping: .model, showsCachedInput: true)
        XCTAssertEqual(data.bucket(at: snapshot.buckets[1].start)?.id, snapshot.buckets[1].id)
        XCTAssertNil(data.bucket(at: snapshot.interval.start.addingTimeInterval(-1)))
        XCTAssertEqual(data.bucket(at: snapshot.buckets.last!.end)?.id, snapshot.buckets.last?.id)
        let nextDay = Calendar.current.date(byAdding: .day, value: 1, to: Calendar.current.startOfDay(for: now))!
        XCTAssertNil(data.bucket(at: nextDay))
        let ranked = UsageAnalyticsPresentation.rankedSessions(snapshot.sessions.reversed())
        XCTAssertEqual(ranked.first?.id, "session-7")
        XCTAssertEqual(ranked.last?.id, "session-0")
    }

    func testNativeAnalyticsFitsNarrowWideAndEmptyStatesWithOneViewport() async throws {
        _ = NSApplication.shared
        let suite = "CodeRim.AnalyticsLayout.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(true, forKey: "analyticsEnabled")
        defaults.set(true, forKey: "sessionsEnabled")
        defaults.set(true, forKey: "showCachedInput")
        defaults.set("compact", forKey: "numberStyle")
        let account = try AccountLayoutFixture(state: .longEmail)
        let profile = ProfileUsageStore(defaults: defaults) { _, _, _ in throw ProfileUsageError.credentialsUnavailable }
        let claude = try makeConnectedClaudeStore(fetchedAt: now)
        let limits = AccountLimitStore(provider: AnalyticsNoLimits(), defaults: defaults, pollingInterval: nil)
        for width: CGFloat in [579, 920] {
            for dark in [false, true] {
                for scenario in ["populated", "expanded", "thirty-days", "empty", "loading", "partial", "disabled"] {
                    defaults.set(scenario != "disabled", forKey: "analyticsEnabled")
                    let selectedRange: AnalyticsRange = scenario == "thirty-days" ? .thirtyDays : .sevenDays
                    let snapshot = fixture(range: selectedRange, empty: scenario == "empty", quality: scenario == "partial" ? .partial : .exact)
                    let store = isolatedLayoutUsageStore(analyticsSnapshots: scenario == "loading" ? [:] : [selectedRange: snapshot],
                        initialSnapshot: UsageSnapshot(today: snapshot.usage, week: snapshot.usage, month: snapshot.usage,
                                                       allTime: snapshot.usage, quality: .exact, updatedAt: now), defaults: defaults)
                    let state = SettingsUsageAnalyticsState()
                    if scenario == "expanded" {
                        state.grouping = .model
                        state.selectedDate = snapshot.buckets[1].start
                        state.showsAllSessions = true
                        state.expandedSessions = ["session-7"]
                        state.expandedPeriods = [.week]
                    }
                    let navigation = MenuNavigation()
                    navigation.usageRange = selectedRange
                    let host = NSHostingView(rootView: ScrollView {
                        MenuPopoverView(accounts: account.store, navigation: navigation, embedded: true, analyticsState: state)
                    }
                    .defaultScrollAnchor(.top)
                    .background(.background)
                    .environmentObject(store).environmentObject(profile).environmentObject(limits).environmentObject(claude)
                    .defaultAppStorage(defaults).environment(\.colorScheme, dark ? .dark : .light))
                    host.sizingOptions = []
                    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: width, height: 560),
                                          styleMask: [.borderless], backing: .buffered, defer: false)
                    window.isReleasedWhenClosed = false
                    window.contentView = host
                    window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                    host.frame = NSRect(x: 0, y: 0, width: width, height: 560)
                    for _ in 0..<6 { host.layoutSubtreeIfNeeded() }
                    let name = "analytics-\(scenario)-\(Int(width))-\(dark ? "dark" : "light")"
                    let scrolls = descendants(NSScrollView.self, in: host)
                    XCTAssertEqual(scrolls.count, 1, name)
                    let scroll = try XCTUnwrap(scrolls.first)
                    XCTAssertLessThanOrEqual(scroll.documentView?.bounds.width ?? 0, scroll.contentSize.width + 1, name)
                    XCTAssertLessThanOrEqual(abs(scroll.contentView.bounds.minY), 1, name)
                    if scenario == "populated" {
                        XCTAssertGreaterThan(scroll.documentView?.bounds.height ?? 0, 560, name)
                    }
                    if scenario == "expanded" {
                        navigation.push(.session(id: "session-7", range: selectedRange))
                        for _ in 0..<6 { host.layoutSubtreeIfNeeded() }
                        navigation.back()
                        for _ in 0..<6 { host.layoutSubtreeIfNeeded() }
                        XCTAssertEqual(state.grouping, .model)
                        XCTAssertEqual(state.selectedDate, snapshot.buckets[1].start)
                        XCTAssertTrue(state.showsAllSessions)
                        XCTAssertTrue(state.expandedSessions.contains("session-7"))
                        XCTAssertTrue(state.expandedPeriods.contains(.week))
                    }
                    if let directory = ProcessInfo.processInfo.environment["CODERIM_ANALYTICS_CAPTURE_DIR"] {
                        let url = URL(fileURLWithPath: directory)
                        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
                        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                        host.cacheDisplay(in: host.bounds, to: bitmap)
                        try bitmap.representation(using: .png, properties: [:])?.write(to: url.appendingPathComponent(name + ".png"))
                        if (scenario == "populated" || scenario == "thirty-days" || scenario == "expanded"), let document = scroll.documentView {
                            let bitmap = try XCTUnwrap(document.bitmapImageRepForCachingDisplay(in: document.bounds))
                            document.cacheDisplay(in: document.bounds, to: bitmap)
                            try bitmap.representation(using: .png, properties: [:])?.write(to: url.appendingPathComponent(name + "-full.png"))
                        }
                    }
                    window.close()
                }
            }
        }
    }

    private func descendants<T: NSView>(_ type: T.Type, in view: NSView) -> [T] {
        ((view as? T).map { [$0] } ?? []) + view.subviews.flatMap { descendants(type, in: $0) }
    }

    private func fixture(range: AnalyticsRange = .sevenDays, empty: Bool = false, quality: DataQuality = .exact) -> AnalyticsSnapshot {
        let calendar = Calendar(identifier: .gregorian)
        let intervals = range.bucketIntervals(through: now, calendar: calendar)
        let buckets = intervals.enumerated().map { day, interval in
            UsageBucket(start: interval.start, end: interval.end, models: empty ? [] : (0..<7).map { model in
                let amount = Int64([170, 82, 64, 2, 38, 57, 10][day % 7] * (7 - model) * 100)
                return ModelUsageSummary(modelID: "model-\(model)", usage: TokenUsage(inputTokens: amount,
                    cachedInputTokens: amount / 2, outputTokens: amount / 4))
            })
        }
        let models = (0..<(empty ? 0 : 7)).map { index in
            ModelUsageSummary(modelID: "model-\(index)", usage: buckets.reduce(TokenUsage.zero) { total, bucket in
                total.adding(bucket.models[index].usage)
            })
        }
        let usage = models.reduce(TokenUsage.zero) { $0.adding($1.usage) }
        let sessionCount = empty ? 0 : 8
        let sessions: [SessionUsageSummary] = (0..<sessionCount).map { index in
            let name = index == 7 ? "CodeRim · A deliberately long workspace name to check truncation and disclosure" : "Workspace \(index)"
            let input = Int64(index + 1) * 800
            return SessionUsageSummary(id: "session-\(index)", projectID: "project", projectName: name,
                startedAt: intervals[0].start, lastActivityAt: now,
                usage: TokenUsage(inputTokens: input, cachedInputTokens: 200, outputTokens: 100),
                models: [], directSubagentCount: 0, imageAttachmentCount: 0, parentSessionID: nil)
        }
        return AnalyticsSnapshot(range: range, interval: .init(start: intervals[0].start, end: now), through: now,
                                 usage: usage, quality: quality, buckets: buckets, models: models, projects: [], sessions: sessions)
    }
}

private struct AnalyticsNoLimits: AccountLimitProviding {
    func readLimits() async throws -> AccountLimitsSnapshot {
        AccountLimitsSnapshot(windows: [], resetCredits: nil, fetchedAt: Date())
    }
}
