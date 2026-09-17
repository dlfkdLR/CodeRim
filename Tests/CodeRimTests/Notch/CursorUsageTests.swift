import CSQLite
import XCTest
@testable import CodeRim

/// Pinned to a response recorded from a live free account. `/api/usage-summary`
/// is not a documented API, so this is what fails first if it changes.
final class NotchCursorUsageTests: XCTestCase {
    private let recorded = """
    {"billingCycleStart":"2026-08-24T03:32:15.933Z",
     "billingCycleEnd":"2026-09-24T03:32:15.933Z",
     "membershipType":"free","limitType":"user","isUnlimited":false,
     "individualUsage":{
       "plan":{"enabled":true,"used":0,"limit":0,"remaining":0,
               "breakdown":{"included":0,"bonus":19,"total":19},
               "autoPercentUsed":0,"apiPercentUsed":19,"totalPercentUsed":9.5},
       "onDemand":{"enabled":false,"used":0,"limit":null,"remaining":null}},
     "teamUsage":{}}
    """

    private func windows(_ json: String) throws -> [LimitWindow] {
        try CursorUsage.windows(fromJSON: json)
    }

    func testReadsTheAutoBar() throws {
        let w = try windows(recorded)
        XCTAssertEqual(w[0].id, "auto")
        XCTAssertEqual(w[0].label, CursorUsage.modelsLabel)
        XCTAssertEqual(w[0].usedFraction ?? -1, 0, accuracy: 0.0001)
        XCTAssertEqual(w[0].duration, 31 * 86400)
        XCTAssertEqual(CursorUsage.headlineID(in: w), "auto")
    }

    func testMonthlyPaceUsesActualBillingDates() throws {
        for days in [28, 29, 30, 31] {
            let start = Date(timeIntervalSince1970: 1_800_000_000)
            let end = start.addingTimeInterval(Double(days) * 86400)
            let iso = ISO8601DateFormatter()
            let result = try windows("""
            {"billingCycleStart":"\(iso.string(from: start))",
             "billingCycleEnd":"\(iso.string(from: end))",
             "individualUsage":{"plan":{"autoPercentUsed":80}}}
            """)
            let window = try XCTUnwrap(result.first)
            let halfway = start.addingTimeInterval(Double(days) * 43200)
            XCTAssertEqual(window.duration, Double(days) * 86400)
            XCTAssertEqual(try XCTUnwrap(window.usagePace(now: halfway)).percentagePoints, 30,
                           accuracy: 0.00001)
        }
    }

    func testApiUsageIsReportedSeparately() throws {
        let w = try windows(recorded)
        XCTAssertEqual(w.map(\.id), ["auto", "api"])
        XCTAssertEqual(w[1].usedFraction ?? -1, 0.19, accuracy: 0.0001)
    }

    func testZeroApiUsageIsOmitted() throws {
        let json = #"{"individualUsage":{"plan":{"autoPercentUsed":4,"apiPercentUsed":0}}}"#
        XCTAssertEqual(try windows(json).map(\.id), ["auto"])
    }

    func testResetComesFromTheBillingCycleEnd() throws {
        let reset = try XCTUnwrap(windows(recorded)[0].resetsAt)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        XCTAssertEqual(calendar.component(.month, from: reset), 9)
        XCTAssertEqual(calendar.component(.day, from: reset), 24)
    }

    func testOnDemandOnlyCountsWhenSwitchedOnWithACeiling() throws {
        let on = """
        {"individualUsage":{"plan":{"autoPercentUsed":5},
                            "onDemand":{"enabled":true,"used":3,"limit":50}}}
        """
        XCTAssertEqual(try windows(on).map(\.id), ["auto", "on_demand"])

        let off = """
        {"individualUsage":{"plan":{"autoPercentUsed":5},
                            "onDemand":{"enabled":false,"used":0,"limit":null}}}
        """
        XCTAssertEqual(try windows(off).map(\.id), ["auto"])
    }

