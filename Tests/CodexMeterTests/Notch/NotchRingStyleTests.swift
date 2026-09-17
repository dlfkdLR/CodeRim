import AppKit
import SwiftUI
import XCTest
@testable import CodexMeter

@MainActor
final class NotchRingStyleTests: XCTestCase {
    private func render(used: Double?, mode: NotchRingColorMode, gradient: NotchRingGradient = .aurora,
                        blocked: Bool = false, percentageMode: NotchPercentageMode = .used) throws -> NSBitmapImageRep {
        let renderer = ImageRenderer(content:
            ProviderRing(usedFraction: used, glyph: .openai, isBlocked: blocked, percentageMode: percentageMode)
                .environment(\.notchAccentColor, NotchAccentChoice.blue.color)
                .environment(\.notchRingAppearance, NotchRingAppearance(mode: mode, gradient: gradient))
        )
        renderer.scale = 4
        return NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
    }

    private func color(_ image: NSBitmapImageRep, at turn: Double = 0.04) throws -> NSColor {
        let scale = Double(image.pixelsWide) / Double(NotchLayout.ringDiameter)
        let center = Double(image.pixelsWide) / 2
        let radius = (Double(NotchLayout.ringDiameter - NotchLayout.trackStroke) / 2) * scale
        let angle = turn * 2 * Double.pi
        return try XCTUnwrap(image.colorAt(x: Int(center + sin(angle) * radius),
                                          y: Int(center - cos(angle) * radius))?.usingColorSpace(.deviceRGB))
    }

    private func assertSame(_ a: NSColor, _ b: NSColor, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(a.redComponent, b.redComponent, accuracy: 0.03, file: file, line: line)
        XCTAssertEqual(a.greenComponent, b.greenComponent, accuracy: 0.03, file: file, line: line)
        XCTAssertEqual(a.blueComponent, b.blueComponent, accuracy: 0.03, file: file, line: line)
    }

    func testFixedColorSurvivesWarningThresholdsExhaustionAndRemainingMode() throws {
        let reference = try color(render(used: 0.25, mode: .fixed))
        XCTAssertGreaterThan(reference.blueComponent, reference.redComponent + 0.3)
        for used in [0.5, 0.7, 0.9, 1.0, 1.4] {
            try assertSame(reference, color(render(used: used, mode: .fixed)))
        }
        try assertSame(reference, color(render(used: 0.25, mode: .fixed, blocked: true)))
        try assertSame(reference, color(render(used: 0.9, mode: .fixed, percentageMode: .remaining)))
    }

    func testUsageModeStillChangesColorBasedOnConsumption() throws {
        let low = try color(render(used: 0.25, mode: .usage))
        let watch = try color(render(used: 0.6, mode: .usage))
        let high = try color(render(used: 0.9, mode: .usage))
        XCTAssertGreaterThan(low.blueComponent, low.redComponent + 0.3)
        XCTAssertGreaterThan(watch.greenComponent, watch.blueComponent + 0.3)
        XCTAssertGreaterThan(high.redComponent, high.greenComponent + 0.3)
        try assertSame(high, color(render(used: 0.9, mode: .usage, percentageMode: .remaining)))
    }

    func testEveryGradientHasMultipleColorsAndStaysAnchoredAsUsageChanges() throws {
        for gradient in NotchRingGradient.allCases {
            let low = try render(used: 0.25, mode: .gradient, gradient: gradient)
            let high = try render(used: 0.9, mode: .gradient, gradient: gradient)
            let start = try color(high, at: 0.04)
            let middle = try color(high, at: 0.5)
            let difference = abs(start.redComponent - middle.redComponent)
                + abs(start.greenComponent - middle.greenComponent)
                + abs(start.blueComponent - middle.blueComponent)
            XCTAssertGreaterThan(difference, 0.2, gradient.title)
            try assertSame(color(low), start)
            try assertSame(start, color(render(used: 1, mode: .gradient, gradient: gradient, blocked: true)))
            try assertSame(start, color(render(used: 0.9, mode: .gradient, gradient: gradient, percentageMode: .remaining)))
        }
    }

    func testNoUsageDoesNotInventAColoredArc() throws {
        for mode in NotchRingColorMode.allCases {
            let empty = try color(render(used: nil, mode: mode))
            XCTAssertEqual(empty.redComponent, empty.greenComponent, accuracy: 0.02)
            XCTAssertEqual(empty.greenComponent, empty.blueComponent, accuracy: 0.02)
        }
    }

