import AppKit
import ServiceManagement
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class LaunchAtLoginServiceTests: XCTestCase {
    func testInitializationAndPresentationDoNotReadSystemStatus() {
        let backend = LoginBackend(status: .notRegistered)
        let service = LaunchAtLoginService(client: backend.client)

        XCTAssertNil(service.status)
        XCTAssertEqual(service.statusText, "Checking…")
        XCTAssertFalse(service.isEnabled)
        XCTAssertFalse(service.isBusy)
        XCTAssertFalse(service.canSetEnabled)
        XCTAssertNil(service.setEnabled(true), "Cannot change an unknown registration state")
        XCTAssertEqual(backend.readCount, 0)
        XCTAssertEqual(backend.mutations, [])
    }

    func testSlowStatusReadLeavesMainActorResponsive() async {
        let backend = LoginBackend(status: .notRegistered)
        let gate = LoginBackendGate()
        backend.blockReads(with: gate)
        defer { gate.release() }
        let service = LaunchAtLoginService(client: backend.client)

        let operation = service.refresh()
        let entered = await gate.waitUntilEntered()
        XCTAssertTrue(entered, "Background status read must start")
        // This continuation runs on the main actor while the synchronous read
        // is still waiting. A main-actor read would instead exhaust the gate.
        XCTAssertTrue(Thread.isMainThread)
        XCTAssertFalse(gate.didTimeOut, "Status IPC blocked the main actor")
        XCTAssertTrue(service.isBusy)
        XCTAssertNil(service.status)
        XCTAssertFalse(service.canSetEnabled)
        XCTAssertFalse(backend.usedMainThread)

        gate.release()
        await operation.value
        XCTAssertEqual(service.status, .notRegistered)
        XCTAssertEqual(service.statusText, "Off")
        XCTAssertFalse(service.isBusy)
        XCTAssertTrue(service.canSetEnabled)
    }

    func testConcurrentRefreshesShareOneReadAndLaterRefreshCanRun() async {
        let backend = LoginBackend(status: .enabled)
        let gate = LoginBackendGate()
        backend.blockReads(with: gate)
        defer { gate.release() }
        let service = LaunchAtLoginService(client: backend.client)

        let operations = (0..<10).map { _ in service.refresh() }
        let entered = await gate.waitUntilEntered()
        XCTAssertTrue(entered)
        XCTAssertEqual(backend.readCount, 1)
        XCTAssertFalse(gate.didTimeOut)

        backend.blockReads(with: nil)
        gate.release()
        for operation in operations { await operation.value }
        XCTAssertEqual(backend.readCount, 1)
        XCTAssertEqual(service.status, .enabled)

        await service.refresh().value
        XCTAssertEqual(backend.readCount, 2, "Completion must clear the in-flight operation")
    }

    func testPendingMutationKeepsCachedStateAndRejectsDuplicateChanges() async throws {
        let backend = LoginBackend(status: .notRegistered)
        let service = LaunchAtLoginService(client: backend.client)
        await service.refresh().value
        let gate = LoginBackendGate()
        backend.blockMutations(with: gate)
        defer { gate.release() }
        backend.configureMutation(status: .enabled)

        let operation = try XCTUnwrap(service.setEnabled(true))
        XCTAssertTrue(service.isBusy)
        XCTAssertFalse(service.canSetEnabled)
        XCTAssertNil(service.setEnabled(false))
        XCTAssertNil(service.setEnabled(true))
        let refreshDuringMutation = service.refresh()
        let entered = await gate.waitUntilEntered()
        XCTAssertTrue(entered)
        XCTAssertFalse(gate.didTimeOut, "Registration must not block the main actor")
        XCTAssertEqual(service.status, .notRegistered)
        XCTAssertFalse(service.isEnabled, "Do not optimistically claim registration succeeded")
        XCTAssertEqual(backend.readCount, 1)
        XCTAssertEqual(backend.mutations, [true])
        XCTAssertFalse(backend.usedMainThread)

        gate.release()
        await operation.value
        await refreshDuringMutation.value
        XCTAssertEqual(backend.readCount, 2, "Mutation and its final status read form one operation")
        XCTAssertEqual(backend.mutations, [true])
        XCTAssertEqual(service.status, .enabled)
        XCTAssertTrue(service.isEnabled)
        XCTAssertTrue(service.canSetEnabled)
        XCTAssertFalse(service.isBusy)
    }

    func testRegistrationRequiringApprovalUsesActualSystemStatus() async throws {
        let backend = LoginBackend(status: .notRegistered)
        let service = LaunchAtLoginService(client: backend.client)
        await service.refresh().value
        backend.configureMutation(status: .requiresApproval)

        let operation = try XCTUnwrap(service.setEnabled(true))
        await operation.value

        XCTAssertEqual(service.status, .requiresApproval)
        XCTAssertEqual(service.statusText, "Approval required")
        XCTAssertFalse(service.isEnabled)
        XCTAssertNil(service.errorMessage)
        XCTAssertEqual(backend.readCount, 2)
    }

    func testFailedMutationStillReadsActualStatusAndAllowsSuccessfulRetry() async throws {
        let backend = LoginBackend(status: .enabled)
        let service = LaunchAtLoginService(client: backend.client)
        await service.refresh().value
        backend.configureMutation(status: .requiresApproval, shouldFail: true)

        let failedOperation = try XCTUnwrap(service.setEnabled(false))
        await failedOperation.value
        XCTAssertEqual(service.status, .requiresApproval)
        XCTAssertEqual(service.errorMessage, "Test registration failure")
        XCTAssertFalse(service.isBusy)
        XCTAssertTrue(service.canSetEnabled)
        XCTAssertEqual(backend.readCount, 2, "A thrown operation can still have changed system state")

        backend.configureMutation(status: .enabled)
        let retry = try XCTUnwrap(service.setEnabled(true))
        XCTAssertNil(service.errorMessage, "Retry should dismiss the previous error")
        await retry.value
        XCTAssertEqual(service.status, .enabled)
        XCTAssertNil(service.errorMessage)
        XCTAssertEqual(backend.mutations, [false, true])
        XCTAssertEqual(backend.readCount, 3)
    }

    func testRefreshRetainsCachedStatusUntilNewResultArrives() async {
        let backend = LoginBackend(status: .enabled)
        let service = LaunchAtLoginService(client: backend.client)
        await service.refresh().value
        let gate = LoginBackendGate()
        backend.changeStatus(to: .notRegistered)
        backend.blockReads(with: gate)
        defer { gate.release() }

        let operation = service.refresh()
        let entered = await gate.waitUntilEntered()
        XCTAssertTrue(entered)
        XCTAssertEqual(service.status, .enabled)
        XCTAssertEqual(service.statusText, "Enabled")
        XCTAssertTrue(service.isEnabled)
        XCTAssertTrue(service.isBusy)
        XCTAssertFalse(service.canSetEnabled)

        gate.release()
        await operation.value
        XCTAssertEqual(service.status, .notRegistered)
        XCTAssertEqual(service.statusText, "Off")
        XCTAssertFalse(service.isEnabled)
    }

    func testGeneralPaneCanLayOutWhileLoginStatusReadIsBlocked() async throws {
        _ = NSApplication.shared
        let backend = LoginBackend(status: .notRegistered)
        let gate = LoginBackendGate()
        backend.blockReads(with: gate)
        defer { gate.release() }
        let service = LaunchAtLoginService(client: backend.client)
        let operation = service.refresh()
        let entered = await gate.waitUntilEntered()
        XCTAssertTrue(entered)

        let layoutStart = ProcessInfo.processInfo.systemUptime
        let size = NSSize(width: 579, height: 560)
        let host = NSHostingView(rootView: GeneralSettingsView(launchAtLogin: service)
            .frame(width: size.width, height: size.height)
            .environment(\.colorScheme, .light))
        host.sizingOptions = []
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: .aqua)
        window.backgroundColor = .windowBackgroundColor
        window.contentView = host
        host.frame = NSRect(origin: .zero, size: size)
        for _ in 0..<3 { host.layoutSubtreeIfNeeded() }
        let layoutDuration = ProcessInfo.processInfo.systemUptime - layoutStart
        print("General pane host construction and layout while status is pending: \(String(format: "%.3f", layoutDuration)) seconds")

        XCTAssertFalse(gate.didTimeOut, "General must render before synchronous IPC finishes")
        XCTAssertTrue(service.isBusy)
        XCTAssertNil(service.status)
        XCTAssertEqual(backend.readCount, 1, "Pane appearance must share the pending status read")
        XCTAssertFalse(host.subviews.isEmpty)
        for scroll in scrollViews(in: host) {
            XCTAssertLessThanOrEqual(scroll.documentView?.bounds.width ?? 0,
                                     scroll.contentSize.width + 1)
        }

        if let capturePath = ProcessInfo.processInfo.environment["CODERIM_LAYOUT_CAPTURE_DIR"] {
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            let directory = URL(fileURLWithPath: capturePath, isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            try png.write(to: directory.appendingPathComponent("settings-general-checking-light.png"))
            XCTAssertFalse(gate.didTimeOut, "Pending-status capture must precede backend completion")
        }

        backend.blockReads(with: nil)
        gate.release()
        await operation.value
        host.layoutSubtreeIfNeeded()
        XCTAssertEqual(service.statusText, "Off")
        window.contentView = nil
    }

    private func scrollViews(in view: NSView) -> [NSScrollView] {
        ((view as? NSScrollView).map { [$0] } ?? []) + view.subviews.flatMap { scrollViews(in: $0) }
    }
}

