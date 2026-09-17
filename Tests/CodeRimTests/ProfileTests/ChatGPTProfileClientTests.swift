import Foundation
import XCTest
@testable import CodeRim

final class ChatGPTProfileClientTests: XCTestCase {
    func testFetchUsesFixedPrivateRequestAndAggregatesCurrentPeriods() async throws {
        let recorder = RequestRecorder()
        let responseData = profileJSON(
            lifetime: 4_490_123_456,
            buckets: [
                ("2026-07-31", 999),
                ("2026-08-01", 50),
                ("2026-08-23", 40),
                ("2026-08-24", 10),
                ("2026-08-25", 20),
                ("2026-08-27", 30)
            ],
            statsAsOf: "2026-08-27"
        )
        let client = ChatGPTProfileClient(
            credentialLoader: {
                ProfileCredential(accessToken: "access-token", accountID: "account-id")
            },
            requestLoader: { request in
                await recorder.record(request)
                return ProfileHTTPResponse(
                    data: responseData,
                    response: Self.httpResponse()
                )
            }
        )

        let snapshot = try await client.fetch(
            now: try isoDate("2026-08-28T09:41:00Z"),
            calendar: utcCalendar,
            weekStart: .monday
        )

        XCTAssertEqual(snapshot.today, 30)
        XCTAssertEqual(snapshot.week, 60)
        XCTAssertEqual(snapshot.month, 150)
        XCTAssertEqual(snapshot.lifetime, 4_490_123_456)
        XCTAssertEqual(snapshot.statsAsOf, try isoDate("2026-08-27T00:00:00Z"))

        let recordedRequest = await recorder.value()
        let request = try XCTUnwrap(recordedRequest)
        XCTAssertEqual(request.url, ChatGPTProfileClient.endpoint)
        XCTAssertEqual(request.httpMethod, "GET")
        XCTAssertEqual(request.cachePolicy, .reloadIgnoringLocalAndRemoteCacheData)
        XCTAssertFalse(request.httpShouldHandleCookies)
        XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer access-token")
        XCTAssertEqual(request.value(forHTTPHeaderField: "ChatGPT-Account-ID"), "account-id")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Accept"), "application/json")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Cache-Control"), "no-store")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Pragma"), "no-cache")
    }

    func testTodayUsesStatsAsOfWhileWeekAndMonthUseCurrentCalendarBoundaries() async throws {
        let data = profileJSON(
            lifetime: 100,
            buckets: [
                ("2026-08-28", 25),
                ("2026-09-01", 75)
            ],
            statsAsOf: "2026-09-01"
        )
        let client = client(returning: data)

        let snapshot = try await client.fetch(
            now: try isoDate("2026-09-03T12:00:00Z"),
            calendar: utcCalendar,
            weekStart: .monday
        )

        XCTAssertEqual(snapshot.today, 75, "Today is the server's stats_as_of bucket")
        XCTAssertEqual(snapshot.week, 75)
        XCTAssertEqual(snapshot.month, 75)
    }

    func testWeekStartSelectionChangesCurrentWeekBoundary() async throws {
        let data = profileJSON(
            lifetime: 60,
            buckets: [
                ("2026-08-22", 10),
                ("2026-08-23", 20),
                ("2026-08-24", 30)
            ],
            statsAsOf: "2026-08-24"
        )
        let client = client(returning: data)
        let now = try isoDate("2026-08-24T12:00:00Z")

        let monday = try await client.fetch(now: now, calendar: utcCalendar, weekStart: .monday)
        let sunday = try await client.fetch(now: now, calendar: utcCalendar, weekStart: .sunday)

        XCTAssertEqual(monday.week, 30)
        XCTAssertEqual(sunday.week, 50)
    }

    func testProfileISODatesUseGregorianCalendarWhenMacCalendarDoesNot() async throws {
        let data = profileJSON(
            lifetime: 60,
            buckets: [
                ("2026-08-24", 10),
                ("2026-08-27", 50)
            ],
            statsAsOf: "2026-08-27"
        )
        var buddhistCalendar = Calendar(identifier: .buddhist)
        buddhistCalendar.timeZone = TimeZone(secondsFromGMT: 0)!

        let snapshot = try await client(returning: data).fetch(
            now: try isoDate("2026-08-28T12:00:00Z"),
            calendar: buddhistCalendar,
            weekStart: .monday
        )

        XCTAssertEqual(snapshot.today, 50)
        XCTAssertEqual(snapshot.week, 60)
        XCTAssertEqual(snapshot.month, 60)
        XCTAssertEqual(snapshot.statsAsOf, try isoDate("2026-08-27T00:00:00Z"))
    }

    func testLaggingStatsAsOfDoesNotMoveCurrentWeekOrMonthBackward() async throws {
        let data = profileJSON(
            lifetime: 10,
            buckets: [("2026-08-28", 10)],
            statsAsOf: "2026-08-28"
        )
        let snapshot = try await client(returning: data).fetch(
            now: try isoDate("2026-09-07T12:00:00Z"),
            calendar: utcCalendar,
            weekStart: .monday
        )

        XCTAssertEqual(snapshot.today, 10)
        XCTAssertEqual(snapshot.week, 0)
        XCTAssertEqual(snapshot.month, 0)
    }

    func testSparseBucketsTreatMissingStatsAsOfDayAsZeroUsage() async throws {
        let data = profileJSON(
            lifetime: 40,
            buckets: [("2026-08-27", 40)],
            statsAsOf: "2026-08-28"
        )

        let snapshot = try await client(returning: data).fetch(
            now: try isoDate("2026-08-28T12:00:00Z"),
            calendar: utcCalendar,
            weekStart: .monday
        )

        XCTAssertEqual(snapshot.today, 0)
        XCTAssertEqual(snapshot.week, 40)
        XCTAssertEqual(snapshot.month, 40)
        XCTAssertEqual(snapshot.lifetime, 40)
    }

    func testInvalidProfilePayloadsAreRejected() async throws {
        let validMetadata = "\"metadata\":{\"generated_at\":\"2026-08-28T01:00:00Z\",\"stats_as_of\":\"2026-08-28\",\"stats_error\":null}"
        let payloads = [
            "{\"stats\":{\"lifetime_tokens\":-1,\"daily_usage_buckets\":[]},\(validMetadata)}",
            String(data: profileJSON(lifetime: 1, buckets: [("2026-02-30", 1)], statsAsOf: "2026-02-28"), encoding: .utf8)!,
            String(data: profileJSON(lifetime: 2, buckets: [("2026-08-28", 1), ("2026-08-28", 1)], statsAsOf: "2026-08-28"), encoding: .utf8)!,
            String(data: profileJSON(lifetime: 1, buckets: [("2026-08-29", 1)], statsAsOf: "2026-08-28"), encoding: .utf8)!,
            "{\"stats\":{\"lifetime_tokens\":1,\"daily_usage_buckets\":[]},\"metadata\":{\"generated_at\":\"bad\",\"stats_as_of\":\"2026-08-28\",\"stats_error\":null}}",
            "{\"stats\":{\"lifetime_tokens\":1,\"daily_usage_buckets\":[]},\"metadata\":{\"generated_at\":\"2026-08-28T01:00:00Z\",\"stats_as_of\":\"2026-08-28\",\"stats_error\":\"upstream\"}}",
            "{\"stats\":{\"lifetime_tokens\":1,\"daily_usage_buckets\":[]},\"metadata\":{\"generated_at\":\"2026-08-28T01:00:00Z\",\"stats_as_of\":\"2026-08-28\"}}"
        ]

        for payload in payloads {
            do {
                _ = try await client(returning: Data(payload.utf8)).fetch(
                    now: try isoDate("2026-08-28T12:00:00Z"),
                    calendar: utcCalendar,
                    weekStart: .monday
                )
                XCTFail("Expected invalid payload to fail")
            } catch let error as ProfileUsageError {
                XCTAssertEqual(error, .invalidResponse)
            }
        }
    }

    func testPeriodAggregationRejectsOverflow() async throws {
        let data = profileJSON(
            lifetime: Int64.max,
            buckets: [
                ("2026-08-27", Int64.max),
                ("2026-08-28", 1)
            ],
            statsAsOf: "2026-08-28"
        )

        do {
            _ = try await client(returning: data).fetch(
                now: try isoDate("2026-08-28T12:00:00Z"),
                calendar: utcCalendar,
                weekStart: .monday
            )
            XCTFail("Expected overflow to fail")
        } catch let error as ProfileUsageError {
            XCTAssertEqual(error, .invalidResponse)
        }
    }

    func testFetchRejectsUnexpectedStatusURLAndOversizedInjectedResponse() async throws {
        let validData = profileJSON(lifetime: 0, buckets: [], statsAsOf: "2026-08-28")
        let cases: [(ProfileHTTPResponse, ProfileUsageError)] = [
            (
                ProfileHTTPResponse(data: validData, response: Self.httpResponse(status: 401)),
                .invalidCredentials
            ),
            (
                ProfileHTTPResponse(data: validData, response: Self.httpResponse(status: 403)),
                .invalidCredentials
            ),
            (
                ProfileHTTPResponse(data: validData, response: Self.httpResponse(status: 500)),
                .invalidHTTPResponse
            ),
            (
                ProfileHTTPResponse(
                    data: validData,
                    response: Self.httpResponse(url: URL(string: "https://example.com/profile")!)
                ),
                .invalidHTTPResponse
            ),
            (
                ProfileHTTPResponse(
                    data: Data(repeating: 0, count: ChatGPTProfileClient.maximumResponseSize + 1),
                    response: Self.httpResponse()
                ),
                .responseTooLarge
            )
        ]

        for (response, expectedError) in cases {
            let client = ChatGPTProfileClient(
                credentialLoader: {
                    ProfileCredential(accessToken: "access-token", accountID: "account-id")
                },
                requestLoader: { _ in response }
            )
            do {
                _ = try await client.fetch()
                XCTFail("Expected response validation to fail")
            } catch let error as ProfileUsageError {
                XCTAssertEqual(error, expectedError)
            }
        }
    }

    func testInjectedInvalidCredentialNeverInvokesNetworkLoader() async throws {
        let counter = InvocationCounter()
        let client = ChatGPTProfileClient(
            credentialLoader: {
                ProfileCredential(accessToken: "token\nvalue", accountID: "account-id")
            },
            requestLoader: { _ in
                await counter.increment()
                return ProfileHTTPResponse(data: Data(), response: Self.httpResponse())
            }
        )

        do {
            _ = try await client.fetch()
            XCTFail("Expected credential validation to fail")
        } catch let error as ProfileUsageError {
            XCTAssertEqual(error, .invalidCredentials)
        }
        let invocationCount = await counter.value()
        XCTAssertEqual(invocationCount, 0)
    }

    private func client(returning data: Data) -> ChatGPTProfileClient {
        ChatGPTProfileClient(
            credentialLoader: {
                ProfileCredential(accessToken: "access-token", accountID: "account-id")
            },
            requestLoader: { _ in
                ProfileHTTPResponse(data: data, response: Self.httpResponse())
            }
        )
    }

    private func profileJSON(
        lifetime: Int64,
        buckets: [(String, Int64)],
        statsAsOf: String
    ) -> Data {
        let bucketJSON = buckets.map { date, tokens in
            "{\"start_date\":\"\(date)\",\"tokens\":\(tokens),\"ignored\":true}"
        }.joined(separator: ",")
        return Data(
            """
            {
              "stats": {
                "lifetime_tokens": \(lifetime),
                "daily_usage_buckets": [\(bucketJSON)],
                "ignored": "value"
              },
              "metadata": {
                "generated_at": "2026-08-28T01:00:00.123Z",
                "stats_as_of": "\(statsAsOf)",
                "stats_error": null,
                "ignored": "value"
              },
              "ignored": {"secret": "must not be decoded"}
            }
            """.utf8
        )
    }

    private static func httpResponse(
        status: Int = 200,
        url: URL = ChatGPTProfileClient.endpoint
    ) -> HTTPURLResponse {
        HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/2", headerFields: nil)!
    }

    private func isoDate(_ value: String) throws -> Date {
        try Date.ISO8601FormatStyle().parse(value)
    }

    private var utcCalendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        calendar.locale = Locale(identifier: "en_US_POSIX")
        return calendar
    }
}

private actor RequestRecorder {
    private var request: URLRequest?

    func record(_ request: URLRequest) {
        self.request = request
    }

    func value() -> URLRequest? {
        request
    }
}

private actor InvocationCounter {
    private var count = 0

    func increment() {
        count += 1
    }

    func value() -> Int {
        count
    }
}
