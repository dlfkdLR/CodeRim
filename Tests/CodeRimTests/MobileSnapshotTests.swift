import CodeRimShared
import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class MobileSnapshotTests: XCTestCase {
    let now = Date(timeIntervalSince1970: 1_790_000_000)
    func testSanitizedSnapshotNeverExportsCredentialsPathsOrThreadIDs() throws {
        let provider = CompanionProvider(id: "codex", name: "Codex", localUsage: nil,
            limits: .init(state: .ready, updatedAt: now, windows: [.init(id: "weekly", name: "Weekly", usedPercent: 25, resetsAt: now.addingTimeInterval(600))]))
        let session = AgentSession(id: "private-session", name: "/Users/private/project", detail: "Private title",
            state: .busy, waitingFor: nil, since: now, codexThreadID: "private-thread")
        let result = MobileConnectionStore.snapshot(.init(generatedAt: now, providers: [provider]),
            sessions: ["codex": [session]], shareTitles: false, now: now)
        let json = String(decoding: try JSONEncoder().encode(result), as: UTF8.self)
        for forbidden in ["private-session", "/Users/private", "Private title", "private-thread"] { XCTAssertFalse(json.contains(forbidden)) }
        XCTAssertEqual(result.providers[0].windows[0].remainingPercent, 75)
        XCTAssertEqual(result.sessions[0].phase, .working)
        XCTAssertNil(result.providers[0].todayTokens)
    }
    func testDisabledProvidersAreNotSharedAndRemoteUnknownDoesNotBecomeWorking() {
        let disabled = CompanionProvider(id: "claude", name: "Claude", localUsage: nil,
            limits: .init(state: .disabled, updatedAt: nil, windows: []), enabled: false)
        let codex = CompanionProvider(id: "codex", name: "Codex", localUsage: nil,
            limits: .init(state: .unavailable, updatedAt: nil, windows: []))
        let session = AgentSession(id: "id", name: "remote", detail: "Title", state: .unavailable,
            waitingFor: nil, since: now, remoteHostID: "remote-secret")
        let result = MobileConnectionStore.snapshot(.init(providers: [codex, disabled]),
            sessions: ["codex": [session], "claude": [session]], shareTitles: true, now: now)
        XCTAssertEqual(result.providers.map(\.id), ["codex"])
        XCTAssertEqual(result.sessions.count, 1)
        XCTAssertEqual(result.sessions.first?.phase, .unavailable)
        XCTAssertEqual(result.sessions.first?.title, "Title")
    }
    func testStaleUsageAndInvalidNumbersNeverAcquireHealthyZeroValues() {
        let provider = CompanionProvider(id: "codex", name: "Codex", localUsage: nil,
            limits: .init(state: .ready, updatedAt: now.addingTimeInterval(-600), windows: [.init(id: "x", name: "Week", usedPercent: .nan)]))
        let result = MobileConnectionStore.snapshot(.init(providers: [provider]), sessions: [:], shareTitles: false, now: now)
        XCTAssertEqual(result.providers[0].state, "stale")
        XCTAssertNil(result.providers[0].windows[0].remainingPercent)
        XCTAssertNil(result.providers[0].todayTokens)
    }
    func testPayloadBoundsKeepWaitingFirstAndPreserveHeadline() {
        let provider = CompanionProvider(id: "codex", name: "Codex", localUsage: nil,
            limits: .init(state: .ready, updatedAt: now,
                windows: [.init(id: "daily", name: "Day"), .init(id: "weekly", name: "Week"), .init(id: "other", name: "Other")], headlineID: "weekly"))
        let sessions = (0..<70).map { i in AgentSession(id: "\(i)", name: "Session", detail: String(repeating: "한", count: 200),
            state: i == 69 ? .waiting : .busy, waitingFor: nil, since: now) }
        let result = MobileConnectionStore.snapshot(.init(providers: [provider]), sessions: ["codex": sessions], shareTitles: true, now: now)
        XCTAssertEqual(result.providers[0].windows.map(\.name), ["Week", "Day"])
        XCTAssertEqual(result.sessions.count, 64)
        XCTAssertEqual(result.sessions[0].phase, .waiting)
        XCTAssertEqual(result.sessions[0].title.count, 60)
    }
}
