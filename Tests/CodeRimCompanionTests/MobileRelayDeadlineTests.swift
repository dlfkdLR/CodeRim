import Foundation
import XCTest
@testable import CodeRimShared

@MainActor
final class MobileRelayDeadlineTests: XCTestCase {
    func testOnlyPairPollsGetEnoughTimeForTheTwentySecondServerWait() async throws {
        let ordinary = MobileRelayClient.configuration(pairingPoll: false)
        let pairing = MobileRelayClient.configuration(pairingPoll: true)
        XCTAssertEqual(ordinary.timeoutIntervalForRequest, 15)
        XCTAssertEqual(ordinary.timeoutIntervalForResource, 20)
        XCTAssertEqual(pairing.timeoutIntervalForRequest, 30)
        XCTAssertEqual(pairing.timeoutIntervalForResource, 35)
        XCTAssertNil(pairing.httpCookieStorage)
        XCTAssertNil(pairing.urlCache)
        let client = try MobileRelayClient(endpoint: URL(string: "https://relay.example.com")!,
                                           protocolClasses: [PairingDeadlineProtocol.self])
        let normal: MobileOK = try await client.send("POST", "/v1/pairing/start", body: MobileEmpty())
        XCTAssertTrue(normal.ok)
        // A synthetic long poll completes at the server's full wait; its late claim must still decode.
        async let pending: MobilePairingPoll = client.send("POST", "/v1/pairing/poll",
            body: MobilePairingRequest(id: "pending", secret: "fixture"))
        async let claimed: MobilePairingPoll = client.send("POST", "/v1/pairing/poll",
            body: MobilePairingRequest(id: "claimed", secret: "fixture"))
        let (empty, result) = try await (pending, claimed)
        XCTAssertEqual(empty.pending, true)
        XCTAssertEqual(result.token, String(repeating: "t", count: 43))
        XCTAssertEqual(result.expiresAt, 1_900_000_000)
    }
}

/// URLProtocol keeps the fixture offline; this checks configured deadlines and delayed delivery,
/// not Cloudflare disconnect handling or signed native URL routing.
private final class PairingDeadlineProtocol: URLProtocol, @unchecked Sendable {
    private let lock = NSLock()
    private var response: DispatchWorkItem?
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let poll = request.url?.path == "/v1/pairing/poll"
        guard request.timeoutInterval == (poll ? 30 : 15) else {
            client?.urlProtocol(self, didFailWithError: URLError(.timedOut)); return
        }
        let body: String
        let delay: TimeInterval
        if poll {
            var data = request.httpBody ?? Data()
            if let stream = request.httpBodyStream {
                stream.open(); defer { stream.close() }
                var buffer = [UInt8](repeating: 0, count: 1024)
                while stream.hasBytesAvailable {
                    let count = stream.read(&buffer, maxLength: buffer.count)
                    guard count > 0 else { break }
                    data.append(contentsOf: buffer.prefix(count))
                }
            }
            let input = try? JSONDecoder().decode(MobilePairingRequest.self, from: data)
            delay = input?.id == "pending" ? 20 : 17
            body = input?.id == "pending" ? #"{"pending":true}"#
                : "{\"token\":\"\(String(repeating: "t", count: 43))\",\"expiresAt\":1900000000,\"deviceID\":\"fixture\"}"
        } else { body = #"{"ok":true}"#; delay = 0 }
        let work = DispatchWorkItem { [self] in
            let head = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            client?.urlProtocol(self, didReceive: head, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        }
        lock.withLock { response = work }
        DispatchQueue.global().asyncAfter(deadline: .now() + delay, execute: work)
    }
    override func stopLoading() { lock.withLock { response?.cancel() } }
}
