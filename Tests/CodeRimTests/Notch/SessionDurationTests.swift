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
}
