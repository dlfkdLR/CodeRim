import CoreImage
import AppKit
import CodeRimShared
import XCTest
@testable import CodeRim

@MainActor
final class MobilePairingTests: XCTestCase {
    private let id = String(repeating: "a", count: 43), secret = "Zq-_" + String(repeating: "9", count: 39)

    func testPairingLinkRoundTripsThroughItsQRText() throws {
        let link = MobilePairingLink(relay: URL(string: "https://relay.example.workers.dev")!, id: id, secret: secret)
        XCTAssertEqual(MobilePairingLink(link.qrPayload), link)
        XCTAssertTrue(link.qrPayload.hasPrefix("CODERIM-PAIR-1\nhttps://relay.example.workers.dev\n"))
        XCTAssertNil(URLComponents(string: link.qrPayload)?.scheme)
        let trailingSlash = MobilePairingLink(relay: URL(string: "https://relay.example.workers.dev/")!, id: id, secret: secret)
        XCTAssertEqual(MobilePairingLink(trailingSlash.qrPayload), trailingSlash)
    }

    func testPairingLinkRejectsAnythingThatIsNotAnHTTPSRelayAndOpaqueTokens() {
        let base = "CODERIM-PAIR-1\n"
        for payload in [base + "http://relay.example\n\(id)\n\(secret)",
                        base + "https://relay.example/path\n\(id)\n\(secret)",
                        base + "https://name:pass@relay.example\n\(id)\n\(secret)",
                        base + "https://relay.example\nshort\n\(secret)",
                        base + "https://relay.example\n\(id)\n\(secret) x",
                        base + "https://relay.example\n\(id)\n\(secret)\nextra",
                        base + "https://relay.example\n\(id)\n\(secret)\r",
                        base + "https://relay.example\n\(id)\n" + String(repeating: "s", count: 513),
                        "coderim://pair?r=https://relay.example&i=\(id)&s=\(secret)"] {
            XCTAssertNil(MobilePairingLink(payload), payload)
        }
    }

    func testWindowsAndSwiftProduceTheSameAppOnlyEnvelopeFixture() throws {
        let path = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Fixtures/mobile-pairing-v1.txt")
        let fixture = try String(contentsOf: path, encoding: .utf8)
        let link = MobilePairingLink(relay: URL(string: "https://relay.example.com")!, id: id, secret: secret)
        XCTAssertEqual(link.qrPayload, fixture)
        XCTAssertEqual(MobilePairingLink(fixture), link)
    }

    func testOnlyChangesAndHeartbeatsAreSent() {
        let now = Date(timeIntervalSince1970: 1_790_000_000)
        let body = MobileSnapshot(generatedAt: now.timeIntervalSince1970, providers: [], sessions: [])
        XCTAssertTrue(MobileConnectionStore.shouldSend(body, after: nil, heartbeat: 300, now: now))
        var later = body; later.generatedAt += 60
        XCTAssertFalse(MobileConnectionStore.shouldSend(later, after: (body, now), heartbeat: 300, now: now + 60),
                       "a newer generation time alone is not a change")
        XCTAssertTrue(MobileConnectionStore.shouldSend(later, after: (body, now), heartbeat: 300, now: now + 300))
        var changed = later
        changed.sessions = [MobileSession(providerID: "codex", phase: .working, title: "", since: nil)]
        XCTAssertTrue(MobileConnectionStore.shouldSend(changed, after: (body, now), heartbeat: 300, now: now + 20))
    }
}

extension MobilePairingTests {
    /// The picture the Mac shows must decode back to exactly the link the iPhone expects.
    func testTheDrawnQRCodeDecodesBackToThePairingLink() throws {
        let link = MobilePairingLink(relay: URL(string: "https://coderim-relay.pages.dev")!, id: id, secret: secret)
        let image = try XCTUnwrap(MobileSettingsSection.qrImage(link.qrPayload))
        let cgImage = try XCTUnwrap(image.cgImage(forProposedRect: nil, context: nil, hints: nil))
        let detector = try XCTUnwrap(CIDetector(ofType: CIDetectorTypeQRCode, context: nil, options: [CIDetectorAccuracy: CIDetectorAccuracyHigh]))
        let decoded = (detector.features(in: CIImage(cgImage: cgImage)) as? [CIQRCodeFeature])?.first?.messageString
        XCTAssertEqual(decoded, link.qrPayload)
        XCTAssertEqual(decoded.flatMap(MobilePairingLink.init), link)
    }
}
