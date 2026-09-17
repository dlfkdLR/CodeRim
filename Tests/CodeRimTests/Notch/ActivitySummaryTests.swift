import XCTest
@testable import CodeRim

final class NotchActivitySummaryTests: XCTestCase {
    private func session(_ state: AgentSession.State, name: String = "s") -> AgentSession {
        AgentSession(id: name, name: name, detail: "Terminal · \(name)",
                     state: state, waitingFor: nil, since: Date())
    }

    func testNothingRunningMeansNoCell() {
        XCTAssertNil(ActivitySummary(sessions: []))
    }

    /// Blocked outranks busy: it is the only state that is asking you for
    /// something, so it must not be hidden behind a session that is merely busy.
    func testWaitingOutranksWorking() {
        let summary = ActivitySummary(sessions: [session(.busy), session(.waiting), session(.idle)])
        XCTAssertEqual(summary?.state, .waiting)
        XCTAssertEqual(summary?.label, "waiting")
    }

    func testWorkingOutranksIdle() {
        XCTAssertEqual(ActivitySummary(sessions: [session(.idle), session(.busy)])?.state, .working)
    }

    func testAllIdleReadsAsIdle() {
        XCTAssertEqual(ActivitySummary(sessions: [session(.idle), session(.idle)])?.state, .idle)
    }

    /// Working must not borrow a colour from the usage scale — the indicator
    /// sits inside a ring whose colour already means something else.
    func testWorkingIsNeutralAndWaitingIsNot() {
        XCTAssertEqual(ActivitySummary(sessions: [session(.busy)])?.color, NotchPalette.textPrimary)
        XCTAssertEqual(ActivitySummary(sessions: [session(.waiting)])?.color, NotchPalette.watch)
    }
    private func agent(_ id: String, parent: String? = nil,
                       state: AgentSession.State = .busy) -> AgentSession {
        AgentSession(id: "codex.\(id)", name: "CodeRim", detail: "Task \(id)",
                     state: state, waitingFor: nil, since: Date(timeIntervalSince1970: 100),
                     codexThreadID: id, parentThread: parent.map { .init(id: $0, title: "Parent \($0)") })
    }

    func testParentAppearsOnceWithSiblingAndNestedAgents() throws {
        let summary = try XCTUnwrap(ActivitySummary(sessions: [agent("c", parent: "a"),
            agent("a"), agent("d", parent: "b"), agent("b", parent: "a")]))
        let rows = summary.displayRows
        XCTAssertEqual(rows.map { $0.session.codexThreadID }, ["a", "b", "d", "c"])
        XCTAssertEqual(rows.map(\.depth), [0, 1, 2, 1])
        XCTAssertFalse(rows.contains(where: \.isContextOnly))
        XCTAssertTrue(rows[0].isParent)
        XCTAssertTrue(rows[1].isParent)
    }

    func testInactiveParentContextDoesNotBecomeAnActiveSession() throws {
        let summary = try XCTUnwrap(ActivitySummary(sessions: [agent("b", parent: "a"),
                                                                  agent("c", parent: "a")]))
        XCTAssertEqual(summary.sessions.count, 2)
        XCTAssertEqual(summary.displayRows.count, 3)
        XCTAssertTrue(summary.displayRows[0].isContextOnly)
        XCTAssertEqual(summary.displayRows[0].session.detail, "Parent a")
        XCTAssertEqual(summary.displayRows.map(\.depth), [0, 1, 1])
        XCTAssertEqual(summary.state, .working)
        XCTAssertEqual(summary.presentation(cap: 3).hidden, 0)
    }

    func testSmallCardKeepsParentContextWithChildAndCountsOnlyHiddenAgents() throws {
        let summary = try XCTUnwrap(ActivitySummary(sessions: [agent("b", parent: "a"),
                                                                  agent("c", parent: "a")]))
        let one = summary.presentation(cap: 1)
        XCTAssertEqual(one.rows.count, 1)
        XCTAssertFalse(one.rows[0].isContextOnly)
        XCTAssertEqual(one.rows[0].inlineParent?.title, "Parent a")
        XCTAssertEqual(one.hidden, 1)
        XCTAssertEqual(summary.presentation(cap: 0).hidden, 2)
        XCTAssertEqual(summary.presentation(cap: 2).rows.map(\.isContextOnly), [true, false])
        XCTAssertEqual(summary.presentation(cap: 2).hidden, 1)
    }

    func testWaitingChildPrioritizesTheWholeGroupWithoutDuplicatingItsParent() throws {
        let summary = try XCTUnwrap(ActivitySummary(sessions: [agent("z"),
            agent("a", state: .idle), agent("b", parent: "a", state: .waiting)]))
        XCTAssertEqual(summary.displayRows.map { $0.session.codexThreadID }, ["a", "b", "z"])
        XCTAssertEqual(summary.displayRows.first?.session.state, .idle)
    }

    func testCyclicMetadataStillDisplaysEachRealSessionOnce() throws {
        let summary = try XCTUnwrap(ActivitySummary(sessions: [agent("a", parent: "b"),
                                                                  agent("b", parent: "a")]))
        XCTAssertEqual(summary.displayRows.count, 2)
        XCTAssertEqual(Set(summary.displayRows.map(\.id)), ["codex.a", "codex.b"])
    }

}

// `NotchCursorActivityTests` — Cursor's `composerHeaders`-based working state —
// lands in Phase 5 with the Cursor provider; `CursorActivityMonitor` is not
// ported yet.