    func testAppearanceDefaultsPersistenceAndUnknownValues() throws {
        let name = "NotchRingStyleTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        AppPreferences.registerDefaults(in: defaults)
        XCTAssertEqual(NotchRingAppearance.stored(in: defaults), NotchRingAppearance())
        defaults.set("pink", forKey: "notchAccent")
        defaults.set("gradient", forKey: "notchRingColorMode")
        defaults.set("sunset", forKey: "notchRingGradient")
        AppPreferences.registerDefaults(in: defaults)
        let reloaded = try XCTUnwrap(UserDefaults(suiteName: name))
        XCTAssertEqual(NotchRingAppearance.stored(in: reloaded), .init(mode: .gradient, gradient: .sunset))
        defaults.set(true, forKey: "notchAnimateGradient")
        XCTAssertEqual(NotchRingAppearance.stored(in: reloaded), .init(mode: .gradient, gradient: .sunset, animatesGradient: true))
        defaults.set(false, forKey: "notchAnimateGradient")
        defaults.set("fixed", forKey: "notchRingColorMode")
        XCTAssertEqual(NotchRingAppearance.stored(in: reloaded), .init(mode: .fixed, gradient: .sunset))
        XCTAssertEqual(defaults.string(forKey: "notchAccent"), "pink")
        defaults.set("future-style", forKey: "notchRingColorMode")
        defaults.set("future-gradient", forKey: "notchRingGradient")
        XCTAssertEqual(NotchRingAppearance.stored(in: defaults), NotchRingAppearance())
    }

    func testGradientAnimationHonorsOptInVisibilityAndReduceMotion() {
        for mode in NotchRingColorMode.allCases {
            let still = NotchRingAppearance(mode: mode)
            XCTAssertFalse(still.shouldAnimate(reduceMotion: false, isVisible: true))
            let moving = NotchRingAppearance(mode: mode, animatesGradient: true)
            XCTAssertEqual(moving.shouldAnimate(reduceMotion: false, isVisible: true), mode == .gradient)
            XCTAssertFalse(moving.shouldAnimate(reduceMotion: true, isVisible: true))
            XCTAssertFalse(moving.shouldAnimate(reduceMotion: false, isVisible: false))
        }
    }

    func testAnimatedGradientChangesColorsWithoutMovingTheUsageArc() throws {
        func image(at seconds: Double) throws -> NSBitmapImageRep {
            let appearance = NotchRingAppearance(mode: .gradient, animatesGradient: true)
            let rotation = NotchRingAppearance.gradientRotation(at: Date(timeIntervalSinceReferenceDate: seconds))
            let renderer = ImageRenderer(content: ProviderRingProgressArc(
                sweep: 0.6, style: appearance.strokeStyle(band: .watch, accent: .blue), rotation: rotation)
                .rotationEffect(.degrees(-90))
                .frame(width: NotchLayout.ringDiameter, height: NotchLayout.ringDiameter))
            renderer.scale = 4
            return NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        }
        let start = try image(at: 0)
        let later = try image(at: 1.5)
        let repeated = try image(at: 3)
        let a = try color(start), b = try color(later)
        XCTAssertGreaterThan(abs(a.redComponent - b.redComponent) + abs(a.greenComponent - b.greenComponent)
                             + abs(a.blueComponent - b.blueComponent), 0.2)
        try assertSame(a, color(repeated))
        for x in stride(from: 0, to: start.pixelsWide, by: 2) {
            for y in stride(from: 0, to: start.pixelsHigh, by: 2) {
                XCTAssertEqual(start.colorAt(x: x, y: y)?.alphaComponent ?? 0,
                               later.colorAt(x: x, y: y)?.alphaComponent ?? 0, accuracy: 0.02,
                               "Only colours should move; the usage sweep must stay fixed")
            }
        }
    }

    func testEntireGradientPaletteFlowsAndLoopsWithoutAWhiteHighlight() throws {
        for gradient in NotchRingGradient.allCases {
            func image(at seconds: Double) throws -> NSBitmapImageRep {
                let appearance = NotchRingAppearance(mode: .gradient, gradient: gradient)
                let rotation = NotchRingAppearance.gradientRotation(at: Date(timeIntervalSinceReferenceDate: seconds))
                let renderer = ImageRenderer(content: ProviderRingProgressArc(
                    sweep: 1, style: appearance.strokeStyle(band: .watch, accent: .blue), rotation: rotation)
                    .rotationEffect(.degrees(-90))
                    .frame(width: NotchLayout.ringDiameter, height: NotchLayout.ringDiameter))
                renderer.scale = 4
                return NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
            }
            let start = try image(at: 0)
            let quarterTurn = try image(at: 0.75)
            let beforeLoop = try image(at: 2.999)
            for turn in [0.1, 0.4, 0.7] {
                // The whole palette travels a quarter turn, not just a bright point.
                try assertSame(color(start, at: turn), color(quarterTurn, at: turn + 0.25))
                try assertSame(color(start, at: turn), color(beforeLoop, at: turn))
            }
            for frame in [start, quarterTurn] {
                for turn in stride(from: 0.0, to: 1.0, by: 0.025) {
                    let sample = try color(frame, at: turn)
                    XCTAssertLessThan(min(sample.redComponent, sample.greenComponent, sample.blueComponent), 0.85,
                                      "The chosen palette must not acquire a white highlight")
                }
            }
        }
    }

