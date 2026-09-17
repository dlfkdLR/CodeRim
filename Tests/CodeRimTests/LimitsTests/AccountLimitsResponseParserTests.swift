import Foundation
import XCTest
@testable import CodeRim

final class AccountLimitsResponseParserTests: XCTestCase {
    private let parser = AccountLimitsResponseParser()

    func testParsesEveryReportedWindowWithoutInventingPrimarySemantics() throws {
        let response = #"{"id":2,"result":{"rateLimitsByLimitId":{"codex":{"limitName":"Codex","primary":{"usedPercent":14,"windowDurationMins":10080,"resetsAt":1788466804},"secondary":null},"codex_bengalfox":{"limitName":"GPT-5.3-Codex-Spark","primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":1788000000},"secondary":{"usedPercent":1.5,"windowDurationMins":10080,"resetsAt":1788466804}}},"rateLimitResetCredits":{"availableCount":1,"unlimited":false,"expiresAt":1790000000}}}"#

        let snapshot = try parser.parse(Data((response + "\n").utf8), fetchedAt: Date(timeIntervalSince1970: 1))

        XCTAssertEqual(snapshot.windows.count, 3)
        let codex = try XCTUnwrap(snapshot.windows.first(where: { $0.limitID == "codex" }))
        XCTAssertEqual(codex.windowLabel, "Weekly")
        XCTAssertEqual(codex.remainingPercent, 86)
        XCTAssertFalse(snapshot.windows.contains(where: {
            $0.limitID == "codex" && $0.windowDurationMinutes == 300
        }))
        XCTAssertEqual(snapshot.resetCredits?.availableCount, 1)
        XCTAssertEqual(snapshot.fetchedAt, Date(timeIntervalSince1970: 1))
    }

    func testFallsBackToLegacyRateLimitsAndDeduplicatesEquivalentWindows() throws {
        let response = #"{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":100},"secondary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":100}}}}"#
        let snapshot = try parser.parse(Data(response.utf8))

        XCTAssertEqual(snapshot.windows.count, 1)
        XCTAssertEqual(snapshot.windows.first?.displayName, "Codex")
        XCTAssertEqual(snapshot.windows.first?.remainingPercent, 75)
    }

    func testIgnoresNotificationsAndUsesResponseIDTwo() throws {
        let lines = [
            #"{"method":"account/rateLimits/updated","params":{"rateLimits":{}}}"#,
            #"{"id":1,"result":{"userAgent":"test"}}"#,
            #"{"id":2,"result":{"rateLimitsByLimitId":{}}}"#
        ].joined(separator: "\n")

        let snapshot = try parser.parse(Data(lines.utf8))
        XCTAssertTrue(snapshot.windows.isEmpty)
    }

    func testRemainingPercentUnknownLimitAndMissingResetAreHandledGenerically() throws {
        let response = #"{"id":2,"result":{"rateLimitsByLimitId":{"future_limit":{"primary":{"remainingPercent":100,"windowDurationMins":300},"secondary":{"usedPercent":100,"windowDurationMins":10080}}}}}"#
        let snapshot = try parser.parse(Data(response.utf8))

        XCTAssertEqual(snapshot.windows.count, 2)
        XCTAssertEqual(snapshot.windows.map(\.displayName), ["Future Limit", "Future Limit"])
        XCTAssertEqual(snapshot.windows.first?.remainingPercent, 100)
        XCTAssertEqual(snapshot.windows.last?.remainingPercent, 0)
        XCTAssertTrue(snapshot.windows.allSatisfy { $0.resetsAt == nil })
    }

    func testMalformedWindowDoesNotDiscardOtherWindows() throws {
        let response = #"{"id":2,"result":{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":"changed","windowDurationMins":300},"secondary":{"usedPercent":50,"windowDurationMins":10080}}}}}"#
        let snapshot = try parser.parse(Data(response.utf8))

        XCTAssertEqual(snapshot.windows.count, 1)
        XCTAssertEqual(snapshot.windows.first?.windowLabel, "Weekly")
        XCTAssertEqual(snapshot.windows.first?.remainingPercent, 50)
    }

    func testServerErrorAndMalformedResponseFailClosed() {
        let serverError = #"{"id":2,"error":{"message":"not signed in"}}"#
        XCTAssertThrowsError(try parser.parse(Data(serverError.utf8))) { error in
            XCTAssertEqual(error as? AccountLimitError, .server("not signed in"))
        }
        XCTAssertThrowsError(try parser.parse(Data(#"{"id":1,"result":{}}"#.utf8))) { error in
            XCTAssertEqual(error as? AccountLimitError, .malformedResponse)
        }
    }
}
