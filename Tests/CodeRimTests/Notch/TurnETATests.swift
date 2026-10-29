import XCTest
@testable import CodeRim

final class TurnETATests: XCTestCase {
    private let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    private func at(_ seconds: TimeInterval) -> Date { t0.addingTimeInterval(seconds) }
    private func sample(_ seconds: Double, _ project: String = "p") -> TurnHistory.Sample {
        TurnHistory.Sample(seconds: seconds, project: project, recordedAt: t0)
    }
    /// 20 turns between 60 and 250 seconds, all in project "p".
    private var samples: [TurnHistory.Sample] { (0..<20).map { sample(60.0 + Double($0) * 10) } }

    // MARK: - Estimator

    func testRemainingConditionsOnSurvival() {
        let early = TurnETAEstimator.estimate(elapsed: 0, samples: samples, project: "p")
        let later = TurnETAEstimator.estimate(elapsed: 100, samples: samples, project: "p")
        XCTAssertEqual(early.basis, .project)
        // At 100s only the turns longer than 100s remain in play.
        let remaining = samples.map(\.seconds).filter { $0 > 100 }.map { $0 - 100 }
        XCTAssertEqual(later.median, remaining.sorted()[remaining.count / 2 - 1], accuracy: 10.001)
        XCTAssertLessThanOrEqual(later.low, later.median)
        XCTAssertLessThanOrEqual(later.median, later.high)
    }

    func testOtherProjectsCountForLessThanThisOne() {
        // Ten quick turns here, ten slow turns elsewhere: the estimate leans quick.
        let here = (0..<10).map { _ in sample(30, "here") }
        let elsewhere = (0..<10).map { _ in sample(600, "elsewhere") }
        let estimate = TurnETAEstimator.estimate(elapsed: 0, samples: here + elsewhere, project: "here")
        XCTAssertEqual(estimate.median, 30)
        XCTAssertEqual(estimate.basis, .project)
    }

    func testFallsBackFromProjectToAgentToPrior() {
        let few = [sample(30, "here"), sample(40, "here")] + samples
        XCTAssertEqual(TurnETAEstimator.estimate(elapsed: 0, samples: few, project: "here").basis, .agent)
        XCTAssertEqual(TurnETAEstimator.estimate(elapsed: 0, samples: [], project: "here").basis, .prior)
    }

    func testPastEverythingOnRecordScalesWithElapsed() {
        let estimate = TurnETAEstimator.estimate(elapsed: 10_000, samples: samples, project: "p")
        XCTAssertEqual(estimate.basis, .overrun)
        XCTAssertEqual(estimate.median, 5_000, accuracy: 0.001)
    }

    func testEstimateIsOrderedAndFiniteForHostileInput() {
        for elapsed in [-5, 0, .infinity, .nan, 1e12] as [Double] {
            let estimate = TurnETAEstimator.estimate(elapsed: elapsed, samples: samples + [sample(.nan)], project: nil)
            XCTAssertTrue(estimate.low.isFinite && estimate.median.isFinite && estimate.high.isFinite)
            XCTAssertLessThanOrEqual(estimate.low, estimate.median)
            XCTAssertLessThanOrEqual(estimate.median, estimate.high)
        }
    }

    // MARK: - Tracker

    func testWaitingStopsTheClockAndHoldsTheBar() {
        var tracker = TurnETATracker()
        _ = tracker.update(phase: .working, now: at(0), samples: samples, project: "p")
        let before = tracker.update(phase: .working, now: at(8), samples: samples, project: "p")
        let waiting = tracker.update(phase: .waiting, now: at(9), samples: samples, project: "p")
        XCTAssertNil(waiting.estimate)
        XCTAssertEqual(waiting.progress, before.progress)
        // A long wait adds nothing once work resumes.
        _ = tracker.update(phase: .waiting, now: at(3_000), samples: samples, project: "p")
        let resumed = tracker.update(phase: .working, now: at(3_002), samples: samples, project: "p")
        XCTAssertLessThan(resumed.elapsed, 30)
    }

