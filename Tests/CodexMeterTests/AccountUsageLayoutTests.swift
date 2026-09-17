import AppKit
import SwiftUI
import Vision
import XCTest
@testable import CodexMeter

@MainActor
final class AccountUsageLayoutTests: XCTestCase {
    func testOnlyLiveLocalTotalsAppearEvenWithSavedAccountSyncAndAccountSwitch() async throws {
        _ = NSApplication.shared
        let suite = "CodexMeter.AccountUsageLayout.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(true, forKey: "profileSyncEnabled")
        defaults.set("detailed", forKey: "numberStyle")
        defaults.set(false, forKey: "costEstimatesEnabled")
        let now = Date()
        let local = UsageSnapshot(today: usage(120), week: usage(240), month: usage(480), allTime: usage(960),
                                  quality: .exact, updatedAt: now)
        let profile = ProfileUsageStore(defaults: defaults) { _, _, _ in
            ProfileUsageSnapshot(today: 0, week: 500, month: 1_000, lifetime: 2_000,
                                 statsAsOf: now.addingTimeInterval(-86_400), generatedAt: now)
        }
        let store = UsageStore(initialSnapshot: local, automaticallyRefresh: false, defaults: defaults)
        let limits = AccountLimitStore(defaults: defaults, pollingInterval: nil)
        let claude = ClaudeIntegrationStore(defaults: defaults, automaticallyRefresh: false)

        for width: CGFloat in [372, 579, 920] {
            for dark in [false, true] {
                await profile.refresh()
                let embedded = width != 372
                let navigation = MenuNavigation()
                let content = embedded
                    ? AnyView(ScrollView { UsageSettingsOverview() }.frame(width: width, height: 780))
                    : AnyView(MenuPopoverView(accounts: AccountLayoutFixture.emptyStore(), navigation: navigation))
                let host = NSHostingView(rootView: content
                    .background(.background)
                    .environmentObject(navigation)
                    .environmentObject(store)
                    .environmentObject(profile)
                    .environmentObject(limits)
                    .environmentObject(claude)
                    .defaultAppStorage(defaults)
                    .environment(\.colorScheme, dark ? .dark : .light))
                if embedded { host.sizingOptions = [] }
                let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: width, height: 780),
                                      styleMask: [.borderless], backing: .buffered, defer: false)
                window.isReleasedWhenClosed = false
                window.contentView = host
                window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                defer { window.contentView = nil; window.close() }
                await layout(host, in: window, fixedSize: embedded ? NSSize(width: width, height: 780) : nil)
                XCTAssertEqual(host.bounds.width, width)
                XCTAssertLessThanOrEqual(host.bounds.height, embedded ? 780 : 740)
                let bitmap = try render(host)
                try capture(bitmap, name: "account-usage-\(Int(width))-\(dark ? "dark" : "light")")
                // SwiftUI's custom buttons are not consistently exposed as
                // NSButtons/AX children in unshown windows. Verify actual pixels.
                let text = try recognizedText(in: bitmap)
                for expected in ["This Mac", "Local History", "240", "480", "960"] {
                    XCTAssertTrue(text.contains(normalized(expected)), "Missing \(expected): \(text)")
                }

                for excluded in ["ChatGPT account", "Through", "Lifetime"] {
                    XCTAssertFalse(text.contains(normalized(excluded)), "Unexpected account total: \(text)")
                }
                XCTAssertEqual(text.components(separatedBy: "thisweek").count - 1, 1, text)
                XCTAssertEqual(text.components(separatedBy: "thismonth").count - 1, 1, text)

                profile.clearForAccountSwitch()
                await layout(host, in: window, fixedSize: embedded ? NSSize(width: width, height: 780) : nil)
                let afterSwitch = try recognizedText(in: render(host))
                for expected in ["Local History", "240", "480", "960"] {
                    XCTAssertTrue(afterSwitch.contains(normalized(expected)), afterSwitch)
                }
                XCTAssertFalse(afterSwitch.contains("lifetime"))
            }
        }
    }

    private func usage(_ total: Int64) -> TokenUsage {
        TokenUsage(inputTokens: total, cachedInputTokens: 0, outputTokens: 0)
    }

    private func layout(_ host: NSView, in window: NSWindow, fixedSize: NSSize?) async {
        for _ in 0..<8 {
            let size = fixedSize ?? host.fittingSize
            window.setContentSize(size)
            host.setFrameSize(size)
            host.layoutSubtreeIfNeeded()
            await Task.yield()
        }
    }

    private func render(_ host: NSView) throws -> NSBitmapImageRep {
        let size = host.bounds.size
        let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil,
            pixelsWide: Int(size.width * 2), pixelsHigh: Int(size.height * 2),
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0))
        bitmap.size = size
        host.cacheDisplay(in: host.bounds, to: bitmap)
        return bitmap
    }

    private func recognizedText(in bitmap: NSBitmapImageRep) throws -> String {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false
        request.recognitionLanguages = ["en-US"]
        try VNImageRequestHandler(cgImage: XCTUnwrap(bitmap.cgImage), options: [:]).perform([request])
        return normalized((request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: " "))
    }

    private func normalized(_ text: String) -> String {
        text.lowercased().filter { $0.isLetter || $0.isNumber }
    }

    private func capture(_ bitmap: NSBitmapImageRep, name: String) throws {
        guard let directory = ProcessInfo.processInfo.environment["CODEXMETER_ACCOUNT_LAYOUT_DIR"] else { return }
        let url = URL(fileURLWithPath: directory, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            .write(to: url.appendingPathComponent("\(name).png"))
    }
}
