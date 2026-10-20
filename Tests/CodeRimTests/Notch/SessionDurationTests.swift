import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class SessionDurationTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    private func session(_ state: AgentSession.State, since: Date? = nil) -> AgentSession {
        AgentSession(id: "work", name: "CodeRim", detail: "작업 진행 시간 확인", state: state,
                     waitingFor: nil, since: since ?? now.addingTimeInterval(-10_020),
                     codexThreadID: "00000000-0000-0000-0000-000000000001")
    }

    func testOnlyKnownActiveStatesHaveOptInDuration() {
        for state: AgentSession.State in [.busy, .waiting, .idle, .unavailable] {
            XCTAssertNil(SessionDuration.text(for: session(state), enabled: false, now: now))
        }
        for state: AgentSession.State in [.busy, .waiting] {
            XCTAssertEqual(SessionDuration.text(for: session(state), enabled: true, now: now), "2 hr 47 min")
        }
        for state: AgentSession.State in [.idle, .unavailable] {
            XCTAssertNil(SessionDuration.text(for: session(state), enabled: true, now: now))
        }
    }

    func testInvalidOrFutureStartNeverProducesUnknownOrInventedDuration() {
        for date in [Date(timeIntervalSince1970: .nan), Date(timeIntervalSince1970: .infinity),
                     now.addingTimeInterval(1), Date(timeIntervalSince1970: -Double.greatestFiniteMagnitude)] {
            XCTAssertNil(SessionDuration.text(for: session(.busy, since: date), enabled: true, now: now))
        }
        XCTAssertEqual(SessionDuration.text(for: session(.busy, since: now), enabled: true, now: now), "<1 min")
    }

    func testDefaultOffPersistsOptInAndKeepsRowGeometry() throws {
        let suite = "SessionDurationTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        AppPreferences.registerDefaults(in: defaults)
        XCTAssertFalse(defaults.bool(forKey: AppPreferences.notchShowSessionDurationKey))
        let snapshot = ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
            fidelity: .official, status: .ok,
            windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.81)])
        let summary = try XCTUnwrap(ActivitySummary(sessions: [session(.busy)]))
        var sizes: [CGSize] = []
        for enabled in [false, true] {
            defaults.set(enabled, forKey: AppPreferences.notchShowSessionDurationKey)
            AppPreferences.registerDefaults(in: defaults)
            XCTAssertEqual(defaults.bool(forKey: AppPreferences.notchShowSessionDurationKey), enabled)
            let card = TooltipCard(snapshot: snapshot, activity: summary, now: now)
                .defaultAppStorage(defaults)
                .transaction { $0.animation = nil; $0.disablesAnimations = true }
            let renderer = ImageRenderer(content: card.padding(20).background(Color.black))
            renderer.scale = 3
            let image = try XCTUnwrap(renderer.nsImage)
            sizes.append(image.size)
            if let folder = ProcessInfo.processInfo.environment["SESSION_DISCLOSURE_RENDER_DIR"] {
                let tiff = try XCTUnwrap(image.tiffRepresentation)
                let png = try XCTUnwrap(NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]))
                try png.write(to: URL(fileURLWithPath: folder).appendingPathComponent("duration-\(enabled ? "on" : "off").png"))
            }
        }
        XCTAssertEqual(sizes[0], sizes[1])
        XCTAssertFalse(defaults.bool(forKey: AppPreferences.notchShowUnknownSessionsKey),
                       "Duration preference must not turn unknown tasks on")
    }
}