    func testNothingMeteredRatherThanAFalseZero() {
        let json = #"{"membershipType":"free","individualUsage":{"plan":{}}}"#
        XCTAssertThrowsError(try windows(json)) { error in
            guard case NotchProviderError.nothingMetered = error else {
                return XCTFail("expected nothingMetered, got \(error)")
            }
        }
    }

    func testRejectsRubbish() {
        XCTAssertThrowsError(try windows("not json"))
    }

    /// The blended total is not a dashboard row.
    func testTheBlendIsNotARow() throws {
        let json = """
        {"individualUsage":{"plan":{
          "autoPercentUsed":19.870833333333334,
          "apiPercentUsed":100,
          "totalPercentUsed":25.123586744639375}}}
        """
        let w = try windows(json)
        XCTAssertEqual(w.map(\.id), ["auto", "api"])
        XCTAssertEqual(w[0].label, CursorUsage.modelsLabel)
        XCTAssertEqual(CursorUsage.headlineID(in: w), "auto")
    }
}

/// Cursor reports 0% and means it — an earlier version suppressed this as
/// "no allowance to be a percentage of", which hid a correct reading.
final class NotchCursorZeroUsageTests: XCTestCase {
    private let freePlan = """
    {"billingCycleEnd":"2026-09-20T01:35:15.142Z","membershipType":"free","isUnlimited":false,
     "individualUsage":{"plan":{"enabled":true,"used":0,"limit":0,"remaining":0,
       "breakdown":{"included":0,"bonus":0,"total":0},
       "autoPercentUsed":0,"apiPercentUsed":0,"totalPercentUsed":0},
      "onDemand":{"enabled":false,"used":0,"limit":null,"remaining":null}}}
    """

    func testZeroPercentIsShownRatherThanSuppressed() throws {
        let windows = try CursorUsage.windows(fromJSON: freePlan)
        XCTAssertEqual(windows.first?.id, "auto")
        XCTAssertEqual(windows.first?.usedFraction, 0)
    }

    func testItStillReadsARealPercentage() throws {
        let used = freePlan.replacingOccurrences(of: "\"autoPercentUsed\":0",
                                                 with: "\"autoPercentUsed\":34")
        let windows = try CursorUsage.windows(fromJSON: used)
        XCTAssertEqual(windows.first?.usedFraction ?? 0, 0.34, accuracy: 0.0001)
    }
}

/// Enterprise / team plans omit `plan` and meter a hard `overall` ceiling.
final class NotchCursorEnterpriseUsageTests: XCTestCase {
    private let recorded = """
    {"billingCycleStart":"2026-09-01T00:00:00.000Z",
     "billingCycleEnd":"2026-10-01T00:00:00.000Z",
     "membershipType":"enterprise","limitType":"team","isUnlimited":false,
     "individualUsage":{
       "overall":{"enabled":true,"used":6907,"limit":45000,"remaining":38093}},
     "teamUsage":{
       "onDemand":{"enabled":true,"used":0,"limit":1000000,"remaining":1000000}}}
    """

    func testOverallCeilingIsTheIncludedWindow() throws {
        let windows = try CursorUsage.windows(fromJSON: recorded)
        XCTAssertEqual(windows.map(\.id), ["included"])
        XCTAssertEqual(windows[0].label, "Included usage")
        XCTAssertEqual(windows[0].usedFraction ?? -1, 6907.0 / 45000.0, accuracy: 0.0001)
    }

    func testUnusedTeamOnDemandIsOmitted() throws {
        let windows = try CursorUsage.windows(fromJSON: recorded)
        XCTAssertFalse(windows.contains { $0.id == "team_on_demand" })
    }

