import Foundation
import XCTest
import CodexBarCore
@testable import CodeRim

final class SharedScriptProviderTests: XCTestCase {
    func testXaiUnavailableAndTruncatedHistoryKeepHonestAmounts() async throws {
        for (status, body, expected, derived) in [
            (503, "{}", "Unavailable", true),
            (401, "{}", "Unavailable", true),
            (401, "<html>sign in</html>", "Unavailable", true),
            (200, #"{"timeSeries":[]}"#, "$0.00", false),
            (200, #"{"timeSeries":[],"limitReached":true}"#, "$0.00", true)
        ] {
            let transport = FixtureTransport { request in
                request.url!.path.hasSuffix("balance")
                    ? (200, #"{"total":{"val":"-1000"}}"#) : (status, body)
            }
            let result = try await SharedScriptProvider.fetch(.xai,
                environment: ["XAI_MANAGEMENT_API_KEY": "synthetic", "XAI_TEAM_ID": "fixture"], transport: transport)
            let rows = result.usage.details.flatMap(\.rows)
            XCTAssertEqual(rows.first(where: { $0.label.hasPrefix("Last 30 days") })?.value, expected)
            XCTAssertEqual(result.usage.dataConfidence == .estimated, derived)
            XCTAssertEqual(rows.first(where: { $0.label == "Prepaid balance" })?.value, "$10.00")
        }
    }

    func testPoeUsesTheSamePartialHistoryReaderAsWindows() async throws {
        let timestamp = Date().timeIntervalSince1970
        let transport = FixtureTransport { request in
            if request.url!.path.hasSuffix("current_balance") { return (200, #"{"current_point_balance":100}"#) }
            return (200, "{\"data\":[{\"query_id\":\"same\",\"creation_time\":\(timestamp),\"cost_points\":10}],\"has_more\":true,\"next_cursor\":\"same\"}")
        }
        let result = try await SharedScriptProvider.fetch(.poe,
            environment: ["POE_API_KEY": "synthetic"], transport: transport)
        XCTAssertEqual(result.usage.dataConfidence, .estimated)
        let row = try XCTUnwrap(result.usage.details.flatMap(\.rows).first(where: { $0.label == "Last 30 days (partial)" }))
        XCTAssertEqual(row.value, "10 points")
        XCTAssertEqual(row.secondaryValue, "1 requests")
    }

    func testRequiredAuthenticationFailurePrecedesBodyParsing() async throws {
        for provider in [CodexBarCore.UsageProvider.xai, .poe] {
            for status in [401, 403] {
                for body in ["{}", "", "<html>sign in</html>"] {
                    do {
                        _ = try await SharedScriptProvider.fetch(provider,
                            environment: ["XAI_MANAGEMENT_API_KEY": "synthetic", "XAI_TEAM_ID": "fixture", "POE_API_KEY": "synthetic"],
                            transport: FixtureTransport { _ in (status, body) })
                        XCTFail("Authentication failure was accepted")
                    } catch let error as ProviderFetchClassifiedError {
                        XCTAssertEqual(error.kind, .authenticationExpired)
                    }
                }
            }
        }
    }

    private struct FixtureTransport: ProviderHTTPTransport {
        let reply: @Sendable (URLRequest) -> (Int, String)
        func data(for request: URLRequest) async throws -> (Data, URLResponse) {
            let (status, body) = reply(request)
            return (Data(body.utf8), HTTPURLResponse(url: request.url!, statusCode: status,
                                                    httpVersion: "HTTP/1.1", headerFields: nil)!)
        }
    }
}
