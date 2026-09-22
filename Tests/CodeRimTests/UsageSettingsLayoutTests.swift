import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class UsageSettingsLayoutTests: XCTestCase {
    func testDashboardAndDetailsFitWithoutNestedScrolling() async throws {
        _ = NSApplication.shared
        let suite = "CodeRim.UsageDashboard.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("detailed", forKey: "numberStyle")
        defaults.set(true, forKey: "profileSyncEnabled")
        defaults.set(true, forKey: "analyticsEnabled")
        defaults.set(true, forKey: "projectsEnabled")
        defaults.set(true, forKey: "sessionsEnabled")
        defaults.set(true, forKey: "showCachedInput")
        defaults.set(false, forKey: "costEstimatesEnabled")
        let now = Date()
        let usage = TokenUsage(inputTokens: 8_766_241, cachedInputTokens: 8_559_232, outputTokens: 20_849)
        let snapshot = UsageSnapshot(today: usage, week: usage, month: usage, allTime: usage,
                                     quality: .exact, updatedAt: now)
        let profile = ProfileUsageStore(defaults: defaults) { _, _, _ in
            ProfileUsageSnapshot(today: 123, week: 456_842_996, month: 3_627_987_895, lifetime: 8_801_852_379,
                                 statsAsOf: now, generatedAt: now)
        }
        await profile.refresh(weekStart: .monday)
        let accounts = try AccountLayoutFixture(state: .longEmail)
        let claude = try makeConnectedClaudeStore(fetchedAt: now)
        await claude.refresh()
        let limits = AccountLimitStore(provider: DashboardLimitProvider(now: now), defaults: defaults, pollingInterval: nil)
        await limits.refresh()
        XCTAssertEqual(limits.status, .ready)
        let analytics = AnalyticsSnapshot(range: .thirtyDays,
            interval: AnalyticsRange.thirtyDays.interval(through: now, calendar: .current),
            through: now, usage: usage, quality: .exact, buckets: [], models: [],
            projects: (1...20).map { index in
                ProjectUsageSummary(id: "project-\(index)",
                    name: index == 1 ? "CodeRim · A long workspace name for layout verification" : "Workspace \(index)",
                    usage: usage, models: [], sessionCount: index)
            }, sessions: [])

        for width: CGFloat in [579, 920] {
            for dark in [false, true] {
                for scenario in ["codex", "claude", "empty", "period", "limits", "analytics"] {
                    let provider: UsageProvider = scenario == "claude" ? .claude : .codex
                    defaults.set(provider.rawValue, forKey: "usageProvider")
                    let navigation = MenuNavigation(path: scenario == "period" ? [.period(.month)]
                        : scenario == "analytics" ? [.projects] : [])
                    let size = NSSize(width: width, height: 560)
                    let host = NSHostingView(rootView:
                        ScrollView {
                            MenuPopoverView(accounts: accounts.store, navigation: navigation,
                                            section: scenario == "limits" ? .codex : .overview, embedded: true)
                        }
                        .background(.background)
                        .environmentObject(isolatedLayoutUsageStore(provider: provider,
                            analyticsSnapshots: scenario == "analytics" ? [.thirtyDays: analytics] : [:],
                            initialSnapshot: scenario == "empty" ? .empty : snapshot,
                            automaticallyRefresh: false, defaults: defaults))
                        .environmentObject(profile)
                        .environmentObject(limits)
                        .environmentObject(claude)
                        .defaultAppStorage(defaults)
                        .environment(\.colorScheme, dark ? .dark : .light)
                    )
                    host.sizingOptions = []
                    let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                        styleMask: [.borderless], backing: .buffered, defer: false)
                    window.isReleasedWhenClosed = false
                    defer { window.close() }
                    window.contentView = host
                    window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                    host.frame = NSRect(origin: .zero, size: size)
                    for _ in 0..<6 { host.layoutSubtreeIfNeeded() }

                    let name = "usage-\(scenario)-\(Int(width))-\(dark ? "dark" : "light")"
                    let scrolls = descendants(of: NSScrollView.self, in: host)
                    XCTAssertEqual(scrolls.count, 1, "\(name): Settings must own the only scroll viewport")
                    for scroll in scrolls {
                        XCTAssertLessThanOrEqual(scroll.documentView?.bounds.width ?? 0, scroll.contentSize.width + 1, name)
                    }
                    if let directory = ProcessInfo.processInfo.environment["CODERIM_USAGE_CAPTURE_DIR"] {
                        let url = URL(fileURLWithPath: directory, isDirectory: true)
                        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
                        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                        host.cacheDisplay(in: host.bounds, to: bitmap)
                        try bitmap.representation(using: .png, properties: [:])?.write(to: url.appendingPathComponent("\(name).png"))
                    }
                }
            }
        }
    }

    private func descendants<T: NSView>(of type: T.Type, in view: NSView) -> [T] {
        ((view as? T).map { [$0] } ?? []) + view.subviews.flatMap { descendants(of: type, in: $0) }
    }
}

private struct DashboardLimitProvider: AccountLimitProviding {
    let now: Date

    func readLimits() async throws -> AccountLimitsSnapshot {
        AccountLimitsSnapshot(windows: [
            AccountLimitWindow(id: "weekly", limitID: "codex", displayName: "Codex", windowDurationMinutes: 10_080,
                               usedPercent: 50, resetsAt: now.addingTimeInterval(4 * 86_400)),
            AccountLimitWindow(id: "spark", limitID: "spark", displayName: "GPT-5.3-Codex-Spark", windowDurationMinutes: 300,
                               usedPercent: 10, resetsAt: now.addingTimeInterval(3_600))
        ], resetCredits: ResetCreditSummary(availableCount: 2, unlimited: false, expiresAt: nil), fetchedAt: now)
    }
}
