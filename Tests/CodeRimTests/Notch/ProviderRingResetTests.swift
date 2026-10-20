import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class ProviderRingResetTests: XCTestCase {
    func testUsedResetRetractsTheLiveArcFromItsEndForFixedAndMovingGradient() throws {
        try requireMotion()
        for appearance in resetAppearances {
            let fixture = ResetRingHost(usedFraction: 0.9, appearance: appearance)
            defer { fixture.close() }
            fixture.pump(0.18)
            let name = appearance.mode.rawValue
            let before = try fixture.capture("reset-\(name)-before")
            XCTAssertEqual(before.coverage, 0.9, accuracy: 0.035)

            fixture.reading.usedFraction = 0
            let frames = try captureMotion(fixture, prefix: "reset-\(name)")
            let middle = try XCTUnwrap(frames.first { (0.18...0.74).contains($0.coverage) },
                                      "A real intermediate arc must appear before zero, including inside TimelineView")
            assertAnchoredArc(middle)
            assertRetraction(frames, initial: before.coverage)

            fixture.pump(0.65)
            let empty = try fixture.capture("reset-\(name)-empty")
            XCTAssertEqual(empty.coloredCount, 0, "A zero reading must leave no coloured round-cap dot at 12 o'clock")
        }
    }

    func testMissingReadingRetractsBeforeTheArcDisappears() throws {
        try requireMotion()
        let fixture = ResetRingHost(usedFraction: 0.9, appearance: .init(mode: .fixed))
        defer { fixture.close() }
        fixture.pump(0.18)
        let before = try fixture.capture("reset-missing-before")
        fixture.reading.usedFraction = nil
        let frames = try captureMotion(fixture, prefix: "reset-missing")
        let middle = try XCTUnwrap(frames.first { (0.18...0.74).contains($0.coverage) },
                                  "A missing reading must retract the existing arc, rather than remove its view immediately")
        assertAnchoredArc(middle)
        assertRetraction(frames, initial: before.coverage)
        fixture.pump(0.65)
        XCTAssertEqual(try fixture.capture("reset-missing-empty").coloredCount, 0)
    }

    func testReduceMotionAppliesZeroAndMissingReadingWithoutSweeping() throws {
        try XCTSkipUnless(NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
                          "Live Reduce Motion requires the real system setting; this test never changes accessibility preferences")
        let targets: [Double?] = [0, nil]
        for target in targets {
            let fixture = ResetRingHost(usedFraction: 0.9,
                                        appearance: .init(mode: .gradient, animatesGradient: true))
            defer { fixture.close() }
            fixture.pump(0.18)
            XCTAssertEqual(try fixture.capture().coverage, 0.9, accuracy: 0.035)
            fixture.reading.usedFraction = target
            fixture.pump(0.06)
            XCTAssertEqual(try fixture.capture("reset-reduce-motion-\(target == nil ? "missing" : "zero")").coloredCount, 0,
                           "Reduce Motion must apply the final arc on the next display pass")
        }
    }

    func testRemainingModeGrowsToFullAllowanceWhenUsageResets() throws {
        try requireMotion()
        let fixture = ResetRingHost(usedFraction: 0.9, appearance: .init(mode: .fixed), percentageMode: .remaining)
        defer { fixture.close() }
        fixture.pump(0.18)
        XCTAssertEqual(try fixture.capture("reset-remaining-before").coverage, 0.1, accuracy: 0.035)
        fixture.reading.usedFraction = 0
        let frames = try captureMotion(fixture, prefix: "reset-remaining")
        let middle = try XCTUnwrap(frames.first { (0.25...0.8).contains($0.coverage) },
                                  "The remaining allowance must grow smoothly when consumption resets")
        assertAnchoredArc(middle)
        fixture.pump(0.65)
        XCTAssertGreaterThan(try fixture.capture("reset-remaining-full").coverage, 0.98,
                             "Zero used means a full ring in Remaining mode")
    }

    private var resetAppearances: [NotchRingAppearance] {
        [.init(mode: .fixed), .init(mode: .gradient, gradient: .aurora, animatesGradient: true)]
    }

    private func requireMotion() throws {
        try XCTSkipIf(NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
                      "System Reduce Motion intentionally suppresses the live sweep")
    }

    private func captureMotion(_ fixture: ResetRingHost, prefix: String) throws -> [ResetRingFrame] {
        var frames: [ResetRingFrame] = []
        // Several displayed frames avoid tying correctness to one exact refresh.
        for index in 0..<10 {
            fixture.pump(0.055)
            frames.append(try fixture.capture("\(prefix)-frame-\(index)"))
        }
        return frames
    }

    private func assertRetraction(_ frames: [ResetRingFrame], initial: Double,
                                  file: StaticString = #filePath, line: UInt = #line) {
        var previous = initial
        for frame in frames {
            XCTAssertLessThanOrEqual(frame.coverage, previous + 0.025,
                                     "The trailing end must only retract, without a spring rebound", file: file, line: line)
            previous = frame.coverage
        }
        XCTAssertLessThan(previous, initial - 0.4, file: file, line: line)
    }

    private func assertAnchoredArc(_ frame: ResetRingFrame,
                                   file: StaticString = #filePath, line: UInt = #line) {
        // Ignore the round caps. A true trim leaves a solid prefix clockwise
        // from 12 o'clock, and an empty suffix, unlike opacity or rotation.
        let prefix = frame.samples(in: 0.035...(frame.coverage - 0.04))
        let suffix = frame.samples(in: (frame.coverage + 0.04)...0.94)
        XCTAssertFalse(prefix.isEmpty, file: file, line: line)
        XCTAssertFalse(suffix.isEmpty, file: file, line: line)
        XCTAssertGreaterThan(Double(prefix.filter { $0 }.count) / Double(max(1, prefix.count)), 0.95,
                             "The 12 o'clock origin and the beginning of the coloured arc must stay in place", file: file, line: line)
        XCTAssertEqual(suffix.filter { $0 }.count, 0,
                       "Only the trailing end should retreat; the whole arc must not fade or rotate", file: file, line: line)
    }
}

