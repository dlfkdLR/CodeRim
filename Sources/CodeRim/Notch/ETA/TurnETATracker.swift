import Foundation

/// One turn's clock, re-estimated every time it is looked at.
///
/// Pure and free of AppKit, like `SessionCompletionWatcher`: it is told what
/// state the session is in and what time it is, and answers with what to show.
/// Nothing in it reads a clock or a file, so tests drive it directly.
struct TurnETATracker {
    /// What the session is doing, as far as the estimate is concerned.
    enum Phase: Equatable, Sendable {
        case working
        /// Stopped for an answer. The person's reaction time is not the
        /// agent's, so the clock and the bar stand still.
        case waiting
        /// Summarising its own context; no progress is knowable.
        case compacting
        case finished
    }

    struct Snapshot: Equatable, Sendable {
        let phase: Phase
        /// Time spent working, with waiting and compaction taken out.
        let elapsed: TimeInterval
        /// Nil whenever there is nothing honest to say (waiting, compacting).
        let estimate: TurnETAEstimate?
        /// 0...1, and never smaller than it has been.
        let progress: Double
        /// A follow-up instruction recently stretched the estimate.
        let extended: Bool
    }

    private(set) var phase: Phase = .working
    private(set) var activeElapsed: TimeInterval

    /// A turn already under way when it was first seen has spent `alreadyRunning`.
    init(alreadyRunning: TimeInterval = 0) {
        activeElapsed = alreadyRunning.isFinite ? max(0, alreadyRunning) : 0
    }
    private var lastTick: Date?
    private var shownProgress: Double = 0
    private var lastRemaining: (low: Double, median: Double, high: Double)?
    /// Work promised before the latest follow-up and not yet done.
    private var carried: (low: Double, median: Double, high: Double, since: TimeInterval)?

    /// A gap longer than this between two looks is the Mac asleep or the app
    /// suspended, not the agent working, so it is not counted.
    static let tickCeiling: TimeInterval = 10
    /// The bar stops short of full until the turn really ends.
    static let progressCeiling = 0.97

    /// A turn this young has shown too little to be sized. Most end inside it,
    /// and the ones that do not are the ones a typical-turn estimate is most
    /// wrong about, so nothing is said until the turn has had time to show
    /// what it is.
    static let warmup: TimeInterval = 90

    mutating func update(
        phase: Phase,
        now: Date,
        samples: [TurnHistory.Sample],
        project: String?,
        toolCalls: Int = 0
    ) -> Snapshot {
        if let last = lastTick, self.phase == .working {
            activeElapsed += min(max(0, now.timeIntervalSince(last)), Self.tickCeiling)
        }
        lastTick = now
        self.phase = phase

        switch phase {
        case .waiting, .compacting:
            return Snapshot(phase: phase, elapsed: activeElapsed, estimate: nil,
                            progress: shownProgress, extended: carried != nil)
        case .finished:
            shownProgress = 1
            return Snapshot(phase: phase, elapsed: activeElapsed, estimate: nil,
                            progress: 1, extended: false)
        case .working:
            break
        }

        if activeElapsed < Self.warmup {
            return Snapshot(phase: .working, elapsed: activeElapsed, estimate: nil,
                            progress: shownProgress, extended: carried != nil)
        }

        let episodeStart = carried?.since ?? 0
        let fresh = TurnETAEstimator.estimate(
            elapsed: activeElapsed - episodeStart, toolCalls: toolCalls,
            samples: samples, project: project, now: now)
        var low = fresh.low, median = fresh.median, high = fresh.high
        var typicalLow = fresh.typicalLow, typicalHigh = fresh.typicalHigh
        if let carried {
            // What was still owed before the follow-up shrinks as time passes;
            // the follow-up's own work comes on top of it.
            let spent = activeElapsed - carried.since
            low += max(0, carried.low - spent)
            median += max(0, carried.median - spent)
            high += max(0, carried.high - spent)
            typicalLow += max(0, carried.low - spent)
            typicalHigh += max(0, carried.high - spent) * 0.5
        }
        lastRemaining = (low, median, high)

        let raw = activeElapsed / max(activeElapsed + median, 1)
        shownProgress = max(shownProgress, min(raw, Self.progressCeiling))
        return Snapshot(phase: .working, elapsed: activeElapsed,
                        estimate: TurnETAEstimate(low: low, median: median, high: high,
                                                  basis: fresh.basis,
                                                  typicalLow: typicalLow, typicalHigh: typicalHigh),
                        progress: shownProgress, extended: carried != nil)
    }

    /// The person gave another instruction while the turn was running.
    ///
    /// What was left is kept, and a fresh estimate for the new work is added to
    /// it from here on. The bar does not go back; it just advances more slowly.
    mutating func noteFollowUp() {
        let owed = lastRemaining ?? (0, 0, 0)
        carried = (owed.low, owed.median, owed.high, activeElapsed)
    }
}