/// All waits are bounded so a regression produces a failure, never a hung suite.
private final class LoginBackendGate: @unchecked Sendable {
    private let entered = DispatchSemaphore(value: 0)
    private let resume = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var timedOut = false

    var didTimeOut: Bool { lock.withLock { timedOut } }

    func block() {
        entered.signal()
        if resume.wait(timeout: .now() + 5) == .timedOut {
            lock.withLock { timedOut = true }
        }
    }

    func waitUntilEntered() async -> Bool {
        await withCheckedContinuation { continuation in
            DispatchQueue.global().async { [self] in
                continuation.resume(returning: entered.wait(timeout: .now() + 2) == .success)
            }
        }
    }

    func release() { resume.signal() }
}

private final class LoginBackend: @unchecked Sendable {
    private let lock = NSLock()
    private var currentStatus: SMAppService.Status
    private var resultStatus: SMAppService.Status
    private var mutationShouldFail = false
    private var readGate: LoginBackendGate?
    private var mutationGate: LoginBackendGate?
    private var reads = 0
    private var requestedValues: [Bool] = []
    private var ranOnMainThread = false

    init(status: SMAppService.Status) {
        currentStatus = status
        resultStatus = status
    }

    var client: LaunchAtLoginClient {
        LaunchAtLoginClient(status: { self.readStatus() }, setEnabled: { try self.setEnabled($0) })
    }