    func testAsleepGapIsNotCountedAsWork() {
        var tracker = TurnETATracker()
        _ = tracker.update(phase: .working, now: at(0), samples: samples, project: "p")
        let after = tracker.update(phase: .working, now: at(7_200), samples: samples, project: "p")
        XCTAssertEqual(after.elapsed, TurnETATracker.tickCeiling, accuracy: 0.001)
    }

    func testCompactionShowsNoEstimate() {
        var tracker = TurnETATracker()
        _ = tracker.update(phase: .working, now: at(0), samples: samples, project: "p")
        XCTAssertNil(tracker.update(phase: .compacting, now: at(2), samples: samples, project: "p").estimate)
    }

    func testBarNeverGoesBackwards() {
        var tracker = TurnETATracker()
        var last = 0.0
        for second in stride(from: 0.0, through: 400, by: 2) {
            let snapshot = tracker.update(phase: .working, now: at(second), samples: samples, project: "p")
            XCTAssertGreaterThanOrEqual(snapshot.progress, last)
            XCTAssertLessThanOrEqual(snapshot.progress, TurnETATracker.progressCeiling)
            last = snapshot.progress
        }
    }

    func testFollowUpWidensTheEstimateButNotTheBar() {
        var tracker = TurnETATracker()
        var before = tracker.update(phase: .working, now: at(0), samples: samples, project: "p")
        for second in stride(from: 2.0, through: 90, by: 2) {
            before = tracker.update(phase: .working, now: at(second), samples: samples, project: "p")
        }
        tracker.noteFollowUp()
        let after = tracker.update(phase: .working, now: at(92), samples: samples, project: "p")
        XCTAssertTrue(after.extended)
        XCTAssertGreaterThan(after.estimate!.median, before.estimate!.median)
        XCTAssertGreaterThanOrEqual(after.progress, before.progress)
    }

    func testTurnFirstSeenMidwayStartsFromWhatHasAlreadyPassed() {
        var tracker = TurnETATracker(alreadyRunning: 120)
        let snapshot = tracker.update(phase: .working, now: at(0), samples: samples, project: "p")
        XCTAssertEqual(snapshot.elapsed, 120, accuracy: 0.001)
    }

    func testFinishedFillsTheBar() {
        var tracker = TurnETATracker()
        _ = tracker.update(phase: .working, now: at(0), samples: samples, project: "p")
        XCTAssertEqual(tracker.update(phase: .finished, now: at(5), samples: samples, project: "p").progress, 1)
    }

    // MARK: - History

    func testHistoryIgnoresImplausibleAndDuplicateTurns() {
        var turns = TurnHistory()
        turns.record(agent: "claude", project: "p", seconds: 1, at: t0)
        turns.record(agent: "claude", project: "p", seconds: .nan, at: t0)
        turns.record(agent: "claude", project: "p", seconds: 100_000, at: t0)
        XCTAssertTrue(turns.durations(agent: "claude").isEmpty)
        turns.absorb(agent: "claude", [sample(90), sample(90)])
        turns.absorb(agent: "claude", [sample(90)])
        XCTAssertEqual(turns.durations(agent: "claude"), [90])
        XCTAssertTrue(turns.durations(agent: "claude", project: "other").isEmpty)
    }

    // MARK: - Coordinator

    private func session(_ id: String, _ state: AgentSession.State, since: Date,
                         project: String = "p", followUps: Int = 0) -> AgentSession {
        AgentSession(id: id, name: id, detail: "", state: state, waitingFor: nil, since: since,
                     projectKey: project, followUps: followUps)
    }

    func testTurnThatWasWatchedToTheEndIsLearned() {
        var coordinator = TurnETACoordinator()
        _ = coordinator.absorb(["claude": [session("a", .busy, since: at(0))]], now: at(0))
        for second in stride(from: 2.0, through: 40, by: 2) {
            _ = coordinator.absorb(["claude": [session("a", .busy, since: at(0))]], now: at(second))
        }
        let done = coordinator.absorb(["claude": [session("a", .idle, since: at(41))]], now: at(41))
        XCTAssertTrue(done.learned)
        XCTAssertEqual(coordinator.history.durations(agent: "claude", project: "p").first ?? 0, 40, accuracy: 1)
    }

