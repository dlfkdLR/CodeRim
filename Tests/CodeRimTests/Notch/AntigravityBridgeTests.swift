import XCTest
@testable import CodeRim

final class NotchAntigravityBridgeTests: XCTestCase {
    /// Verbatim from the running language server.
    private let real = Data("""
    {"response":{"groups":[
      {"displayName":"Gemini Models",
       "buckets":[{"bucketId":"gemini-weekly","displayName":"Weekly Limit Remaining",
                   "window":"weekly","remainingFraction":0.96262,
                   "resetTime":"2026-09-07T14:12:34Z"}]},
      {"displayName":"Claude and GPT models",
       "buckets":[{"bucketId":"3p-weekly","displayName":"Weekly Limit Remaining",
                   "window":"weekly","remainingFraction":1,
                   "resetTime":"2026-09-08T09:12:10Z"}]}]}}
    """.utf8)

    func testRemainingIsTurnedIntoUsed() {
        let windows = AntigravityBridge.windows(in: real)
        XCTAssertEqual(windows.count, 2)
        XCTAssertEqual(windows[0].id, "gemini-weekly")
        XCTAssertEqual(windows[0].usedFraction ?? 0, 1 - 0.96262, accuracy: 0.00001)
        XCTAssertEqual(windows[0].label, "Weekly Limit")
        XCTAssertEqual(windows[0].group, "Gemini Models")
        XCTAssertEqual(windows[0].duration, 7 * 86400)
    }

    func testAnUntouchedLimitIsZeroUsed() {
        XCTAssertEqual(AntigravityBridge.windows(in: real)[1].usedFraction, 0)
    }

    func testItKeepsTheResetTime() throws {
        let resets = try XCTUnwrap(AntigravityBridge.windows(in: real)[0].resetsAt)
        XCTAssertEqual(resets, ISO8601DateFormatter().date(from: "2026-09-07T14:12:34Z"))
    }

    func testImpossibleFractionsAreDropped() {
        let wild = Data(#"{"response":{"groups":[{"displayName":"G","buckets":[{"bucketId":"a","remainingFraction":1.4},{"bucketId":"b","remainingFraction":-0.2}]}]}}"#.utf8)
        XCTAssertTrue(AntigravityBridge.windows(in: wild).isEmpty)
    }

    func testAnUnfamiliarShapeYieldsNothing() {
        XCTAssertTrue(AntigravityBridge.windows(in: Data(#"{"other":1}"#.utf8)).isEmpty)
        XCTAssertTrue(AntigravityBridge.windows(in: Data("nonsense".utf8)).isEmpty)
    }

    // MARK: - Discovery

    func testItReadsTheTokenFromTheProcessTable() throws {
        let table = """
        29283 /Applications/Antigravity.app/Contents/Resources/bin/language_server --standalone \
        --csrf_token d4bd9204-bf02-4111-b1fe-71f0d0d921d0 --app_data_dir antigravity
        """
        let endpoint = try XCTUnwrap(
            AntigravityBridge.discover(processTable: table, listeningPorts: { pid in
                XCTAssertEqual(pid, 29283)
                return [63881, 63882]
            })
        )
        XCTAssertEqual(endpoint.csrfToken, "d4bd9204-bf02-4111-b1fe-71f0d0d921d0")
        XCTAssertEqual(endpoint.ports, [63881, 63882])
    }

    func testNoAntigravityMeansNoEndpoint() {
        XCTAssertNil(AntigravityBridge.discover(processTable: "1 /sbin/launchd",
                                                listeningPorts: { _ in [] }))
    }

    func testNoPortMeansNoEndpoint() {
        let table = "1 language_server --csrf_token abc"
        XCTAssertNil(AntigravityBridge.discover(processTable: table, listeningPorts: { _ in [] }))
    }

    func testTheCLIIsFoundAndAsksForNoToken() throws {
        let table = """
        1 /sbin/launchd
        34221 agy
        """
        let endpoint = try XCTUnwrap(
            AntigravityBridge.discover(processTable: table, listeningPorts: { pid in
                XCTAssertEqual(pid, 34221)
                return [54166, 54167]
            })
        )
        XCTAssertNil(endpoint.csrfToken, "the CLI serves this without one")
        XCTAssertEqual(endpoint.ports, [54166, 54167])
    }

    func testTheCLIIsFoundByItsFullPathToo() throws {
        let table = "700 /Users/someone/.local/bin/agy"
        let endpoint = try XCTUnwrap(
            AntigravityBridge.discover(processTable: table, listeningPorts: { _ in [9000] })
        )
        XCTAssertNil(endpoint.csrfToken)
    }

    func testTheIDEWinsWhenBothAreRunning() throws {
        let table = """
        29283 /Applications/Antigravity.app/Contents/Resources/bin/language_server --csrf_token abc
        34221 agy
        """
        let endpoint = try XCTUnwrap(
            AntigravityBridge.discover(processTable: table, listeningPorts: { _ in [1] })
        )
        XCTAssertEqual(endpoint.csrfToken, "abc")
    }

    func testPortsAreParsedFromLSOF() {
        let output = """
        language_ 29283 me   34u  IPv4 0x1  0t0  TCP 127.0.0.1:63881 (LISTEN)
        language_ 29283 me   35u  IPv6 0x2  0t0  TCP [::1]:63882 (LISTEN)
        """
        XCTAssertEqual(AntigravityBridge.parsePorts(fromLSOF: output), [63881, 63882])
    }

    // MARK: - Provider

    @MainActor
    func testProviderIsHiddenWithoutARunningAntigravity() async {
        let provider = AntigravityNotchProvider(discover: { nil })
        XCTAssertEqual(provider.id, "gemini")
        XCTAssertFalse(provider.isVisibleWhenAbsent)
        do {
            _ = try await provider.fetchSnapshot()
            XCTFail("expected needsAuth")
        } catch NotchProviderError.needsAuth {
        } catch {
            XCTFail("expected needsAuth, got \(error)")
        }
    }
}
