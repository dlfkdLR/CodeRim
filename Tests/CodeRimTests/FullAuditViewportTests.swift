import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class FullAuditViewportTests: XCTestCase {
    private func fixture(id: String, windows: [LimitWindow] = []) -> ProviderSnapshot {
        ProviderSnapshot(id: id, displayName: id, glyph: .openai, fidelity: .official, status: .ok, windows: windows)
    }
    private struct Screen: ScreenDescribing {
        let frameValue: CGRect
        var visibleFrameValue: CGRect { frameValue.insetBy(dx: 0, dy: 25) }
        var hardwareNotch: HardwareNotch?
    }

    func testAllProvidersRemainReachableOnEveryEdgeAndScale() {
        for size in [CGSize(width: 1280, height: 720), CGSize(width: 1440, height: 900)] {
            for edge in NotchEdge.allCases {
                for scale in [0.8, 1.0, 1.25] {
                    for count in [0, 1, 10, 70] {
                        let model = NotchViewModel()
                        model.edge = edge
                        model.sizeScale = scale
                        model.isExpanded = true
                        model.snapshots = (0..<count).map { fixture(id: "provider-\($0)") }
                        let screen = Screen(frameValue: CGRect(origin: CGPoint(x: -1440, y: 200), size: size),
                            hardwareNotch: edge == .top ? HardwareNotch(width: 220, height: 32) : nil)
                        model.adopt(screen: screen)
                        var reached: [String] = []
                        repeat {
                            reached += model.visibleSnapshots.map(\.id)
                            XCTAssertLessThanOrEqual(model.panelSize.width, size.width)
                            XCTAssertLessThanOrEqual(model.panelSize.height, size.height)
                            for offset: CGFloat in [-10000, 0, 10000] {
                                let frame = NotchGeometry.panelFrame(for: screen, panelSize: model.panelSize,
                                    edge: edge, alongOffset: offset, slack: model.slack,
                                    trailingExtent: model.trailingExtent * scale,
                                    leadingExtent: model.leadingExtent * scale, keepsPanelOnScreen: true)
                                XCTAssertTrue(screen.frameValue.contains(frame), "\(edge), \(scale), \(count): \(frame)")
                            }
                            for (index, snapshot) in model.visibleSnapshots.enumerated() {
                                model.hoveredIndex = index
                                XCTAssertEqual(model.hoveredSnapshot?.id, snapshot.id)
                                XCTAssertEqual(model.providerID(atVisibleIndex: index), snapshot.id)
                            }
                            guard model.canShowNextPage else { break }
                            model.changePage(forward: true)
                            XCTAssertNil(model.hoveredIndex)
                        } while true
                        XCTAssertEqual(reached, model.snapshots.map(\.id))
                        while model.canShowPreviousPage { model.changePage(forward: false) }
                        XCTAssertEqual(model.pageStart, 0)
                        XCTAssertEqual(model.snapshots.count, count)
                    }
                }
            }
        }
    }

    func testLongOllamaReadingGetsAScrollViewportWithoutDiscardingRows() throws {
        let payload: [String: Any] = ["limits": ["monthly": ["usage": 0.5,
            "models": (0..<50).map { ["name": "model-\($0)", "request_count": 1] as [String: Any] }]]]
        let usage = try OllamaUsage.parse(String(decoding: JSONSerialization.data(withJSONObject: payload), as: UTF8.self))
        let snapshot = fixture(id: "ollama", windows: usage.windows)
        for edge in NotchEdge.allCases {
            let model = NotchViewModel()
            model.edge = edge
            model.isExpanded = true
            model.snapshots = [snapshot] + (1..<10).map { fixture(id: "other-\($0)") }
            model.adopt(screen: Screen(frameValue: CGRect(x: 0, y: 0, width: 1280, height: 720)))
            let natural = NotchLayout.cardHeight(for: snapshot, now: model.now)
            let bounded = model.cardHeight(for: snapshot)
            XCTAssertGreaterThan(natural, bounded)
            XCTAssertGreaterThan(bounded, 150)
            XCTAssertEqual(model.snapshots[0].windows.count, 51)
            XCTAssertLessThanOrEqual(model.panelSize.height, 720)
            let card = TooltipCard(snapshot: snapshot, now: model.now,
                                   direction: edge.tooltipDirection, maxHeight: bounded)
            let host = NSHostingView(rootView: card)
            let extra = edge.isVertical ? 0 : NotchLayout.tailLength
            XCTAssertLessThanOrEqual(host.fittingSize.height, bounded + extra + 1)
            model.snapshots[0] = fixture(id: "ollama")
            XCTAssertLessThan(model.cardHeight(for: model.snapshots[0]), bounded)
        }
    }

    func testChangingPageProviderOrderScaleOrDisplayCannotKeepAnOldHover() {
        let model = NotchViewModel()
        model.snapshots = (0..<70).map { fixture(id: "provider-\($0)") }
        model.adopt(screen: Screen(frameValue: CGRect(x: 0, y: 0, width: 1280, height: 720)))
        model.hoveredIndex = 0
        model.changePage(forward: true)
        XCTAssertNil(model.hoveredIndex)
        XCTAssertNotEqual(model.providerID(atVisibleIndex: 0), "provider-0")
        model.hoveredIndex = 0
        model.snapshots.reverse()
        XCTAssertNil(model.hoveredIndex)
        model.hoveredIndex = 0
        model.sizeScale = 1.25
        XCTAssertNil(model.hoveredIndex)
        XCTAssertEqual(model.pageStart, 0)
        model.changePage(forward: true)
        model.hoveredIndex = 0
        model.adopt(screen: Screen(frameValue: CGRect(x: 0, y: 0, width: 1440, height: 900)))
        XCTAssertNil(model.hoveredIndex)
        XCTAssertEqual(model.pageStart, 0)
    }
}
