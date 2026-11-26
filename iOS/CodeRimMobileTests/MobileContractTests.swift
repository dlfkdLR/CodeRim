import XCTest
@testable import CodeRimMobile

final class MobileContractTests: XCTestCase {
    func testSelectionKeepsOriginalSpellingAndDuplicateOptions() throws {
        let composed = "é", decomposed = "e\u{301}"
        let available = [MobileProviderOption(id: composed, name: "First reading"),
                         MobileProviderOption(id: decomposed, name: "Second reading")]
        let saved = MobilePreferences(providerIDs: [decomposed, "claude", "codex", "claude"])
        XCTAssertEqual(saved.selectionOptions(available: available).map(\.name),
                       ["First reading", "Second reading", "Claude · not connected", "Codex · not connected"])
        let unchanged = try XCTUnwrap(saved.updating(composed, enabled: true, available: available))
        XCTAssertEqual(Array(unchanged.providerIDs[0].utf8), Array(decomposed.utf8))
        XCTAssertEqual(saved.updating("claude", enabled: false, available: available)?.providerIDs,
                       [decomposed, "codex"])
        let all = MobilePreferences()
        XCTAssertNil(all.updating(composed, enabled: false, available: available))
        let next = try XCTUnwrap(all.updating("other", enabled: true, available: available))
        XCTAssertEqual(next.providerIDs.map { Array($0.utf8) }, [composed, decomposed, "other"].map { Array($0.utf8) })
    }

    func testToggleLimitAppliesAfterRemovingEveryMatchingID() {
        let allowed = (0..<100).map { "p" + String($0) }
        let stored = MobilePreferences(providerIDs: Array(repeating: "old", count: 120) + allowed)
        XCTAssertEqual(stored.updating("old", enabled: false, available: [])?.providerIDs, allowed)
        XCTAssertNil(MobilePreferences(providerIDs: allowed).updating("new", enabled: true, available: []))
        XCTAssertNil(MobilePreferences().updating("absent", enabled: false, available: []))
    }

