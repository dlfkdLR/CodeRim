import Darwin
import XCTest
@testable import CodeRim

final class NotchAntigravityBridgeTests: XCTestCase {
    private func addresses(_ ports: [Int]) -> [LocalTCPPeer.Address] {
        ports.map { .init(host: "127.0.0.1", port: $0) }
    }

    private func identity(_ pid: pid_t) -> LocalTCPPeer.ProcessIdentity {
        .init(pid: pid, userID: getuid(), startedSeconds: 1, startedMicroseconds: 2)
    }

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
            AntigravityBridge.discover(processTable: table, listeningAddresses: { pid in
                XCTAssertEqual(pid, 29283)
                return self.addresses([63881, 63882])
            }, processIdentity: identity)
        )
        XCTAssertEqual(endpoint.csrfToken, "d4bd9204-bf02-4111-b1fe-71f0d0d921d0")
        XCTAssertEqual(endpoint.listeners, addresses([63881, 63882]))
    }

    func testNoAntigravityMeansNoEndpoint() {
        XCTAssertNil(AntigravityBridge.discover(processTable: "1 /sbin/launchd",
                                                listeningAddresses: { _ in [] }, processIdentity: identity))
    }

    func testNoPortMeansNoEndpoint() {
        let table = "1 language_server --csrf_token abc"
        XCTAssertNil(AntigravityBridge.discover(processTable: table, listeningAddresses: { _ in [] }, processIdentity: identity))
    }

    func testTheCLIIsFoundAndAsksForNoToken() throws {
        let table = """
        1 /sbin/launchd
        34221 agy
        """
        let endpoint = try XCTUnwrap(
            AntigravityBridge.discover(processTable: table, listeningAddresses: { pid in
                XCTAssertEqual(pid, 34221)
                return self.addresses([54166, 54167])
            }, processIdentity: identity)
        )
        XCTAssertNil(endpoint.csrfToken, "the CLI serves this without one")
        XCTAssertEqual(endpoint.listeners, addresses([54166, 54167]))
    }

    func testTheCLIIsFoundByItsFullPathToo() throws {
        let table = "700 /Users/someone/.local/bin/agy"
        let endpoint = try XCTUnwrap(
            AntigravityBridge.discover(processTable: table, listeningAddresses: { _ in self.addresses([9000]) }, processIdentity: identity)
        )
        XCTAssertNil(endpoint.csrfToken)
    }

    func testTheIDEWinsWhenBothAreRunning() throws {
        let table = """
        29283 /Applications/Antigravity.app/Contents/Resources/bin/language_server --csrf_token abc
        34221 agy
        """
        let endpoint = try XCTUnwrap(
            AntigravityBridge.discover(processTable: table, listeningAddresses: { _ in self.addresses([1]) }, processIdentity: identity)
        )
        XCTAssertEqual(endpoint.csrfToken, "abc")
    }

    func testIPv6ListenerIsPreserved() throws {
        let listener = LocalTCPPeer.Address(host: "::1", port: 63882)
        let endpoint = try XCTUnwrap(AntigravityBridge.discover(
            processTable: "29283 language_server --csrf_token dummy-token",
            listeningAddresses: { _ in [listener] }, processIdentity: identity))
        XCTAssertEqual(endpoint.listeners, [listener])
        XCTAssertEqual(endpoint.listeners[0].authority, "[::1]:63882")
        XCTAssertEqual(endpoint.process, identity(29283))
    }

    func testDiscoveryRejectsUnknownOrDifferentUserProcess() {
        let table = "29283 language_server --csrf_token dummy-token"
        XCTAssertNil(AntigravityBridge.discover(processTable: table,
            listeningAddresses: { _ in self.addresses([63881]) }, processIdentity: { _ in nil }))
        XCTAssertNil(AntigravityBridge.discover(processTable: table,
            listeningAddresses: { _ in self.addresses([63881]) }, processIdentity: { pid in
                .init(pid: pid, userID: getuid() + 1, startedSeconds: 1, startedMicroseconds: 2)
            }))
    }

    func testDiscoveryRejectsProcessReplacementAndNonLoopbackListener() {
        var reads = 0
        XCTAssertNil(AntigravityBridge.discover(
            processTable: "29283 language_server --csrf_token dummy-token",
            listeningAddresses: { _ in self.addresses([63881]) }, processIdentity: { pid in
                reads += 1
                return .init(pid: pid, userID: getuid(), startedSeconds: UInt64(reads), startedMicroseconds: 0)
            }))
        XCTAssertNil(AntigravityBridge.discover(
            processTable: "29283 language_server --csrf_token dummy-token",
            listeningAddresses: { _ in [.init(host: "0.0.0.0", port: 63881)] }, processIdentity: identity))
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