    var readCount: Int { lock.withLock { reads } }
    var mutations: [Bool] { lock.withLock { requestedValues } }
    var usedMainThread: Bool { lock.withLock { ranOnMainThread } }

    func blockReads(with gate: LoginBackendGate?) { lock.withLock { readGate = gate } }
    func blockMutations(with gate: LoginBackendGate?) { lock.withLock { mutationGate = gate } }
    func changeStatus(to status: SMAppService.Status) { lock.withLock { currentStatus = status } }

    func configureMutation(status: SMAppService.Status, shouldFail: Bool = false) {
        lock.withLock {
            resultStatus = status
            mutationShouldFail = shouldFail
        }
    }

    private func readStatus() -> SMAppService.Status {
        let gate = lock.withLock {
            reads += 1
            ranOnMainThread = ranOnMainThread || Thread.isMainThread
            return readGate
        }
        gate?.block()
        return lock.withLock { currentStatus }
    }

    private func setEnabled(_ enabled: Bool) throws {
        let gate = lock.withLock {
            requestedValues.append(enabled)
            ranOnMainThread = ranOnMainThread || Thread.isMainThread
            return mutationGate
        }
        gate?.block()
        let shouldFail = lock.withLock {
            currentStatus = resultStatus
            return mutationShouldFail
        }
        if shouldFail { throw TestFailure() }
    }

    private struct TestFailure: LocalizedError {
        var errorDescription: String? { "Test registration failure" }
    }
}
