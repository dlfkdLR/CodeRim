import XCTest
@testable import CodeRim

/// Pinned to a response recorded from a live SuperGrok CLI session. Credits is
/// the weekly Grok Build allowance — the one number this account's own endpoint
/// actually states.
final class NotchGrokUsageTests: XCTestCase {
    private let credits = """
    {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY",\
    "start":"2026-09-05T08:21:18.802818+00:00",\
    "end":"2026-09-12T08:21:18.802818+00:00"},\
    "creditUsagePercent":8.0,\
    "onDemandCap":{"val":0},"onDemandUsed":{"val":0},\
    "productUsage":[{"product":"GrokBuild","usagePercent":8.0}],\
    "isUnifiedBillingUser":true,"prepaidBalance":{"val":0},\
    "billingPeriodStart":"2026-09-05T08:21:18.802818+00:00",\
    "billingPeriodEnd":"2026-09-12T08:21:18.802818+00:00"}}
    """

    private func windows() throws -> [LimitWindow] {
        try GrokUsage.windows(creditsJSON: credits)
    }

    func testTheRingIsTheCreditsPercentage() throws {
        let credits = try XCTUnwrap(windows().first { $0.id == "credits" })
        XCTAssertEqual(credits.duration, 7 * 86400)
        XCTAssertEqual(credits.label, "Grok Build")
        XCTAssertEqual(credits.usedFraction ?? -1, 0.08, accuracy: 0.0001)
    }

    func testWeeklyCreditsHaveTheirOwnReset() throws {
        let credits = try XCTUnwrap(windows().first { $0.id == "credits" })
        let reset = try XCTUnwrap(credits.resetsAt)
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        XCTAssertEqual(utc.component(.month, from: reset), 9)
        XCTAssertEqual(utc.component(.day, from: reset), 12)
    }

    func testAnEmptyConfigIsNotASuccessfulReading() {
        XCTAssertThrowsError(try GrokUsage.windows(creditsJSON: #"{"config":{}}"#)) { error in
            guard case NotchProviderError.nothingMetered = error else {
                return XCTFail("expected nothingMetered, got \(error)")
            }
        }
    }

    func testProductOnlyCreditsStillUseTheHeadlineID() throws {
        let productOnly = """
        {"config":{"productUsage":[{"product":"GrokBuild","usagePercent":33.0}],\
        "billingPeriodEnd":"2026-09-12T08:21:18.802818+00:00"}}
        """
        let w = try GrokUsage.windows(creditsJSON: productOnly)
        let credits = try XCTUnwrap(w.first { $0.id == "credits" })
        XCTAssertNil(credits.duration)
        XCTAssertEqual(credits.label, "Grok Build")
        XCTAssertEqual(credits.usedFraction ?? -1, 0.33, accuracy: 0.0001)
        let snap = ProviderSnapshot(
            id: "grok", displayName: "Grok", glyph: .grok,
            fidelity: .official, status: .ok, windows: w, headlineID: "credits"
        )
        XCTAssertEqual(snap.headline?.id, "credits")
        XCTAssertEqual(snap.usedFraction ?? -1, 0.33, accuracy: 0.0001)
    }

    func testWeeklyPoolWithoutAPercentIsAZeroRing() throws {
        let weeklyOnly = """
        {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY",\
        "start":"2026-09-07T20:59:12+00:00",\
        "end":"2026-09-14T20:59:12+00:00"},\
        "onDemandCap":{"val":0},"onDemandUsed":{"val":0},\
        "isUnifiedBillingUser":true,"prepaidBalance":{"val":0}}}
        """
        let w = try GrokUsage.windows(creditsJSON: weeklyOnly)
        let credits = try XCTUnwrap(w.first { $0.id == "credits" })
        XCTAssertEqual(credits.label, "Weekly limit")
        XCTAssertEqual(credits.usedFraction ?? -1, 0, accuracy: 0.0001)
        let reset = try XCTUnwrap(credits.resetsAt)
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        XCTAssertEqual(utc.component(.month, from: reset), 9)
        XCTAssertEqual(utc.component(.day, from: reset), 14)
    }

    func testGarbageIsABadResponseRatherThanAGuess() {
        XCTAssertThrowsError(try GrokUsage.windows(creditsJSON: "not json")) { error in
            guard case NotchProviderError.badResponse = error else {
                return XCTFail("expected badResponse, got \(error)")
            }
        }
    }

    func testHumanizesTheProductNameTheWayTheModalWritesIt() {
        XCTAssertEqual(GrokUsage.humanize("GrokBuild"), "Grok Build")
    }
}

/// The `~/.grok/auth.json` reader — trust the xAI issuer, prefer a live entry.
final class NotchGrokCredentialsTests: XCTestCase {
    private func write(_ json: String) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("grok-\(UUID().uuidString).json")
        try Data(json.utf8).write(to: url)
        return url
    }

    func testReadsATrustedSession() throws {
        let url = try write("""
        {"https://auth.x.ai::grok-cli":{"key":"tok","email":"me@example.com",
          "expires_at":"2999-01-01T00:00:00Z"}}
        """)
        defer { try? FileManager.default.removeItem(at: url) }

        let creds = try GrokCredentials.load(from: url)
        XCTAssertEqual(creds.accessToken, "tok")
        XCTAssertEqual(creds.email, "me@example.com")
        XCTAssertFalse(creds.isExpired)
    }

    func testUntrustedIssuerIsIgnored() throws {
        let url = try write("""
        {"https://idp.example.com::grok-cli":{"key":"private-proxy-token",
          "oidc_issuer":"https://idp.example.com"}}
        """)
        defer { try? FileManager.default.removeItem(at: url) }

        XCTAssertThrowsError(try GrokCredentials.load(from: url)) { error in
            guard case NotchProviderError.needsAuth = error else {
                return XCTFail("expected needsAuth, got \(error)")
            }
        }
    }

    func testMissingFileIsSignedOut() {
        let missing = URL(fileURLWithPath: "/tmp/not-here-\(UUID().uuidString).json")
        XCTAssertThrowsError(try GrokCredentials.load(from: missing)) { error in
            guard case NotchProviderError.needsAuth = error else {
                return XCTFail("expected needsAuth, got \(error)")
            }
        }
    }

    func testPrefersALiveEntryOverAnExpiredOne() throws {
        let url = try write("""
        {"https://auth.x.ai::a":{"key":"stale","expires_at":"2000-01-01T00:00:00Z"},
         "https://auth.x.ai::b":{"key":"fresh","expires_at":"2999-01-01T00:00:00Z"}}
        """)
        defer { try? FileManager.default.removeItem(at: url) }

        XCTAssertEqual(try GrokCredentials.load(from: url).accessToken, "fresh")
    }
}
