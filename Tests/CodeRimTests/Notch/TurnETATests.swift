import XCTest
@testable import CodeRim

final class TurnETATests: XCTestCase {
    private let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    private func at(_ seconds: TimeInterval) -> Date { t0.addingTimeInterval(seconds) }
    /// 20 turns between 60 and 250 seconds.
    private let history = (0..<20).map { 60.0 + Double($0) * 10 }

    func testRemainingConditionsOnSurvival() {
        let early = TurnETAEstimator.estimate(elapsed: 0, projectDurations: history, agentDurations: [])
        let later = TurnETAEstimator.estimate(elapsed: 100, projectDurations: history, agentDurations: [])
        XCTAssertEqual(early.basis, .project)
        // At 100s only the turns longer than 100s remain in play.
        XCTAssertEqual(later.median, TurnETAEstimator.quantile(
            history.filter { $0 > 100 }.map { $0 - 100 }, 0.5), accuracy: 0.001)
        XCTAssertLessThanOrEqual(later.low, later.median)
        XCTAssertLessThanOrEqual(later.median, later.high)
    }

    func testFallsBackFromProjectToAgentToPrior() {
        XCTAssertEqual(TurnETAEstimator.estimate(elapsed: 0, projectDurations: [30, 40],
                                                 agentDurations: history).basis, .agent)
        XCTAssertEqual(TurnETAEstimator.estimate(elapsed: 0, projectDurations: [],
                                                 agentDurations: []).basis, .prior)
    }

    func testPastEverythingOnRecordScalesWithElapsed() {
        let estimate = TurnETAEstimator.estimate(elapsed: 10_000, projectDurations: history,
                                                 agentDurations: history)
        XCTAssertEqual(estimate.basis, .overrun)
        XCTAssertEqual(estimate.median, 5_000, accuracy: 0.001)
    }

    func testWaitingStopsTheClockAndHoldsTheBar() {
        var tracker = TurnETATracker()
        _ = tracker.update(phase: .working, now: at(0), projectDurations: history, agentDurations: [])
        let before = tracker.update(phase: .working, now: at(8), projectDurations: history, agentDurations: [])
        let waiting = tracker.update(phase: .waiting, now: at(9), projectDurations: history, agentDurations: [])
        XCTAssertNil(waiting.estimate)
        XCTAssertEqual(waiting.progress, before.progress)
        // A long wait adds nothing once work resumes.
        _ = tracker.update(phase: .waiting, now: at(3_000), projectDurations: history, agentDurations: [])
        let resumed = tracker.update(phase: .working, now: at(3_002), projectDurations: history, agentDurations: [])
        XCTAssertLessThan(resumed.elapsed, 30)
    }

    func testAsleepGapIsNotCountedAsWork() {
        var tracker = TurnETATracker()
        _ = tracker.update(phase: .working, now: at(0), projectDurations: history, agentDurations: [])
        let after = tracker.update(phase: .working, now: at(7_200), projectDurations: history, agentDurations: [])
        XCTAssertEqual(after.elapsed, TurnETATracker.tickCeiling, accuracy: 0.001)
    }

    func testCompactionShowsNoEstimate() {
        var tracker = TurnETATracker()
        _ = tracker.update(phase: .working, now: at(0), projectDurations: history, agentDurations: [])
        XCTAssertNil(tracker.update(phase: .compacting, now: at(2), projectDurations: history,
                                    agentDurations: []).estimate)
    }

    func testBarNeverGoesBackwards() {
        var tracker = TurnETATracker()
        var last = 0.0
        for second in stride(from: 0.0, through: 400, by: 2) {
            let snapshot = tracker.update(phase: .working, now: at(second),
                                          projectDurations: history, agentDurations: [])
            XCTAssertGreaterThanOrEqual(snapshot.progress, last)
            XCTAssertLessThanOrEqual(snapshot.progress, TurnETATracker.progressCeiling)
            last = snapshot.progress
        }
    }

    func testFollowUpWidensTheEstimateButNotTheBar() {
        var tracker = TurnETATracker()
        var before = tracker.update(phase: .working, now: at(0), projectDurations: history, agentDurations: [])
        for second in stride(from: 2.0, through: 90, by: 2) {
            before = tracker.update(phase: .working, now: at(second), projectDurations: history, agentDurations: [])
        }
        tracker.noteFollowUp()
        let after = tracker.update(phase: .working, now: at(92), projectDurations: history, agentDurations: [])
        XCTAssertTrue(after.extended)
        XCTAssertGreaterThan(after.estimate!.median, before.estimate!.median)
        XCTAssertGreaterThanOrEqual(after.progress, before.progress)
    }

    func testFinishedFillsTheBar() {
        var tracker = TurnETATracker()
        _ = tracker.update(phase: .working, now: at(0), projectDurations: history, agentDurations: [])
        XCTAssertEqual(tracker.update(phase: .finished, now: at(5), projectDurations: history,
                                      agentDurations: []).progress, 1)
    }

    func testHistoryIgnoresImplausibleAndDuplicateTurns() {
        var turns = TurnHistory()
        turns.record(agent: "claude", project: "p", seconds: 1, at: t0)
        turns.record(agent: "claude", project: "p", seconds: .nan, at: t0)
        turns.record(agent: "claude", project: "p", seconds: 100_000, at: t0)
        XCTAssertTrue(turns.durations(agent: "claude").isEmpty)
        let sample = TurnHistory.Sample(seconds: 90, project: "p", recordedAt: t0)
        turns.absorb(agent: "claude", [sample, sample])
        turns.absorb(agent: "claude", [sample])
        XCTAssertEqual(turns.durations(agent: "claude"), [90])
        XCTAssertTrue(turns.durations(agent: "claude", project: "other").isEmpty)
    }
}
