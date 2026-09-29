import Combine
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class LocalTokenUsageTests: XCTestCase {
    func testZeroLoadingUnavailablePartialAndStaleStayDistinct() {
        var snapshot = UsageSnapshot.empty
        XCTAssertEqual(LocalTokenUsage(snapshot: snapshot, hasLoaded: false).text(style: .detailed), "Loading…")
        XCTAssertEqual(LocalTokenUsage(snapshot: snapshot, hasLoaded: true).text(style: .detailed), "Unavailable")
        snapshot.quality = .exact
        XCTAssertEqual(LocalTokenUsage(snapshot: snapshot, hasLoaded: true).text(style: .detailed), "0 tokens")
        snapshot.today = TokenUsage(inputTokens: 500, cachedInputTokens: 400, outputTokens: 26)
        snapshot.quality = .partial
        XCTAssertEqual(LocalTokenUsage(snapshot: snapshot, hasLoaded: true).text(style: .detailed), "526 tokens")
        snapshot.quality = .stale
        XCTAssertEqual(LocalTokenUsage(snapshot: snapshot, hasLoaded: true).text(style: .detailed), "526 tokens (stale)")
    }

    func testArchiveImmediatelyReceivesLocalTotalWithoutRefreshingItsQuota() throws {
        let suite = "LocalTokenUsageTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let archive = UsageArchive(defaults: defaults)
        let oldDate = Date().addingTimeInterval(-46 * 3600)
        let provider = OfflineProvider()
        let old = ProviderSnapshot(id: "claude", displayName: "Claude Code", glyph: .claude,
            fidelity: .official, status: .ok,
            windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.04)])
        archive.save(["claude": (old, oldDate)])
        let notch = NotchUsageStore(providers: [provider], archive: archive)
        var snapshot = UsageSnapshot.empty
        snapshot.today = TokenUsage(inputTokens: 608595, cachedInputTokens: 544279, outputTokens: 1931)
        snapshot.quality = .exact
        let usage = UsageStore(provider: .claude, initialSnapshot: snapshot, automaticallyRefresh: false)
        notch.bindLocalUsage(usage, providerID: "claude")
        XCTAssertEqual(notch.snapshots.first?.todaysTokens, 610526)
        XCTAssertEqual(notch.snapshots.first?.status, .stale(since: oldDate))
        XCTAssertEqual(notch.snapshots.first?.headline?.usedFraction, 0.04)
        XCTAssertEqual(provider.fetchCount, 0)
        XCTAssertNil(archive.load()["claude"]?.snapshot.todaysTokens, "Daily totals must not survive as yesterday's Today")
        notch.invalidateAccount(providerID: "claude")
        XCTAssertEqual(notch.snapshots.first?.todaysTokens, 610526, "Switching accounts must keep This Mac totals")
        XCTAssertNotNil(notch.snapshots.first?.localTokenUsage)
        XCTAssertTrue(notch.snapshots.first?.windows.isEmpty == true, "Old account quota must still be cleared")
        XCTAssertEqual(provider.fetchCount, 0)
    }

    func testTranscriptChangesReachTheNotchWhileQuotaFetchFails() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("notch-local-\(UUID())")
        let sources = root.appendingPathComponent("projects")
        try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let suite = "LocalTokenUsageTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(RefreshMode.manual.rawValue, forKey: "refreshMode")
        let db = try SQLiteDatabase(url: root.appendingPathComponent("Claude.sqlite"))
        let collector = CodexUsageCollector(database: db, roots: [sources], provider: .claude)
        let usage = UsageStore(provider: .claude, automaticallyRefresh: false, collector: collector, defaults: defaults)
        let provider = OfflineProvider()
        let notch = NotchUsageStore(providers: [provider], archive: UsageArchive(defaults: defaults))
        notch.bindLocalUsage(usage, providerID: "claude")
        for output in [10, 25] {
            let row = """
            {"type":"assistant","timestamp":"\(Date().formatted(.iso8601))","sessionId":"test","message":{"id":"same-message","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":100,"output_tokens":\(output)}}}
            """
            try Data((row + "\n").utf8).write(to: sources.appendingPathComponent("session.jsonl"))
            await usage.refresh()
            for _ in 0..<100 {
                if notch.snapshots.first?.todaysTokens == 100 + output { break }
                try await Task.sleep(for: .milliseconds(10))
            }
            XCTAssertEqual(notch.snapshots.first?.todaysTokens, 100 + output)
        }
        XCTAssertEqual(provider.fetchCount, 0, "Token updates must never trigger provider requests")
        await notch.refresh()
        XCTAssertEqual(provider.fetchCount, 1)
        XCTAssertEqual(notch.snapshots.first?.todaysTokens, 125, "A quota error must not erase local tokens")
    }

    func testProviderIsolationAndKnownZero() throws {
        let suite = "LocalTokenUsageTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let notch = NotchUsageStore(providers: [OfflineProvider()], archive: UsageArchive(defaults: defaults))
        var snapshot = UsageSnapshot.empty
        snapshot.quality = .exact
        let codex = UsageStore(provider: .codex, initialSnapshot: snapshot, automaticallyRefresh: false)
        notch.bindLocalUsage(codex, providerID: "claude")
        XCTAssertNil(notch.snapshots.first?.localTokenUsage)
        let claude = UsageStore(provider: .claude, initialSnapshot: snapshot, automaticallyRefresh: false)
        notch.bindLocalUsage(claude, providerID: "claude")
        XCTAssertEqual(notch.snapshots.first?.todaysTokens, 0)
        XCTAssertEqual(notch.snapshots.first?.localTokenText(style: .detailed), "0 tokens")
    }

    func testCalendarRecalculationReplacesYesterdaysLocalTotalWithoutQuotaFetch() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("notch-rollover-\(UUID())")
        let sources = root.appendingPathComponent("projects")
        try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let suite = "LocalTokenUsageTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let yesterday = try XCTUnwrap(Calendar.current.date(byAdding: .day, value: -1, to: Date()))
        let row = """
        {"type":"assistant","timestamp":"\(yesterday.formatted(.iso8601))","sessionId":"test","message":{"id":"yesterday-message","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":100,"output_tokens":10}}}
        """
        try Data((row + "\n").utf8).write(to: sources.appendingPathComponent("session.jsonl"))
        let db = try SQLiteDatabase(url: root.appendingPathComponent("Claude.sqlite"))
        let collector = CodexUsageCollector(database: db, roots: [sources], provider: .claude)
        let previous = try await collector.refresh(now: yesterday, weekStart: .monday)
        let usage = UsageStore(provider: .claude, initialSnapshot: previous.snapshot,
                               automaticallyRefresh: false, collector: collector, defaults: defaults)
        let provider = OfflineProvider()
        let notch = NotchUsageStore(providers: [provider], archive: UsageArchive(defaults: defaults))
        notch.bindLocalUsage(usage, providerID: "claude")
        XCTAssertEqual(notch.snapshots.first?.todaysTokens, 110)
        await usage.recalculateVisiblePeriods()
        for _ in 0..<100 {
            if notch.snapshots.first?.todaysTokens == 0 { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(notch.snapshots.first?.todaysTokens, 0)
        XCTAssertEqual(usage.snapshot.allTime.totalTokens, 110)
        XCTAssertEqual(provider.fetchCount, 0)
    }

    func testLocalTokenStatesRenderInsideTheirAllocatedCard() throws {
        let now = Date()
        let cards = [DataQuality.exact, .partial, .stale, .unavailable].map { quality in
            var usage = UsageSnapshot.empty
            usage.quality = quality
            usage.today = TokenUsage(inputTokens: 608595, cachedInputTokens: 544279, outputTokens: 1931)
            return ProviderSnapshot(id: "claude", displayName: "Claude Code", glyph: .claude,
                fidelity: .official, status: .stale(since: now.addingTimeInterval(-46 * 3600)),
                windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.04)],
                localTokenUsage: LocalTokenUsage(snapshot: usage, hasLoaded: true))
        }
        let content = VStack(spacing: 12) {
            ForEach(Array(cards.enumerated()), id: \.offset) { _, snapshot in
                TooltipCard(snapshot: snapshot, activity: nil, now: now)
            }
        }.padding(16).background(Color.gray)
        let renderer = ImageRenderer(content: content)
        renderer.scale = 2
        let image = try XCTUnwrap(renderer.nsImage)
        if let path = ProcessInfo.processInfo.environment["LOCAL_TOKEN_RENDER_PATH"] {
            let data = try XCTUnwrap(image.tiffRepresentation)
            let png = try XCTUnwrap(NSBitmapImageRep(data: data)?.representation(using: .png, properties: [:]))
            try png.write(to: URL(fileURLWithPath: path))
        }
        XCTAssertGreaterThan(image.size.height, 300)
    }

    func testTodayIncludesOnlyPostMidnightDeltasAndIgnoresRepeatedSnapshots() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("notch-midnight-\(UUID())")
        let sources = root.appendingPathComponent("sessions")
        try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "Asia/Seoul"))
        let iso = ISO8601DateFormatter()
        let midnight = try XCTUnwrap(iso.date(from: "2026-09-24T15:00:00Z"))
        var input = 1_000, cached = 0, output = 10
        var lines: [String] = []
        func append(seconds: Double, last: [String: Int]) throws {
            let total = ["input_tokens": input, "cached_input_tokens": cached,
                         "output_tokens": output, "total_tokens": input + output]
            let row: [String: Any] = [
                "timestamp": iso.string(from: midnight.addingTimeInterval(seconds)),
                "type": "event_msg",
                "payload": ["type": "token_count", "info": [
                    "total_token_usage": total, "last_token_usage": last]]
            ]
            lines.append(String(decoding: try JSONSerialization.data(withJSONObject: row), as: UTF8.self))
        }
        try append(seconds: -1, last: ["input_tokens": input, "cached_input_tokens": cached,
                                      "output_tokens": output, "total_tokens": input + output])
        let deltas = [(520, 113634, 113408, 229), (525, 115656, 113408, 54),
                      (962, 56930, 19200, 108), (969, 58772, 56704, 111),
                      (980, 59101, 58624, 214), (989, 69786, 58880, 217),
                      (1798, 70015, 69888, 357)]
        for (seconds, newInput, newCached, newOutput) in deltas {
            input += newInput
            cached += newCached
            output += newOutput
            let last = ["input_tokens": newInput, "cached_input_tokens": newCached,
                        "output_tokens": newOutput, "total_tokens": newInput + newOutput]
            try append(seconds: Double(seconds), last: last)
            try append(seconds: Double(seconds + 1), last: last)
        }
        try Data((lines.joined(separator: "\n") + "\n").utf8)
            .write(to: sources.appendingPathComponent("session.jsonl"))
        let db = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: db, roots: [sources])
        let now = midnight.addingTimeInterval(22 * 3_600 + 32 * 60)
        let result = try await collector.refresh(now: now, calendar: calendar, weekStart: .monday)
        XCTAssertEqual(result.snapshot.today.totalTokens, 545_184)
        XCTAssertEqual(result.snapshot.today.cachedInputTokens, 490_112)
        XCTAssertEqual(result.snapshot.allTime.totalTokens, 546_194)
        XCTAssertEqual(LocalTokenUsage(snapshot: result.snapshot, hasLoaded: true).total, 545_184)
        let nextDay = try await collector.cachedSnapshot(now: midnight.addingTimeInterval(86_400),
                                                         calendar: calendar, weekStart: .monday)
        XCTAssertEqual(nextDay.today.totalTokens, 0)
        XCTAssertEqual(nextDay.allTime.totalTokens, 546_194)
        calendar.timeZone = try XCTUnwrap(TimeZone(secondsFromGMT: 0))
        let utc = try await collector.cachedSnapshot(now: now, calendar: calendar, weekStart: .monday)
        XCTAssertEqual(utc.today.totalTokens, 0, "Today must follow the selected local calendar, not UTC")
    }

    func testLocalScopeHelpDoesNotMislabelProviderReportedTokensOrAddHeight() throws {
        let remote = ProviderSnapshot(id: "grok", displayName: "Grok", glyph: .openai,
            fidelity: .official, status: .ok, windows: [], todaysTokens: 545_184)
        XCTAssertTrue(remote.showsLocalTokens)
        XCTAssertNil(remote.localTokenScopeHelp)
        for id in ["codex", "claude"] {
            let local = ProviderSnapshot(id: id, displayName: "Local", glyph: .openai,
                fidelity: .official, status: .ok, windows: [], todaysTokens: 545_184)
            XCTAssertEqual(local.localTokenScopeHelp, LocalTokenUsage.scopeHelp)
            let difference = NotchLayout.cardHeight(for: local, now: Date())
                - NotchLayout.cardHeight(for: remote, now: Date())
            XCTAssertEqual(difference, 0, accuracy: 0.001)
        }
    }

    func testLoadingLocalUsageKeepsTheSameSessionBudgetAsLoadedUsage() {
        for height in [600.0, 800.0, 1200.0] {
            let model = NotchViewModel()
            model.screenSize = CGSize(width: 1440, height: height)
            let snapshot = ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
                fidelity: .official, status: .ok, windows: [], todaysTokens: 545_184)
            model.snapshots = [snapshot]
            let loadedCap = model.sessionCap
            var loading = snapshot
            loading.todaysTokens = nil
            model.snapshots = [loading]
            XCTAssertEqual(model.sessionCap, loadedCap)
        }
    }

    private final class OfflineProvider: NotchProvider {
        let id = "claude"
        let displayName = "Claude Code"
        let glyph: ProviderGlyph = .claude
        var fetchCount = 0
        func fetchSnapshot() async throws -> ProviderSnapshot {
            fetchCount += 1
            throw NotchProviderError.badResponse(status: 503)
        }
        func account() -> ProviderAccount? { nil }
        var signInRoute: SignInRoute { .guidance("") }
        func signOut() async {}
        func presentSignIn() {}
        func forgetCachedCredential() {}
    }
}
