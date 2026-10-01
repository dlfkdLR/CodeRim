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
        XCTAssertEqual(MobilePairingLink(link.url.absoluteString), link)
        XCTAssertTrue(link.url.absoluteString.hasPrefix("coderim://pair?r=https://relay.example.workers.dev"))
    }

    func testPairingLinkRejectsAnythingThatIsNotAnHTTPSRelayAndOpaqueTokens() {
        let base = "coderim://pair?i=\(id)&s=\(secret)&r="
        XCTAssertNil(MobilePairingLink(base + "http://relay.example"))
        XCTAssertNil(MobilePairingLink(base + "https://relay.example/path"))
        XCTAssertNil(MobilePairingLink("https://pair?r=https://relay.example&i=\(id)&s=\(secret)"))
        XCTAssertNil(MobilePairingLink("coderim://pair?r=https://relay.example&i=short&s=\(secret)"))
        XCTAssertNil(MobilePairingLink("coderim://pair?r=https://relay.example&i=\(id)&s=\(secret)%20x"))
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
        let image = try XCTUnwrap(MobileSettingsSection.qrImage(link.url.absoluteString))
        let cgImage = try XCTUnwrap(image.cgImage(forProposedRect: nil, context: nil, hints: nil))
        let detector = try XCTUnwrap(CIDetector(ofType: CIDetectorTypeQRCode, context: nil, options: [CIDetectorAccuracy: CIDetectorAccuracyHigh]))
        let decoded = (detector.features(in: CIImage(cgImage: cgImage)) as? [CIQRCodeFeature])?.first?.messageString
        XCTAssertEqual(decoded, link.url.absoluteString)
        XCTAssertEqual(decoded.flatMap(MobilePairingLink.init), link)
    }
}