    func testTeamOnDemandAppearsOnceTouched() throws {
        let touched = recorded.replacingOccurrences(
            of: #""onDemand":{"enabled":true,"used":0,"limit":1000000,"remaining":1000000}"#,
            with: #""onDemand":{"enabled":true,"used":250000,"limit":1000000,"remaining":750000}"#)
        let windows = try CursorUsage.windows(fromJSON: touched)
        XCTAssertEqual(windows.map(\.id), ["included", "team_on_demand"])
        XCTAssertEqual(windows[1].usedFraction ?? -1, 0.25, accuracy: 0.0001)
    }

    func testTheHeadlineResolvesToTheEnterpriseCeiling() throws {
        let windows = try CursorUsage.windows(fromJSON: recorded)
        XCTAssertEqual(CursorUsage.headlineID(in: windows), "included")
    }
}

/// Borrowing the editor's session is what stops the notch reporting a different
/// account's usage. Editor path only for now.
final class NotchCursorCredentialsTests: XCTestCase {
    func testCookieIsTheAccountAndTokenPair() {
        let credentials = CursorCredentials(accountID: "google-oauth2|user_ABC", accessToken: "tok")
        XCTAssertEqual(credentials.sessionCookie,
                       "WorkosCursorSessionToken=google-oauth2|user_ABC::tok")
    }

    func testAMissingStoreMeansSignedOutRatherThanAnError() {
        let missing = URL(fileURLWithPath: "/tmp/definitely-not-here-\(UUID().uuidString).vscdb")
        XCTAssertThrowsError(try CursorCredentials.load(from: missing)) { error in
            guard case NotchProviderError.needsAuth = error else {
                return XCTFail("expected needsAuth, got \(error)")
            }
        }
    }

    func testReadsTheSessionFromTheEditorStore() throws {
        let store = try writeEditorStore(account: "auth0|user_EDITOR", token: "editor-tok")
        defer { try? FileManager.default.removeItem(at: store) }

        let credentials = try CursorCredentials.load(from: store)
        XCTAssertEqual(credentials.sessionCookie,
                       "WorkosCursorSessionToken=auth0|user_EDITOR::editor-tok")
    }

    /// When the editor omits `stripeMembershipAuthId`, the cookie's left half
    /// comes from the access token's `sub`.
    func testFallsBackToTheJWTSubjectForTheAccountID() throws {
        let token = Self.jwt(sub: "auth0|user_JWT")
        let store = try writeEditorStore(account: nil, token: token)
        defer { try? FileManager.default.removeItem(at: store) }

        let credentials = try CursorCredentials.load(from: store)
        XCTAssertEqual(credentials.accountID, "auth0|user_JWT")
    }

    func testSubjectIsReadFromTheAccessTokenJWT() {
        XCTAssertEqual(CursorCredentials.subject(fromJWT: Self.jwt(sub: "auth0|user_ABC")),
                       "auth0|user_ABC")
    }

    // MARK: - cursor-agent CLI fallback

    private func writeAgentConfig(authId: String?, userId: Int?, email: String?) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("cursor-cli-\(UUID().uuidString).json")
        var info: [String: Any] = [:]
        if let authId { info["authId"] = authId }
        if let userId { info["userId"] = userId }
        if let email { info["email"] = email }
        try JSONSerialization.data(withJSONObject: ["authInfo": info]).write(to: url)
        return url
    }

    func testAgentCookieUsesAuthIdFromCliConfig() throws {
        let token = Self.jwt(sub: "auth0|user_JWT")
        let config = try writeAgentConfig(authId: "auth0|user_CLI", userId: 42, email: "cli@example.com")
        defer { try? FileManager.default.removeItem(at: config) }

        let creds = try CursorCredentials.agentSession(token: token, configURL: config)
        XCTAssertEqual(creds.accountID, "auth0|user_CLI")
        XCTAssertEqual(creds.sessionCookie, "WorkosCursorSessionToken=auth0|user_CLI::\(token)")
    }

