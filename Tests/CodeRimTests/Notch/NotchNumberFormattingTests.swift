import XCTest
import AppKit
import SwiftUI
@testable import CodeRim

final class NotchNumberFormattingTests: XCTestCase {
    @MainActor
    func testTooltipObservesTheSharedNumberFormatPreference() throws {
        let suite = "CodeRim.NumberStyle.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let snapshot = ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
            fidelity: .official, status: .ok, windows: [LimitWindow(id: "w", label: "Weekly", usedFraction: 0.6)],
            todaysTokens: 12_556_351)
        func render(_ style: TokenNumberStyle) throws -> Data {
            defaults.set(style.rawValue, forKey: "numberStyle")
            let renderer = ImageRenderer(content: TooltipCard(snapshot: snapshot, now: Date(timeIntervalSince1970: 1_800_000_000))
                .defaultAppStorage(defaults))
            let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
            return try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
        }
        XCTAssertNotEqual(try render(.compact), try render(.detailed),
                          "The actual tooltip must observe General's shared preference")
    }

    func testTokenAndCountReadingsFollowBothFormatsWithoutChangingPercentages() {
        let locale = Locale(identifier: "en_US")
        XCTAssertEqual(NotchNumberFormatting.count(12_556_351, style: .compact, locale: locale), "12.6M")
        XCTAssertEqual(NotchNumberFormatting.count(12_556_351, style: .detailed, locale: locale), "12,556,351")
        let requests = LimitWindow(id: "r", label: "Requests", remaining: 12_500)
        XCTAssertEqual(NotchNumberFormatting.summary(requests, style: .compact, locale: locale), "12.5K left")
        XCTAssertEqual(NotchNumberFormatting.summary(requests, style: .detailed, locale: locale), "12,500 left")
        let quota = LimitWindow(id: "q", label: "Quota", usedFraction: 0.59)
        XCTAssertEqual(NotchNumberFormatting.summary(quota, style: .compact),
                       NotchNumberFormatting.summary(quota, style: .detailed))
    }
}