    func testTurnAlreadyFinishedOnFirstSightTeachesNothing() {
        var coordinator = TurnETACoordinator()
        let result = coordinator.absorb(["claude": [session("a", .idle, since: at(0))]], now: at(5))
        XCTAssertFalse(result.learned)
        XCTAssertTrue(coordinator.history.durations(agent: "claude").isEmpty)
    }

    func testSessionThatVanishesMidTurnTeachesNothing() {
        var coordinator = TurnETACoordinator()
        _ = coordinator.absorb(["claude": [session("a", .busy, since: at(0))]], now: at(10))
        let gone = coordinator.absorb(["claude": []], now: at(12))
        XCTAssertFalse(gone.learned)
        XCTAssertTrue(coordinator.history.durations(agent: "claude").isEmpty)
    }

    func testFollowUpCountWidensTheSessionsEstimate() {
        var coordinator = TurnETACoordinator()
        coordinator.merge(imported: samples, agent: "claude")
        let key = TurnETACoordinator.key("claude", "a")
        var last: TurnETATracker.Snapshot?
        for second in stride(from: 0.0, through: 60, by: 2) {
            last = coordinator.absorb(["claude": [session("a", .busy, since: at(0))]], now: at(second)).snapshots[key]
        }
        let next = coordinator.absorb(["claude": [session("a", .busy, since: at(0), followUps: 1)]], now: at(62)).snapshots[key]
        XCTAssertEqual(next?.extended, true)
        XCTAssertGreaterThan(next?.estimate?.median ?? 0, last?.estimate?.median ?? .infinity)
    }

    func testCompactingSessionsShowNoEstimate() {
        var coordinator = TurnETACoordinator()
        let key = TurnETACoordinator.key("claude", "a")
        let result = coordinator.absorb(["claude": [session("a", .busy, since: at(0))]],
                                        compacting: [key], now: at(5))
        XCTAssertEqual(result.snapshots[key]?.phase, .compacting)
        XCTAssertNil(result.snapshots[key]?.estimate)
    }

    // MARK: - Importer

    func testClaudeTurnDurationRecordIsRead() throws {
        let line = #"{"type":"system","subtype":"turn_duration","durationMs":95000,"timestamp":"2026-10-06T17:08:35.420Z","cwd":"/Users/x/app","isSidechain":false}"#
        let imported = try XCTUnwrap(TurnHistoryImporter.claudeSample(fromLine: Data(line.utf8)))
        XCTAssertEqual(imported.seconds, 95)
        XCTAssertEqual(imported.project, "app")
    }

    func testCodexTaskCompleteRecordIsRead() throws {
        let line = #"{"timestamp":"2026-10-07T03:17:06.739Z","type":"event_msg","payload":{"type":"task_complete","duration_ms":94478}}"#
        let imported = try XCTUnwrap(TurnHistoryImporter.codexSample(fromLine: Data(line.utf8), project: "app"))
        XCTAssertEqual(imported.seconds, 94.478, accuracy: 0.001)
        XCTAssertEqual(imported.project, "app")
    }

