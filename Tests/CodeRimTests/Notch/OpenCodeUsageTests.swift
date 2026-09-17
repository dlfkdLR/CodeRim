import XCTest
@testable import CodeRim

/// The Go plan windows, pinned to the live response (secret redacted).
final class NotchOpenCodeUsageTests: XCTestCase {
    private let payload = """
    {"usage":{\
    "rolling":{"status":"ok","percent":0,"resetsAt":"2026-09-06T12:31:06.611Z"},\
    "weekly":{"status":"ok","percent":0,"resetsAt":"2026-09-07T00:00:00.611Z"},\
    "monthly":{"status":"ok","percent":0,"resetsAt":"2026-10-03T13:09:45.611Z"}}}
    """

    func testReadsAllThreeWindows() throws {
        let w = try OpenCodeUsage.windows(fromJSON: payload)
        XCTAssertEqual(w.map(\.duration), [18000, 604800, 30 * 86400])
        XCTAssertEqual(w.map(\.id), ["rolling", "weekly", "monthly"])
        XCTAssertEqual(w.map(\.label), ["5h limit", "Weekly limit", "Monthly limit"])
        XCTAssertTrue(w.allSatisfy { ($0.usedFraction ?? -1) == 0 })
    }

    /// `percent` is used, matching the dashboard's "X% used" — the ring must
    /// not invert it.
    func testPercentIsUsedNotRemaining() throws {
        let json = """
        {"usage":{"rolling":{"status":"ok","percent":65,"resetsAt":"2026-09-06T12:31:06.611Z"}}}
        """
        let w = try OpenCodeUsage.windows(fromJSON: json)
        XCTAssertEqual(w.count, 1)
        XCTAssertEqual(w[0].usedFraction ?? -1, 0.65, accuracy: 0.0001)
    }

    func testReadsAMillisecondResetTime() throws {
        let w = try OpenCodeUsage.windows(fromJSON: payload)
        let rolling = try XCTUnwrap(w.first { $0.id == "rolling" })
        let at = try XCTUnwrap(rolling.resetsAt)
        let plain = ISO8601DateFormatter().date(from: "2026-09-06T12:31:06Z")!
        XCTAssertEqual(at.timeIntervalSince1970, plain.timeIntervalSince1970, accuracy: 1)
    }

    func testWindowsWithoutAPercentAreDropped() throws {
        let json = """
        {"usage":{"rolling":{"status":"ok"},"weekly":{"status":"ok","percent":3}}}
        """
        XCTAssertEqual(try OpenCodeUsage.windows(fromJSON: json).map(\.id), ["weekly"])
    }

    func testAnEmptyUsageIsNotAReading() {
        for json in ["{}", #"{"usage":{}}"#, #"{"usage":{"rolling":{"status":"ok"}}}"#] {
            XCTAssertThrowsError(try OpenCodeUsage.windows(fromJSON: json)) { error in
                guard case NotchProviderError.badResponse = error else {
                    return XCTFail("expected badResponse, got \(error)")
                }
            }
        }
    }
}

/// Only the `opencode-go` entry may ever be claimed — any other is somebody
/// else's account.
final class NotchOpenCodeCredentialsTests: XCTestCase {
    private func file(_ text: String) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("opencode-auth-\(UUID().uuidString).json")
        try text.write(to: url, atomically: true, encoding: .utf8)
        return url
    }

    func testReadsTheGoKey() throws {
        let url = try file(#"{"opencode-go":{"type":"api","key":"sk-go-live"}}"#)
        defer { try? FileManager.default.removeItem(at: url) }
        XCTAssertEqual(OpenCodeCredentials.load(from: url)?.token, "sk-go-live")
    }

    func testReadsABareStringEntry() throws {
        let url = try file(#"{"opencode-go":"sk-go-live"}"#)
        defer { try? FileManager.default.removeItem(at: url) }
        XCTAssertEqual(OpenCodeCredentials.load(from: url)?.token, "sk-go-live")
    }

    func testNeverClaimsAnotherVendorsKey() throws {
        let url = try file(
            #"{"openai":{"type":"api","key":"sk-openai"},"google":{"type":"api","key":"g-key"}}"#)
        defer { try? FileManager.default.removeItem(at: url) }
        XCTAssertNil(OpenCodeCredentials.load(from: url))
    }

    func testAnEmptyKeyIsMissing() throws {
        let url = try file(#"{"opencode-go":{"type":"api","key":""}}"#)
        defer { try? FileManager.default.removeItem(at: url) }
        XCTAssertNil(OpenCodeCredentials.load(from: url))
    }
}

/// The 429 backoff — a minute, doubling, capped, and persisted so a penalty
/// survives a relaunch.
final class NotchOpenCodeBackoffTests: XCTestCase {
    func testBackoffDoublesFromAMinuteAndCaps() {
        XCTAssertEqual(OpenCodeNotchProvider.backoff(forAttempt: 0, retryAfter: nil), 60)
        XCTAssertEqual(OpenCodeNotchProvider.backoff(forAttempt: 1, retryAfter: nil), 120)
        XCTAssertEqual(OpenCodeNotchProvider.backoff(forAttempt: 3, retryAfter: nil), 480)
        XCTAssertEqual(OpenCodeNotchProvider.backoff(forAttempt: 10, retryAfter: nil), 15 * 60)
    }

    func testServerHintOnlyRaisesTheFloor() {
        XCTAssertEqual(OpenCodeNotchProvider.backoff(forAttempt: 0, retryAfter: 300), 300)
        XCTAssertEqual(OpenCodeNotchProvider.backoff(forAttempt: 0, retryAfter: 10), 60)
    }

    func testArchivePersistsThePenaltyPerProvider() {
        let suite = "opencode-backoff-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let archive = UsageArchive(defaults: defaults)

        XCTAssertNil(archive.loadBackoffUntil(providerID: "opencode"))
        let until = Date().addingTimeInterval(600)
        archive.saveBackoffUntil(until, providerID: "opencode")
        XCTAssertEqual(archive.loadBackoffUntil(providerID: "opencode")?.timeIntervalSince1970 ?? 0,
                       until.timeIntervalSince1970, accuracy: 1)
        XCTAssertNil(archive.loadBackoffUntil(providerID: "grok"))

        // A past date is spent, not returned.
        archive.saveBackoffUntil(Date().addingTimeInterval(-10), providerID: "opencode")
        XCTAssertNil(archive.loadBackoffUntil(providerID: "opencode"))
    }
}