@MainActor
private final class ResetRingReading: ObservableObject {
    @Published var usedFraction: Double?
    init(_ usedFraction: Double?) { self.usedFraction = usedFraction }
}

private struct ResetRingFixture: View {
    @ObservedObject var reading: ResetRingReading
    let appearance: NotchRingAppearance
    let percentageMode: NotchPercentageMode
    let scale: CGFloat
    let side: CGFloat

    var body: some View {
        ProviderRing(usedFraction: reading.usedFraction, glyph: .openai, percentageMode: percentageMode)
            .environment(\.notchAccentColor, NotchAccentChoice.blue.color)
            .environment(\.notchRingAppearance, appearance)
            .environment(\.notchRingAnimationEnabled, true)
            .scaleEffect(scale)
            .frame(width: side, height: side)
            .background(.black)
    }
}

@MainActor
private final class ResetRingHost {
    let reading: ResetRingReading
    private let host: NSHostingView<ResetRingFixture>
    private let window: NSWindow
    private let scale: CGFloat = 4

    init(usedFraction: Double?, appearance: NotchRingAppearance,
         percentageMode: NotchPercentageMode = .used) {
        _ = NSApplication.shared
        let model = ResetRingReading(usedFraction)
        reading = model
        let displayScale: CGFloat = 4
        let side = NotchLayout.ringDiameter * displayScale + 32
        host = NSHostingView(rootView: ResetRingFixture(reading: model, appearance: appearance,
                                                        percentageMode: percentageMode,
                                                        scale: displayScale, side: side))
        let rect = NSRect(x: 70, y: 70, width: side, height: side)
        window = NSWindow(contentRect: rect, styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        host.frame = NSRect(origin: .zero, size: rect.size)
        window.orderFrontRegardless()
    }

    func close() {
        window.orderOut(nil)
        window.contentView = nil
        window.close()
    }

    func pump(_ seconds: TimeInterval) {
        RunLoop.current.run(until: Date().addingTimeInterval(seconds))
        host.layoutSubtreeIfNeeded()
        host.displayIfNeeded()
    }

    func capture(_ name: String? = nil) throws -> ResetRingFrame {
        host.layoutSubtreeIfNeeded()
        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: bitmap)
        let pixelsPerPoint = CGFloat(bitmap.pixelsWide) / host.bounds.width
        let centerX = CGFloat(bitmap.pixelsWide) / 2
        let centerY = CGFloat(bitmap.pixelsHigh) / 2
        let radius = (NotchLayout.ringDiameter - NotchLayout.trackStroke) / 2 * scale * pixelsPerPoint
        var samples: [Bool] = []
        for index in 0..<180 {
            let turn = (Double(index) + 0.5) / 180
            let angle = turn * 2 * Double.pi
            let x = Int(centerX + CGFloat(sin(angle)) * radius)
            let y = Int(centerY - CGFloat(cos(angle)) * radius)
            let color = try XCTUnwrap(bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB))
            let components = [color.redComponent, color.greenComponent, color.blueComponent]
            let high = components.max() ?? 0
            let low = components.min() ?? 0
            // Both the track and glyph are neutral. Every selected palette
            // colour retains substantial chroma while rotating around the ring.
            samples.append(high > 0.25 && high - low > 0.14)
        }
        if let name, let path = ProcessInfo.processInfo.environment["CODERIM_RING_CAPTURE_DIR"] {
            let directory = URL(fileURLWithPath: path, isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: directory.appendingPathComponent("\(name).png"))
        }
        return ResetRingFrame(coloredSamples: samples)
    }
}

private struct ResetRingFrame {
    let coloredSamples: [Bool]
    var coloredCount: Int { coloredSamples.filter { $0 }.count }
    var coverage: Double { Double(coloredCount) / Double(coloredSamples.count) }

    func samples(in turns: ClosedRange<Double>) -> [Bool] {
        coloredSamples.enumerated().compactMap { index, colored in
            let turn = (Double(index) + 0.5) / Double(coloredSamples.count)
            return turns.contains(turn) ? colored : nil
        }
    }
}