    func testRetainedDesktopSnapshotThroughRelayDecodesOnThePhone() throws {
        struct Fixture: Decodable { let response: MobileSnapshotResponse }
        let url = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "mobile-desktop-v2141", withExtension: "json"))
        let reply = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url)).response
        XCTAssertEqual(reply.preferences.providerIDs, ["codex"])
        XCTAssertEqual(reply.devices?.first?.id, "fixture-mac")
        XCTAssertEqual(reply.state.connection, "connected")
        XCTAssertEqual(reply.state.workingCount, 1)
        XCTAssertEqual(reply.state.sessions.first?.title, "")
        XCTAssertEqual(reply.state.providers.first?.windows.first?.remainingPercent, 75)
        XCTAssertEqual(reply.state.providers.first?.windows.first?.resetsAt, 1_790_000_600)
        XCTAssertNil(reply.state.providers.first?.todayTokens)
        XCTAssertEqual(reply.pushConfigured, false)
    }

    func testEndpointRejectsInsecureURLsAndAmbiguousOrigins() throws {
        for text in ["http://example.com", "https://name:password@example.com", "https://example.com/other", "https://example.com?token=x", "https://example.com#fragment", "file:///tmp"] {
            XCTAssertThrowsError(try MobileRelayClient.validatedEndpoint(text), text)
        }
        XCTAssertEqual(try MobileRelayClient.validatedEndpoint(" https://relay.example.com/ ").host, "relay.example.com")
    }
    func testAPNsStateUsesUnixEpochAndPreservesMissingValues() throws {
        let wire = #"{"connection":"connected","updatedAt":1790000000,"staleAt":1790000090,"providers":[{"id":"codex","name":"Codex","state":"needsAuth","windows":[{"name":"Weekly","remainingPercent":null,"resetsAt":null}],"todayTokens":null,"localState":"unavailable","updatedAt":null}],"sessions":[{"providerID":"codex","phase":"unavailable","title":"","since":null}],"additionalSessionCount":0,"workingCount":0,"waitingCount":0,"unavailableCount":1}"#
        let decoded = try JSONDecoder().decode(MobileActivityState.self, from: Data(wire.utf8))
        XCTAssertNil(decoded.providers[0].todayTokens)
        XCTAssertNil(decoded.providers[0].windows[0].remainingPercent)
        XCTAssertEqual(decoded.sessions[0].phase, .unavailable)
        XCTAssertFalse(decoded.isStale(at: Date(timeIntervalSince1970: 1790000001)))
        XCTAssertTrue(decoded.isStale(at: Date(timeIntervalSince1970: 1790000090)))
        let roundtrip = try JSONDecoder().decode(MobileActivityState.self, from: JSONEncoder().encode(decoded))
        XCTAssertEqual(roundtrip, decoded)
    }
    func testSnapshotPushAvailabilityDecodesForNewAndOlderRelays() throws {
        let response = MobileSnapshotResponse(state: .disconnected, preferences: MobilePreferences(), availableProviders: [])
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(response)) as? [String: Any])
        json["pushConfigured"] = true
        let enabled = try JSONDecoder().decode(MobileSnapshotResponse.self, from: JSONSerialization.data(withJSONObject: json))
        XCTAssertEqual(enabled.pushConfigured, true)
        json["pushConfigured"] = false
        let disabled = try JSONDecoder().decode(MobileSnapshotResponse.self, from: JSONSerialization.data(withJSONObject: json))
        XCTAssertEqual(disabled.pushConfigured, false)
        json.removeValue(forKey: "pushConfigured")
        let older = try JSONDecoder().decode(MobileSnapshotResponse.self, from: JSONSerialization.data(withJSONObject: json))
        XCTAssertNil(older.pushConfigured)
    }
    func testActivityTokenRegistrationNeverCarriesPermissionToResumeAfterStop() throws {
        let registration = MobileActivityRegistration(activityID: "fixture", pushToken: String(repeating: "a", count: 64))
        let wire = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(registration)) as? [String: String])
        XCTAssertEqual(Set(wire.keys), ["activityID", "pushToken"])
        XCTAssertEqual(wire["activityID"], "fixture")
    }

    func testDisconnectedStateNeverLooksLive() {
        XCTAssertTrue(MobileActivityState.disconnected.isStale())
    }
    func testPickerStateRoundTripsAndOlderStateRemainsDecodable() throws {
        var state = MobileActivityState.disconnected
        state.providerPicker = .init(isOpen: true, page: 1, pageCount: 3, options: [.init(id: "claude", name: "Claude")])
        let encoded = try JSONEncoder().encode(state)
        let decoded = try JSONDecoder().decode(MobileActivityState.self, from: encoded)
        XCTAssertEqual(decoded, state)
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        json.removeValue(forKey: "providerPicker")
        let legacy = try JSONDecoder().decode(MobileActivityState.self, from: JSONSerialization.data(withJSONObject: json))
        XCTAssertNil(legacy.providerPicker)
    }

    func testScalablePickerWireMetadataAndLegacyDefaults() throws {
        let wire = #"{"isOpen":true,"page":0,"pageCount":34,"options":[],"mode":"all","groups":[{"id":"2.1","firstName":"Service 80","lastName":"Service 90","count":11}],"canGoBack":true,"isCurrentPinned":true,"canPinCurrent":true}"#
        let decoded = try JSONDecoder().decode(MobileProviderPicker.self, from: Data(wire.utf8))
        XCTAssertEqual(decoded.groups?.first?.id, "2.1")
        XCTAssertEqual(decoded.groups?.first?.count, 11)
        XCTAssertEqual(try JSONDecoder().decode(MobileProviderPicker.self, from: JSONEncoder().encode(decoded)), decoded)
        let legacy = try JSONDecoder().decode(MobileProviderPicker.self, from: Data(#"{"isOpen":true,"page":0,"pageCount":1,"options":[{"id":"codex","name":"Codex"}]}"#.utf8))
        XCTAssertNil(legacy.mode); XCTAssertNil(legacy.options.first?.isPinned)
        let request = MobileNavigation(axis: "provider-group", direction: 1, expectedRevision: 8, groupID: "2.1", pickerVersion: 2)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(request)) as? [String: Any])
        XCTAssertEqual(json["groupID"] as? String, "2.1"); XCTAssertEqual(json["pickerVersion"] as? Int, 2)
        XCTAssertNil(json["providerID"])
    }

    @MainActor
    func testIslandReadingKeepsTheTaskTitleAndSuppressesQuotaWhileOffline() throws {
        var state = MobileActivityState.disconnected
        state.connection = "connected"; state.staleAt = Date.distantFuture.timeIntervalSince1970
        state.providers = [.init(id: "sample", name: "Sample", state: "needsAuth", windows: [],
                                 todayTokens: nil, localState: "unavailable", updatedAt: nil)]
        state.sessions = [.init(providerID: "sample", phase: .working, title: "합성 👩‍💻", since: nil)]
        let needsSignIn = IslandReading(state: state, stale: false)
        XCTAssertEqual(needsSignIn.statusTitle, "Sign in required")
        XCTAssertEqual(needsSignIn.taskTitle, "합성 👩‍💻")
        state.waitingCount = 1
        state.workingCount = 2
        state.unavailableCount = 3
        state.providers[0].state = "partial"
        state.providers[0].windows = [.init(name: "Primary", remainingPercent: -0.0, resetsAt: nil),
                                     .init(name: "Secondary", remainingPercent: 125, resetsAt: nil)]
        let working = IslandReading(state: state, stale: false)
        XCTAssertEqual(working.statusTitle, "Needs input")
        XCTAssertEqual(try XCTUnwrap(working.remaining).bitPattern, (-0.0 as Double).bitPattern)
        XCTAssertEqual(working.secondaryRemaining, 100)
        let offline = IslandReading(state: state, stale: true)
        XCTAssertEqual(offline.statusSymbol, "wifi.slash")
        XCTAssertEqual(offline.taskTitle, "Check your computer’s connection")
        XCTAssertNil(offline.secondaryWindow)
        XCTAssertNil(offline.remaining)
    }

    @MainActor
    func testPickerDoesNotAdvertiseLiveWorkAfterItsActivityStales() {
        var state = MobileActivityState.disconnected
        state.connection = "connected"; state.staleAt = Date().addingTimeInterval(60).timeIntervalSince1970
        let option = MobileProviderOption(id: "codex", name: "Codex", isPinned: true, phase: "waiting")
        let picker = MobileProviderPicker(isOpen: true, page: 0, pageCount: 1, options: [option], mode: "quick")
        XCTAssertEqual(IslandQuickPicker(state: state, picker: picker).activityPhase(for: option), "waiting")
        XCTAssertNil(IslandQuickPicker(state: state, picker: picker, stale: true).activityPhase(for: option))
        state.staleAt = Date().addingTimeInterval(-1).timeIntervalSince1970
        XCTAssertNil(IslandQuickPicker(state: state, picker: picker).activityPhase(for: option))
        state.staleAt = Date().addingTimeInterval(60).timeIntervalSince1970; state.connection = "offline"
        XCTAssertNil(IslandQuickPicker(state: state, picker: picker).activityPhase(for: option))
        XCTAssertEqual(picker.options.first?.isPinned, true)
    }

}