    func testLiveRingTimelineMovesAndStopsWhenTurnedOff() throws {
        try XCTSkipIf(NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
                      "Live motion is intentionally paused by the system accessibility setting")
        _ = NSApplication.shared
        func content(animated: Bool) -> AnyView {
            AnyView(ProviderRing(usedFraction: 0.6, glyph: .openai)
                .environment(\.notchRingAppearance,
                             NotchRingAppearance(mode: .gradient, gradient: .aurora, animatesGradient: animated))
                .padding(10)
                .background(.black))
        }
        let host = NSHostingView(rootView: content(animated: true))
        let size = NSSize(width: 64, height: 64)
        let window = NSWindow(contentRect: NSRect(x: 40, y: 40, width: 64, height: 64),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        host.frame = NSRect(origin: .zero, size: size)
        window.orderFront(nil)
        defer { window.orderOut(nil); window.contentView = nil }
        func capture() throws -> NSBitmapImageRep {
            host.layoutSubtreeIfNeeded()
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            return bitmap
        }
        func changedPixels(_ a: NSBitmapImageRep, _ b: NSBitmapImageRep) -> Int {
            var changed = 0
            for x in stride(from: 0, to: a.pixelsWide, by: 2) {
                for y in stride(from: 0, to: a.pixelsHigh, by: 2) {
                    guard let p = a.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB),
                          let q = b.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB) else { continue }
                    if abs(p.redComponent - q.redComponent) + abs(p.greenComponent - q.greenComponent)
                        + abs(p.blueComponent - q.blueComponent) > 0.12 { changed += 1 }
                }
            }
            return changed
        }
        RunLoop.current.run(until: Date().addingTimeInterval(0.3))
        let movingA = try capture()
        RunLoop.current.run(until: Date().addingTimeInterval(0.6))
        let movingB = try capture()
        XCTAssertGreaterThan(changedPixels(movingA, movingB), 40,
                             "The real ProviderRing TimelineView must visibly change, not only its phase helper")
        host.rootView = content(animated: false)
        RunLoop.current.run(until: Date().addingTimeInterval(0.3))
        let stillA = try capture()
        RunLoop.current.run(until: Date().addingTimeInterval(0.6))
        let stillB = try capture()
        XCTAssertLessThan(changedPixels(stillA, stillB), 3, "Turning animation off must stop the real ring")
        if let path = ProcessInfo.processInfo.environment["CODEXMETER_RING_CAPTURE_DIR"] {
            let dir = URL(fileURLWithPath: path, isDirectory: true)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            for (name, bitmap) in [("live-moving-a", movingA), ("live-moving-b", movingB),
                                   ("live-still-a", stillA), ("live-still-b", stillB)] {
                try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                    .write(to: dir.appendingPathComponent("\(name).png"))
            }
        }
    }

    func testSettingsModesFitAndRenderInBothAppearances() throws {
        _ = NSApplication.shared
        let name = "NotchRingSettingsTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        AppPreferences.registerDefaults(in: defaults)
        defaults.set("blue", forKey: "notchAccent")
        let size = NSSize(width: 579, height: 1000)
        for dark in [false, true] {
            for mode in NotchRingColorMode.allCases {
                defaults.set(mode.rawValue, forKey: "notchRingColorMode")
                let host = NSHostingView(rootView: NotchSettingsView()
                    .defaultAppStorage(defaults)
                    .environment(\.colorScheme, dark ? .dark : .light)
                    .frame(width: size.width, height: size.height))
                host.sizingOptions = []
                let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                                      styleMask: [.borderless], backing: .buffered, defer: false)
                window.contentView = host
                window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                host.frame = NSRect(origin: .zero, size: size)
                window.orderFront(nil)
                for _ in 0..<6 { host.layoutSubtreeIfNeeded() }
                // Let SwiftUI finish its initial display pass before capturing
                // the preview; an immediate cacheDisplay can capture empty ink.
                RunLoop.current.run(until: Date().addingTimeInterval(0.15))
                host.layoutSubtreeIfNeeded()
                host.displayIfNeeded()
                func checkWidths(_ view: NSView) {
                    if let scroll = view as? NSScrollView {
                        XCTAssertLessThanOrEqual(scroll.documentView?.bounds.width ?? 0, scroll.contentSize.width + 1)
                    }
                    view.subviews.forEach(checkWidths)
                }
                checkWidths(host)
                if let path = ProcessInfo.processInfo.environment["CODEXMETER_RING_CAPTURE_DIR"] {
                    let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                    host.cacheDisplay(in: host.bounds, to: bitmap)
                    let directory = URL(fileURLWithPath: path, isDirectory: true)
                    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                    try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                        .write(to: directory.appendingPathComponent("settings-\(mode.rawValue)-\(dark ? "dark" : "light").png"))
                }
                window.orderOut(nil)
                window.contentView = nil
            }
        }
    }
}
