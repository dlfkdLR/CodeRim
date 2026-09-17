import XCTest
@testable import CodeRim

final class NotchCopilotUsageTests: XCTestCase {
    func testReadsCopilotQuotas() throws {
        let json = """
        {"copilot_plan":"individual","quota_reset_date":"2026-10-01T00:00:00Z",
         "quota_snapshots":{
          "chat":{"entitlement":50,"remaining":48,"used":2,"unlimited":false},
          "completions":{"entitlement":2000,"remaining":1990,"used":10,"unlimited":false},
          "premium_interactions":{"entitlement":300,"remaining":294,"used":6,"unlimited":false}}}
        """

        let windows = try GitHubCopilotUsage.windows(from: Data(json.utf8))
        XCTAssertEqual(windows.map(\.id), ["premium_interactions", "chat", "completions"])
        XCTAssertEqual(windows[0].label, "Premium requests")
        XCTAssertEqual(windows[0].usedFraction ?? -1, 0.02, accuracy: 0.0001)
        XCTAssertEqual(windows[0].duration, 30 * 86400)
        XCTAssertEqual(windows[1].usedFraction ?? -1, 0.04, accuracy: 0.0001)
    }

    /// Recorded from a live `individual` account in late 2026: `used` is gone,
    /// each quota's `quota_reset_at` is a `0` placeholder, and the reset date
    /// is stated once at the root — a bare `yyyy-MM-dd` plus a full ISO
    /// `*_utc`. Premium is entitlement 0 on this plan and drops out.
    func testCurrentIndividualShapeKeepsTheMonthlyReset() throws {
        let json = """
        {"login":"someone","copilot_plan":"individual",
         "quota_snapshots":{
           "chat":{"percent_remaining":100.0,"unlimited":false,"quota_reset_at":0,
                   "remaining":200,"entitlement":200},
           "completions":{"percent_remaining":100.0,"unlimited":false,"quota_reset_at":0,
                          "remaining":2000,"entitlement":2000},
           "premium_interactions":{"percent_remaining":0.0,"unlimited":false,
                                   "quota_reset_at":0,"remaining":0,"entitlement":0}},
         "quota_reset_date":"2026-10-01",
         "quota_reset_date_utc":"2026-10-01T00:00:00.000Z"}
        """
        let windows = try GitHubCopilotUsage.windows(from: Data(json.utf8))
        XCTAssertEqual(windows.map(\.id), ["chat", "completions"])
        XCTAssertEqual(windows[0].usedFraction ?? -1, 0, accuracy: 0.0001)

        let reset = try XCTUnwrap(windows[0].resetsAt)
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        XCTAssertEqual(utc.component(.month, from: reset), 10)
        XCTAssertEqual(utc.component(.day, from: reset), 1)
        XCTAssertEqual(windows[0].duration, 30 * 86400, "September is the window that just ended")
    }

    /// The date-only string alone must still resolve, since it is the field the
    /// docs name even when `*_utc` is absent.
    func testBareCalendarResetDateResolves() throws {
        let json = """
        {"quota_snapshots":{"chat":{"unlimited":false,"remaining":10,"entitlement":10}},
         "quota_reset_date":"2026-11-01"}
        """
        let windows = try GitHubCopilotUsage.windows(from: Data(json.utf8))
        let reset = try XCTUnwrap(windows.first?.resetsAt)
        XCTAssertEqual(reset.timeIntervalSince1970,
                       ISO8601DateFormatter().date(from: "2026-11-01T00:00:00Z")!.timeIntervalSince1970,
                       accuracy: 1)
    }

    func testSkipsUnlimitedAndZeroEntitlement() throws {
        let json = """
        {"quota_snapshots":{
          "chat":{"entitlement":0,"remaining":0,"used":0,"unlimited":false},
          "completions":{"entitlement":0,"remaining":0,"used":0,"unlimited":true}}}
        """

        XCTAssertThrowsError(try GitHubCopilotUsage.windows(from: Data(json.utf8))) { error in
            guard case NotchProviderError.nothingMetered = error else {
                return XCTFail("expected nothingMetered, got \(error)")
            }
        }
    }

    func testLoadsEnvironmentTokenBeforeHosts() throws {
        let credentials = try GitHubCopilotCredentials.load(
            environment: ["GH_TOKEN": "env-token"],
            hosts: "github.com:\n    user: octocat\n    oauth_token: old-token\n",
            command: { XCTFail("should not invoke gh"); return nil }
        )
        XCTAssertEqual(credentials.token, "env-token")
        XCTAssertEqual(credentials.username, "octocat")
    }

    func testParsesGitHubCLIHosts() throws {
        let credentials = try GitHubCopilotCredentials.load(
            environment: [:],
            hosts: "github.com:\n    user: octocat\n    oauth_token: cli-token\n",
            command: { XCTFail("should not invoke gh"); return nil }
        )
        XCTAssertEqual(credentials.token, "cli-token")
        XCTAssertEqual(credentials.source, "GitHub CLI")
    }

    func testNoTokenAnywhereIsANeedsAuth() {
        XCTAssertThrowsError(
            try GitHubCopilotCredentials.load(environment: [:], hosts: nil, command: { nil })
        ) { error in
            guard case NotchProviderError.needsAuth = error else {
                return XCTFail("expected needsAuth, got \(error)")
            }
        }
    }

    @MainActor
    func testProviderStaysHiddenUntilItHasAToken() async {
        let provider = CopilotNotchProvider(loadCredentials: {
            throw NotchProviderError.needsAuth
        })
        XCTAssertEqual(provider.id, "copilot")
        XCTAssertFalse(provider.isVisibleWhenAbsent,
                       "a machine without GitHub CLI must not get a Copilot ring")

        do {
            _ = try await provider.fetchSnapshot()
            XCTFail("expected the missing-token error to surface")
        } catch NotchProviderError.needsAuth {
            // expected
        } catch {
            XCTFail("expected needsAuth, got \(error)")
        }
    }
}
