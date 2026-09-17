import XCTest
import SwiftUI
@testable import CodexMeter

final class SessionFocusTests: XCTestCase {
    private func session(threadID: String? = nil, pid: Int32? = nil) -> AgentSession {
        AgentSession(id: "codex-work.profile.display-id", name: "Project", detail: "Task",
                     state: .busy, waitingFor: nil, since: Date(),
                     processID: pid, codexThreadID: threadID)
    }

    func testCodexThreadTakesPrecedenceOverOwningApplication() throws {
        let id = "01a0ac9e-2019-7872-ba5f-c293768dd2d5"
        XCTAssertEqual(SessionFocus.target(for: session(threadID: id, pid: 42)),
                       .codexThread(try XCTUnwrap(URL(string: "codex://threads/\(id)"))))
    }

    func testMalformedIDsCannotOpenOtherRoutes() {
        for id in ["", "new", "../settings", "codex://settings", "invalid-id",
                   "01a0ac9e-2019-7872-ba5f-c293768dd2d5?prompt=hello",
                   "01a0ac9e-2019-7872-ba5f-c293768dd2d5/extra"] {
            XCTAssertNil(SessionFocus.target(for: session(threadID: id)), id)
        }
    }

    @MainActor
    func testClickableRowsPreserveCardContentSize() {
        let snapshot = ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
                                        fidelity: .official, status: .ok, windows: [])
        let now = Date()
        let plain = session()
        let linked = session(threadID: "01a0ac9e-2019-7872-ba5f-c293768dd2d5")
        func size(for session: AgentSession) -> NSSize {
            let card = TooltipCard(snapshot: snapshot, activity: ActivitySummary(sessions: [session]), now: now)
            return NSHostingView(rootView: card.cardContent.frame(width: NotchLayout.cardTextWidth)).fittingSize
        }
        XCTAssertEqual(size(for: linked).width, size(for: plain).width, accuracy: 0.5)
        XCTAssertEqual(size(for: linked).height, size(for: plain).height, accuracy: 0.5)
    }

    @MainActor
    func testParentGroupsFitTheCardBudgetAndKeepChildClickTargets() throws {
        let parentID = "11111111-1111-1111-1111-111111111111"
        let children = ["22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"]
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let agents = children.enumerated().map { index, id in
            AgentSession(id: "codex.\(id)", name: "CodexMeter", detail: index == 0 ? "코드 검토" : "동작 검증",
                         state: .busy, waitingFor: nil, since: now.addingTimeInterval(-120),
                         codexThreadID: id, parentThread: .init(id: parentID, title: "진행 상황에서 채팅 열기"))
        }
        let activity = try XCTUnwrap(ActivitySummary(sessions: agents))
        let snapshot = ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
            fidelity: .official, status: .ok,
            windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.53)])
        for cap in 0...4 {
            let card = TooltipCard(snapshot: snapshot, activity: activity, now: now, sessionCap: cap)
            let natural = ImageRenderer(content: card.cardContent.frame(width: NotchLayout.cardTextWidth))
            let height = try XCTUnwrap(natural.nsImage).size.height
            let budget = NotchLayout.cardHeight(for: snapshot, sessionCount: activity.displayRows.count,
                                                sessionCap: cap, now: now) - 2 * NotchLayout.cardPadding
            XCTAssertLessThanOrEqual(height, budget + 1, "cap \(cap)")
            for row in activity.presentation(cap: cap).rows where !row.isContextOnly {
                let id = try XCTUnwrap(row.session.codexThreadID)
                XCTAssertTrue(children.contains(id))
                XCTAssertEqual(SessionFocus.target(for: row.session),
                               .codexThread(try XCTUnwrap(URL(string: "codex://threads/\(id)"))))
            }
        }
        if let path = ProcessInfo.processInfo.environment["PARENT_GROUP_RENDER_PATH"] {
            // Show a standalone chat beside a parent group so their title and
            // project hierarchy can be compared in the rendered evidence.
            let parent = AgentSession(id: "codex.\(parentID)", name: "CodexMeter", detail: "전체 변경사항 릴리즈",
                state: .busy, waitingFor: nil, since: now.addingTimeInterval(-240), codexThreadID: parentID)
            let standalone = AgentSession(id: "codex.standalone", name: "CodexMeter", detail: "브랜치 접두사 규칙 확인",
                state: .busy, waitingFor: nil, since: now.addingTimeInterval(-60),
                codexThreadID: "44444444-4444-4444-4444-444444444444")
            let preview = ActivitySummary(sessions: [standalone, parent] + agents.map { agent in
                AgentSession(id: agent.id, name: agent.name, detail: agent.detail, state: agent.state,
                    waitingFor: agent.waitingFor, since: agent.since, codexThreadID: agent.codexThreadID,
                    parentThread: .init(id: parentID, title: parent.detail))
            })
            let renderer = ImageRenderer(content: TooltipCard(snapshot: snapshot, activity: preview, now: now,
                                                               sessionCap: 4).padding(20).background(Color.black))
            renderer.scale = 3
            let image = try XCTUnwrap(renderer.nsImage)
            let tiff = try XCTUnwrap(image.tiffRepresentation)
            let png = try XCTUnwrap(NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]))
            try png.write(to: URL(fileURLWithPath: path))
        }
    }

    func testProcessSessionsStillOpenTheirOwningApplication() {
        XCTAssertEqual(SessionFocus.target(for: session(pid: 42)), .application(42))
        XCTAssertNil(SessionFocus.target(for: session()))
        for pid: Int32 in [-1, 0, 1] {
            XCTAssertNil(SessionFocus.target(for: session(pid: pid)))
        }
    }
}
