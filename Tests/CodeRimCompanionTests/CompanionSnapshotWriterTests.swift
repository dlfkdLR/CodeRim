import Foundation
import XCTest
import CodeRimShared

final class CompanionSnapshotWriterTests: XCTestCase {
    func testBlockedWidgetWriteDoesNotBlockCLIAndCoalescesToNewestSnapshot() async {
        let entered = expectation(description: "Widget write entered")
        let cliFinished = expectation(description: "Independent CLI export completed")
        let newestFinished = expectation(description: "Newest widget export completed")
        let release = DispatchSemaphore(value: 0)
        let recorder = SnapshotWriteRecorder()
        let widget = CompanionSnapshotWriter(label: "test.widget") { snapshot in
            XCTAssertFalse(Thread.isMainThread)
            let marker = snapshot.providers[0].limits.message!
            recorder.append(marker)
            if marker == "first" {
                entered.fulfill()
                XCTAssertEqual(release.wait(timeout: .now() + 5), .success)
            }
            if marker == "newest" { newestFinished.fulfill() }
        }
        let cli = CompanionSnapshotWriter(label: "test.cli") { _ in
            XCTAssertFalse(Thread.isMainThread)
            cliFinished.fulfill()
        }
        widget.submit(snapshot("first"))
        await fulfillment(of: [entered], timeout: 2)
        // Submit runs synchronously on the main actor even while the destination is blocked.
        let superseded = snapshot("superseded")
        let newest = snapshot("newest")
        let cliValue = snapshot("cli")
        await MainActor.run {
            widget.submit(superseded)
            widget.submit(newest)
            cli.submit(cliValue)
        }
        await fulfillment(of: [cliFinished], timeout: 2)
        release.signal()
        await fulfillment(of: [newestFinished], timeout: 2)
        XCTAssertEqual(recorder.values, ["first", "newest"])
    }

    func testFailedDestinationCanRetryIdenticalSnapshot() async {
        let firstFailed = expectation(description: "First write failed")
        let retrySucceeded = expectation(description: "Same snapshot retried")
        let recorder = SnapshotWriteRecorder()
        let writer = CompanionSnapshotWriter(label: "test.retry", write: { _ in
            recorder.append("attempt")
            if recorder.values.count == 1 { throw CocoaError(.fileWriteNoPermission) }
        }, completion: { _, success in
            if success { retrySucceeded.fulfill() } else { firstFailed.fulfill() }
        })
        let value = snapshot("same")
        writer.submit(value)
        await fulfillment(of: [firstFailed], timeout: 2)
        writer.submit(value)
        await fulfillment(of: [retrySucceeded], timeout: 2)
        XCTAssertEqual(recorder.values.count, 2)
    }

    private func snapshot(_ marker: String) -> CompanionSnapshot {
        .init(providers: [.init(id: "openrouter", name: "OpenRouter", localUsage: nil,
            limits: .init(state: .ready, updatedAt: Date(timeIntervalSince1970: 1), windows: [], message: marker))])
    }
}

private final class SnapshotWriteRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [String] = []
    func append(_ value: String) { lock.withLock { storage.append(value) } }
    var values: [String] { lock.withLock { storage } }
}