    func testImporterReadsOnlyDurationsFromALogDirectory() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("eta-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let log = """
        {"type":"user","message":{"content":"secret prompt"},"timestamp":"2026-10-06T17:00:00.000Z"}
        {"type":"system","subtype":"turn_duration","durationMs":42000,"timestamp":"2026-10-06T17:01:00.000Z","cwd":"/a/b/app"}
        {"type":"system","subtype":"turn_duration","durationMs":1000,"timestamp":"2026-10-06T17:02:00.000Z","isSidechain":true}
        """
        try log.write(to: root.appendingPathComponent("s.jsonl"), atomically: true, encoding: .utf8)
        let found = TurnHistoryImporter.claudeSamples(projects: root,
            now: TurnHistoryImporter.parse("2026-10-07T00:00:00.000Z")!)
        XCTAssertEqual(found.map(\.seconds), [42])
        XCTAssertEqual(found.first?.project, "app")
    }
}

final class TurnETACopyTests: XCTestCase {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        return calendar
    }
    private let noon = Date(timeIntervalSince1970: 1_800_000_000)

    private func working(_ low: Double, _ median: Double, _ high: Double,
                         basis: TurnETAEstimate.Basis = .project, extended: Bool = false) -> TurnETATracker.Snapshot {
        TurnETATracker.Snapshot(phase: .working, elapsed: 30,
            estimate: TurnETAEstimate(low: low, median: median, high: high, basis: basis),
            progress: 0.3, extended: extended)
    }

    func testRangesShareTheirUnit() {
        XCTAssertEqual(TurnETACopy.range(180, 480), "3–8 min")
        XCTAssertEqual(TurnETACopy.range(30, 150), "30 sec – 3 min")
        XCTAssertEqual(TurnETACopy.range(40, 40), "40 sec")
        XCTAssertEqual(TurnETACopy.span(4_200), "1 hr 10 min")
    }

    func testHeadlineAndFinishTime() {
        let line = TurnETACopy.line(for: working(180, 300, 480), now: noon,
                                    locale: Locale(identifier: "en_US_POSIX"), calendar: calendar)
        XCTAssertEqual(line?.headline, "typically 3–8 min left")
        XCTAssertTrue(line?.detail?.contains("around ") == true)
    }

    func testALongTailIsKeptOutOfTheHeadline() {
        let wide = TurnETATracker.Snapshot(phase: .working, elapsed: 40,
            estimate: TurnETAEstimate(low: 15, median: 200, high: 1_975, basis: .project,
                                      typicalLow: 60, typicalHigh: 360),
            progress: 0.15, extended: false)
        let line = TurnETACopy.line(for: wide, now: noon)
        XCTAssertEqual(line?.headline, "typically 1–6 min left")
        XCTAssertTrue(line?.detail?.hasPrefix("up to 33 min") == true)
    }

    func testNearlyDoneNeedsNoRange() {
        XCTAssertEqual(TurnETACopy.line(for: working(2, 6, 12), now: noon)?.headline, "almost done")
    }

    func testFollowUpSaysSo() {
        XCTAssertEqual(TurnETACopy.line(for: working(180, 300, 480, extended: true), now: noon)?.headline,
                       "now typically 3–8 min left")
    }

    func testOverrunMakesNoPromise() {
        XCTAssertEqual(TurnETACopy.line(for: working(10, 30, 90, basis: .overrun), now: noon)?.headline,
                       "running long")
    }

    func testWaitingAndCompactingAreNamed() {
        func snapshot(_ phase: TurnETATracker.Phase) -> TurnETATracker.Snapshot {
            TurnETATracker.Snapshot(phase: phase, elapsed: 5, estimate: nil, progress: 0.2, extended: false)
        }
        XCTAssertEqual(TurnETACopy.line(for: snapshot(.waiting), now: noon)?.headline, "waiting for you")
        XCTAssertEqual(TurnETACopy.line(for: snapshot(.compacting), now: noon)?.headline, "compacting context")
        XCTAssertNil(TurnETACopy.line(for: snapshot(.finished), now: noon))
    }
}

@MainActor
final class TurnETAStoreTests: XCTestCase {
    func testWatchedTurnsSurviveARelaunch() async throws {
        let file = FileManager.default.temporaryDirectory
            .appendingPathComponent("eta-store-\(UUID().uuidString)/TurnHistory.json")
        defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
        let start = Date()
        func session(_ state: AgentSession.State) -> [String: [AgentSession]] {
            ["claude": [AgentSession(id: "a", name: "a", detail: "", state: state, waitingFor: nil,
                                     since: start.addingTimeInterval(-90), projectKey: "app")]]
        }
        let store = TurnETAStore(file: file)
        store.absorb(session(.busy), now: start)
        XCTAssertNotNil(store.snapshots[TurnETACoordinator.key("claude", "a")])
        store.absorb(session(.idle), now: start.addingTimeInterval(5))

        // Saved after a short debounce rather than on every turn.
        for _ in 0..<60 where !FileManager.default.fileExists(atPath: file.path) {
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        let reloaded = TurnETAStore(file: file)
        reloaded.absorb(session(.busy), now: start.addingTimeInterval(100))
        let snapshot = try XCTUnwrap(reloaded.snapshots[TurnETACoordinator.key("claude", "a")])
        XCTAssertGreaterThan(snapshot.elapsed, 90)
    }
}

final class TurnETACardLayoutTests: XCTestCase {
    /// The time-left card is drawn in the panel's transparent margin, so the
    /// panel has to reach past the usage card by exactly its width and gap —
    /// clipped otherwise — on every edge.
    func testThePanelReachesPastTheUsageCardForTheTimeCard() {
        for edge in NotchEdge.allCases {
            let card = NotchLayout.defaultMaxCardHeight
            let depth = NotchLayout.tooltipDepth(for: edge, maxCardHeight: card)
            let usageOnly = (edge.isVertical ? NotchLayout.cardWidth : card)
                + NotchLayout.tailLength + NotchLayout.tailGap
            if edge.isVertical {
                XCTAssertEqual(depth - usageOnly, NotchLayout.etaReach, accuracy: 0.001, "\(edge)")
            } else {
                // Along a horizontal edge the card sits beside the usage card instead.
                XCTAssertEqual(depth, usageOnly, accuracy: 0.001, "\(edge)")
                XCTAssertGreaterThanOrEqual(
                    NotchLayout.slack(for: edge, maxCardHeight: card),
                    NotchLayout.cardWidth / 2 + NotchLayout.cardCorner + NotchLayout.etaReach, "\(edge)")
            }
        }
    }

    func testTheCardHeightHoldsItsThreeLines() {
        XCTAssertGreaterThan(NotchLayout.etaCardHeight, 2 * NotchLayout.cardBodyLineHeight)
        // Even with every tile it must fit inside the usage card's own height, the room the panel gives it.
        XCTAssertLessThan(NotchLayout.etaCardHeight, NotchLayout.defaultMaxCardHeight)
    }
}

final class SessionDetailCopyTests: XCTestCase {
    func testClaudeModelNamesAreReadable() {
        XCTAssertEqual(SessionDetailCopy.modelName("claude-opus-5-5"), "Opus 5.5")
        XCTAssertEqual(SessionDetailCopy.modelName("claude-sonnet-5-5-20261001"), "Sonnet 5.5")
        XCTAssertEqual(SessionDetailCopy.modelName("gpt-6.1-sol"), "gpt-6.1-sol")
    }

    func testFactsKeepTheModelApartAndDropWhatTheRingAlreadySays() {
        let rows = [
            SessionDetailCopy.Row(label: "Up to", value: "48 min"),
            SessionDetailCopy.Row(label: "Finishes", value: "8:32 PM"),
            SessionDetailCopy.Row(label: "State", value: "working"),
            SessionDetailCopy.Row(label: "Elapsed", value: "5 min"),
            SessionDetailCopy.Row(label: "Tokens", value: "10.2M"),
            SessionDetailCopy.Row(label: "Model", value: "Opus 5.5"),
            SessionDetailCopy.Row(label: "Effort", value: "high"),
            SessionDetailCopy.Row(label: "Fast mode", value: "On", isOn: true),
            SessionDetailCopy.Row(label: "Daybreak", value: "Off"),
        ]
        let withEstimate = SessionDetailCopy.facts(rows, hasEstimate: true)
        XCTAssertEqual(withEstimate.model, "Opus 5.5")
        XCTAssertEqual(withEstimate.effort, "high")
        XCTAssertEqual(withEstimate.fast, true)
        XCTAssertEqual(withEstimate.daybreak, false)
        XCTAssertEqual(withEstimate.stats.map(\.label), ["Elapsed", "Tokens", "Finishes", "Up to"])
        // With no ring, the state is worth a figure of its own.
        let without = SessionDetailCopy.facts(rows, hasEstimate: false)
        XCTAssertEqual(without.stats.map(\.label), ["State", "Elapsed", "Tokens", "Finishes", "Up to"])
    }

    func testRowsListWhatIsKnownAndNothingElse() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let session = AgentSession(id: "codex.1", name: "p", detail: "d", state: .busy, waitingFor: nil,
                                   since: now.addingTimeInterval(-480), codexThreadID: "1",
                                   details: ModelDetails(model: "gpt-6-sol", effort: "high", fast: false, daybreak: true))
        let rows = SessionDetailCopy.rows(for: session, tokens: 2_010_000,
                                          line: TurnETACopy.Line(headline: "typically 2–15 min left",
                                                                  detail: "up to 54 min · around 12:05 AM"),
                                          now: now)
        XCTAssertEqual(rows.map(\.label), ["Up to", "Finishes", "State", "Elapsed", "Tokens", "Model", "Effort", "Fast mode", "Daybreak"])
        XCTAssertEqual(rows.first { $0.label == "Fast mode" }?.value, "Off")
        XCTAssertEqual(rows.first { $0.label == "Daybreak" }?.isOn, true)
        let bare = AgentSession(id: "x", name: "p", detail: "d", state: .idle, waitingFor: nil, since: now)
        XCTAssertEqual(SessionDetailCopy.rows(for: bare, tokens: nil, line: nil, now: now).map(\.label), ["State"])
    }

    @MainActor
    func testAFinishedChatKeepsTheRingAndAnUnknownOneDoesNot() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        func card(_ state: AgentSession.State) -> CGFloat {
            let session = AgentSession(id: "x", name: "p", detail: "d", state: state, waitingFor: nil, since: now)
            return TurnETACard.height(snapshot: nil, session: session, tokens: nil, now: now)
        }
        // The ring fills the body, so a finished chat's card holds at least the ring.
        XCTAssertGreaterThanOrEqual(card(.idle), NotchLayout.etaRing + 2 * NotchLayout.etaCardPadding)
        XCTAssertLessThan(card(.unavailable), card(.idle))
    }
}

final class RecentTurnPaceTests: XCTestCase {
    func testRecentShortTurnsPullTheEstimateDown() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let longAgo = now.addingTimeInterval(-3 * 24 * 3600)
        var samples = (0..<30).map { _ in TurnHistory.Sample(seconds: 400, project: "other", recordedAt: longAgo) }
        samples += (0..<6).map { i in TurnHistory.Sample(seconds: 12, project: "chat", recordedAt: now.addingTimeInterval(-Double(i) * 300)) }
        let withPace = TurnETAEstimator.estimate(elapsed: 3, samples: samples, project: "chat", now: now)
        let withoutPace = TurnETAEstimator.estimate(elapsed: 3, samples: samples, project: "chat",
                                                    now: now.addingTimeInterval(30 * 24 * 3600))
        XCTAssertLessThan(withPace.median, withoutPace.median)
    }
}

final class RangeCopyTests: XCTestCase {
    func testTheRingFigureAlwaysFitsInOnePiece() {
        func label(_ headline: String) -> String {
            TurnETACopy.ringLabel(headline).map { "\($0.value)|\($0.unit)" } ?? "nil"
        }
        XCTAssertEqual(label("typically 3–8 min left"), "3–8|min left")
        XCTAssertEqual(label("now typically 4 min left"), "4|min left")
        XCTAssertEqual(label("typically 25 sec – 3 min left"), "25s–3m|left")
        XCTAssertEqual(label("typically 1 hr 10 min – 2 hr left"), "1h10m–2h|left")
        XCTAssertEqual(label("typically 1 hr 10 min left"), "1h10m|left")
        XCTAssertEqual(label("almost done"), "nil")
        XCTAssertEqual(label("running long"), "nil")
    }

    func testEndsThatRoundTogetherReadAsOneValue() {
        XCTAssertEqual(TurnETACopy.range(215, 265), "4 min")
        XCTAssertEqual(TurnETACopy.range(180, 480), "3–8 min")
    }
}
