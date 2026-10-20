import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class SessionDisclosureTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    private func fixture() -> (ProviderSnapshot, ActivitySummary) {
        let snapshot = ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
            fidelity: .official, status: .ok,
            windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.81)])
        var sessions: [AgentSession] = []
        for index in 0..<8 {
            let local = index == 0
            let state: AgentSession.State = local ? .busy : .unavailable
            let thread = String(format: "00000000-0000-0000-0000-%012d", index)
            let since = now.addingTimeInterval(-Double(index) * 60)
            sessions.append(AgentSession(id: "session-\(index)", name: local ? "CodeRim" : "Vispace",
                detail: local ? "로컬 작업" : "원격 작업 \(index)", state: state,
                waitingFor: nil, since: since, codexThreadID: thread,
                remoteHostID: local ? nil : "remote:pc"))
        }
        return (snapshot, ActivitySummary(sessions: sessions)!)
    }

    func testUnknownVisibilityDefaultsOffPersistsAndFiltersBeforeCounting() throws {
        let suite = "SessionDisclosureTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let (snapshot, activity) = fixture()
        let model = NotchViewModel(defaults: defaults)
        model.snapshots = [snapshot]
        model.sessions = ["codex": activity.sessions]
        XCTAssertFalse(model.showUnknownSessions)
        XCTAssertEqual(model.activity(for: "codex")?.sessions.count, 1)
        XCTAssertEqual(model.activity(for: "codex")?.presentation(cap: 2).hidden, 0)
        if let directory = ProcessInfo.processInfo.environment["SESSION_DISCLOSURE_RENDER_DIR"] {
            let card = TooltipCard(snapshot: snapshot, activity: model.activity(for: "codex"), now: now)
            try save(ImageRenderer(content: card.padding(20).background(Color.black)),
                     to: URL(fileURLWithPath: directory).appendingPathComponent("unknown-off.png"))
        }
        let hiddenHeight = model.cardHeight(for: snapshot)
        model.showUnknownSessions = true
        XCTAssertEqual(model.activity(for: "codex")?.sessions.count, 8)
        XCTAssertEqual(model.activity(for: "codex")?.presentation(cap: 2).hidden, 6)
        XCTAssertGreaterThan(model.cardHeight(for: snapshot), hiddenHeight)
        XCTAssertEqual(model.activity(for: "codex")?.state, .working)
        defaults.set(true, forKey: AppPreferences.notchShowUnknownSessionsKey)
        AppPreferences.registerDefaults(in: defaults)
        XCTAssertTrue(NotchViewModel(defaults: defaults).showUnknownSessions)
        defaults.set(false, forKey: AppPreferences.notchShowUnknownSessionsKey)
        XCTAssertFalse(NotchViewModel(defaults: defaults).showUnknownSessions)
        model.showUnknownSessions = false
        XCTAssertEqual(model.sessions["codex"]?.count, 8, "Filtering must not erase state samples")
        XCTAssertEqual(model.cardHeight(for: snapshot), hiddenHeight)
    }

    func testUnknownOnlyDoesNotCreateActivityOrCompletionWhenHidden() throws {
        let suite = "SessionDisclosureTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let (_, activity) = fixture()
        let model = NotchViewModel(defaults: defaults)
        model.sessions = ["codex": Array(activity.sessions.dropFirst())]
        XCTAssertNil(model.activity(for: "codex"))
        model.showUnknownSessions = true
        XCTAssertEqual(model.activity(for: "codex")?.state, .unavailable)
        model.showUnknownSessions = false
        XCTAssertNil(model.activity(for: "codex"))
        var watcher = SessionCompletionWatcher()
        XCTAssertTrue(watcher.absorb(model.sessions).isEmpty)
        model.sessions = [:]
        XCTAssertTrue(watcher.absorb(model.sessions).isEmpty)
    }

    func testExpansionRevealsEveryCollectedRowAndCanCollapseAgain() {
        let (_, activity) = fixture()
        var list = SessionList(summary: activity, now: now, cap: 2, onToggleExpansion: {})
        XCTAssertEqual(list.presentation.rows.count, 2)
        XCTAssertEqual(list.presentation.hidden, 6)
        list.isExpanded = true
        XCTAssertEqual(list.presentation.rows.count, 8)
        XCTAssertEqual(list.presentation.hidden, 0)
        XCTAssertEqual(Set(list.presentation.rows.map(\.id)), Set(activity.sessions.map(\.id)))
        list.isExpanded = false
        XCTAssertEqual(list.presentation.rows.count, 2)
        XCTAssertEqual(list.presentation.hidden, 6)
    }

    func testExpandedListGrowsUntilScreenBudgetThenScrollsWithoutIndicator() async throws {
        let (snapshot, activity) = fixture()
        let collapsed = TooltipCard(snapshot: snapshot, activity: activity, now: now, sessionCap: 2)
        let unlimited = TooltipCard(snapshot: snapshot, activity: activity, now: now, sessionCap: 2,
                                   showsAllSessions: true)
        let before = try XCTUnwrap(ImageRenderer(content: collapsed).nsImage).size
        let full = try XCTUnwrap(ImageRenderer(content: unlimited).nsImage).size
        XCTAssertGreaterThan(full.height, before.height)
        let expanded = TooltipCard(snapshot: snapshot, activity: activity, now: now, sessionCap: 2,
            maxHeight: before.height, showsAllSessions: true)
        let after = try XCTUnwrap(ImageRenderer(content: expanded).nsImage).size
        XCTAssertEqual(before.width, after.width, accuracy: 1)
        XCTAssertEqual(before.height, after.height, accuracy: 1)
        let fullContent = try XCTUnwrap(ImageRenderer(content:
            expanded.cardContent.frame(width: NotchLayout.cardTextWidth)).nsImage).size
        XCTAssertGreaterThan(fullContent.height, after.height)

        _ = NSApplication.shared
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: after),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.close() }
        let host = NSHostingView(rootView: expanded)
        window.contentView = host
        try await Task.sleep(for: .milliseconds(100))
        host.frame = CGRect(origin: .zero, size: after)
        host.layoutSubtreeIfNeeded()
        func scrollViews(in view: NSView) -> [NSScrollView] {
            (view as? NSScrollView).map { [$0] } ?? view.subviews.flatMap { scrollViews(in: $0) }
        }
        let scroll = try XCTUnwrap(scrollViews(in: host).first)
        XCTAssertFalse(scroll.hasVerticalScroller)
        let document = try XCTUnwrap(scroll.documentView)
        XCTAssertGreaterThan(document.frame.height, scroll.contentView.bounds.height)

        if let directory = ProcessInfo.processInfo.environment["SESSION_DISCLOSURE_RENDER_DIR"] {
            try save(ImageRenderer(content: collapsed.padding(20).background(Color.black)),
                     to: URL(fileURLWithPath: directory).appendingPathComponent("collapsed.png"))
            try save(ImageRenderer(content: expanded.cardContent
                .frame(width: NotchLayout.cardTextWidth).padding(20).background(Color.black)),
                     to: URL(fileURLWithPath: directory).appendingPathComponent("all-rows.png"))
        }
    }


    func testExpandedTasksFitEveryEdgeAndSizeIncludingDraggedOffsets() {
        struct Screen: ScreenDescribing {
            var frameValue = CGRect(x: -1280, y: 200, width: 1280, height: 800)
            var visibleFrameValue = CGRect(x: -1280, y: 220, width: 1280, height: 758)
        }
        let screen = Screen()
        let (snapshot, _) = fixture()
        let sessions = (0..<60).map { index in
            AgentSession(id: "task-\(index)", name: "CodeRim", detail: "Task \(index)",
                state: .busy, waitingFor: nil, since: now,
                codexThreadID: String(format: "00000000-0000-0000-0000-%012d", index))
        }
        let model = NotchViewModel()
        model.snapshots = [snapshot]
        model.sessions = ["codex": sessions]
        model.expandedSessionProviderID = "codex"
        for edge in NotchEdge.allCases {
            for scale: CGFloat in [0.8, 1, 1.25] {
                model.edge = edge
                model.sizeScale = scale
                model.adopt(screen: screen)
                let natural = model.sessionPresentation(for: snapshot, cellCount: 1)
                    .height(snapshot: snapshot, now: now, accountAction: false)
                XCTAssertGreaterThan(natural, model.cardHeight(for: snapshot))
                let size = model.panelSize(cellCount: 1)
                XCTAssertLessThanOrEqual(size.height, screen.frameValue.height,
                    "\(edge) scale \(scale) must fit the screen")
                for offset: CGFloat in [-1000, 0, 1000] {
                    let frame = NotchGeometry.panelFrame(for: screen, panelSize: size, edge: edge,
                        alongOffset: offset, slack: model.slack(cellCount: 1), keepsPanelOnScreen: true)
                    XCTAssertTrue(screen.frameValue.contains(frame),
                        "\(edge) scale \(scale) offset \(offset): \(frame)")
                }
            }
        }
    }

    private func save<Content: View>(_ renderer: ImageRenderer<Content>, to url: URL) throws {
        renderer.scale = 3
        let image = try XCTUnwrap(renderer.nsImage)
        let tiff = try XCTUnwrap(image.tiffRepresentation)
        let png = try XCTUnwrap(NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]))
        try png.write(to: url)
    }
}
