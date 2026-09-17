import Foundation
import XCTest
@testable import CodeRim

final class TokenFormatterTests: XCTestCase {
    private let formatter = TokenFormatter()
    private let locale = Locale(identifier: "en_US")

    func testCompactBoundaries() {
        XCTAssertEqual(formatter.string(from: 0, style: .compact, locale: locale), "0")
        XCTAssertEqual(formatter.string(from: 999, style: .compact, locale: locale), "999")
        XCTAssertEqual(formatter.string(from: 1_000, style: .compact, locale: locale), "1K")
        XCTAssertEqual(formatter.string(from: 1_200, style: .compact, locale: locale), "1.2K")
        XCTAssertEqual(formatter.string(from: 999_999, style: .compact, locale: locale), "1M")
        XCTAssertEqual(formatter.string(from: 1_000_000, style: .compact, locale: locale), "1M")
        XCTAssertEqual(formatter.string(from: 15_244_263, style: .compact, locale: locale), "15.2M")
        XCTAssertEqual(formatter.string(from: 1_000_000_000, style: .compact, locale: locale), "1B")
    }

    func testDetailedFormatting() {
        XCTAssertEqual(formatter.string(from: 15_244_263, style: .detailed, locale: locale), "15,244,263")
    }
}
