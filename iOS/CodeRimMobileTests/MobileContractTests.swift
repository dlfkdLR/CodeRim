import XCTest
@testable import CodeRimMobile

final class MobileContractTests: XCTestCase {
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