    func testAgentAccountIDFallsBackToUserIdThenJWTSub() throws {
        let token = Self.jwt(sub: "auth0|user_JWT")
        let withUser = try writeAgentConfig(authId: nil, userId: 99, email: nil)
        defer { try? FileManager.default.removeItem(at: withUser) }
        XCTAssertEqual(CursorCredentials.agentAccountID(token: token, configURL: withUser), "99")

        let missing = URL(fileURLWithPath: "/tmp/not-here-\(UUID().uuidString).json")
        XCTAssertEqual(CursorCredentials.agentAccountID(token: token, configURL: missing), "auth0|user_JWT")
    }

    func testExpiredAgentTokenKeepsTheLastReading() {
        let expired = Self.jwt(sub: "auth0|u", exp: 1)
        XCTAssertThrowsError(
            try CursorCredentials.agentSession(token: expired,
                                               configURL: URL(fileURLWithPath: "/tmp/x.json"))
        ) { error in
            guard case NotchProviderError.credentialExpired = error else {
                return XCTFail("expected credentialExpired, got \(error)")
            }
        }
    }

    func testNoAgentTokenIsSignedOut() {
        XCTAssertThrowsError(
            try CursorCredentials.agentSession(token: nil, configURL: URL(fileURLWithPath: "/tmp/x.json"))
        ) { error in
            guard case NotchProviderError.needsAuth = error else {
                return XCTFail("expected needsAuth, got \(error)")
            }
        }
    }

    func testAgentAccountReadsEmailFromCliConfig() throws {
        let config = try writeAgentConfig(authId: "auth0|user_CLI", userId: 1, email: "cli@example.com")
        defer { try? FileManager.default.removeItem(at: config) }
        let account = try XCTUnwrap(CursorCredentials.agentAccount(from: config))
        XCTAssertEqual(account.label, "cli@example.com")
        XCTAssertEqual(account.source, "cursor-agent")
    }

    func testSignInOpensTheEditorWhenItIsInstalled() {
        guard case .openApp(let bundleID, let name) =
                CursorCredentials.signInRoute(editorInstalled: true) else {
            return XCTFail("expected openApp")
        }
        XCTAssertEqual(bundleID, CursorCredentials.bundleID)
        XCTAssertEqual(name, "Cursor")
    }

    func testSignInGivesGuidanceWhenTheEditorIsMissing() {
        guard case .guidance(let text) =
                CursorCredentials.signInRoute(editorInstalled: false) else {
            return XCTFail("expected guidance")
        }
        XCTAssertTrue(text.contains("cursor-agent") || text.contains("Cursor editor"), text)
    }

    private func writeEditorStore(account: String?, token: String) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("cursor-\(UUID().uuidString).vscdb")
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path, &db), SQLITE_OK)
        sqlite3_exec(db, "CREATE TABLE ItemTable (key TEXT, value TEXT);", nil, nil, nil)
        sqlite3_exec(db,
            "INSERT INTO ItemTable VALUES ('cursorAuth/accessToken', '\(token)');", nil, nil, nil)
        if let account {
            sqlite3_exec(db,
                "INSERT INTO ItemTable VALUES ('cursorAuth/stripeMembershipAuthId', '\(account)');",
                nil, nil, nil)
        }
        sqlite3_close(db)
        return url
    }

    /// Unsigned, for tests only — the server never sees these.
    private static func jwt(sub: String, exp: TimeInterval = Date().timeIntervalSince1970 + 3600) -> String {
        func encode(_ object: [String: Any]) -> String {
            let data = try! JSONSerialization.data(withJSONObject: object)
            return data.base64EncodedString()
                .replacingOccurrences(of: "+", with: "-")
                .replacingOccurrences(of: "/", with: "_")
                .trimmingCharacters(in: CharacterSet(charactersIn: "="))
        }
        return encode(["alg": "none", "typ": "JWT"])
            + "." + encode(["sub": sub, "exp": exp])
            + ".sig"
    }
}
