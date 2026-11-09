import Combine
import XCTest
@testable import CodeRim

@MainActor
final class NotchActivityPollingTests: XCTestCase {
    func testHiddenOrRemovedBusyMonitorsUseTheIdleQuotaCadence() {
        for provider in ["claude", "codex"] {
            let monitor = RetainedActivityMonitor()
            let monitors: [String: any AgentActivityMonitor] = [provider: monitor]
            var visible = true
            var selected: Set<String> = [provider]
            let isBusy = {
                NotchController.hasBusyMonitors(visible: visible, selectedProviderIDs: selected, monitors: monitors)
            }
            monitor.start()
            XCTAssertTrue(isBusy())
            XCTAssertTrue(NotchUsageStore.shouldRefresh(isBusy: isBusy(), sinceLastAttempt: 60, idleInterval: 300))

            monitor.stop()
            visible = false
            XCTAssertEqual(monitor.sessions.first?.state, .busy, "Stopping retains the last published session")
            XCTAssertFalse(isBusy())
            XCTAssertFalse(NotchUsageStore.shouldRefresh(isBusy: isBusy(), sinceLastAttempt: 60, idleInterval: 300))
            XCTAssertTrue(NotchUsageStore.shouldRefresh(isBusy: isBusy(), sinceLastAttempt: 300, idleInterval: 300))

            visible = true
            selected.remove(provider)
            XCTAssertFalse(isBusy())
            XCTAssertFalse(NotchUsageStore.shouldRefresh(isBusy: isBusy(), sinceLastAttempt: 60, idleInterval: 300))
            selected.insert(provider)
            monitor.start()
            XCTAssertTrue(isBusy())
        }
    }

    private final class RetainedActivityMonitor: AgentActivityMonitor {
        let sessions = [AgentSession(id: "busy", name: "synthetic", detail: "", state: .busy,
                                     waitingFor: nil, since: Date(timeIntervalSince1970: 0))]
        var sessionsPublisher: AnyPublisher<[AgentSession], Never> { Just(sessions).eraseToAnyPublisher() }
        func start() {}
        func stop() {}
    }
}
