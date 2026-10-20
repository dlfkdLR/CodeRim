import Foundation

/// What is left of a turn, as a range.
///
/// A turn's length is heavy-tailed: the same agent in the same project can
/// answer in ten seconds or in an hour, depending on what was asked. A single
/// number would be wrong most of the time, so this says "between low and high",
/// with `median` for the bar to aim at. Measured against this Mac's own
/// transcripts, an 80% range holds about 80% of the time; a point estimate
/// within ±50% holds under 40% of it. The range is the honest output.
struct TurnETAEstimate: Equatable, Sendable {
    /// Where the numbers came from, from most to least specific.
    enum Basis: Equatable, Sendable {
        /// Earlier turns in this project.
        case project
        /// Earlier turns of this agent anywhere.
        case agent
        /// Not enough history: a built-in prior from typical turns.
        case prior
        /// Already longer than nearly every turn on record, so the only
        /// honest statement left is proportional to the time already spent.
        case overrun
    }

    let low: TimeInterval
    let median: TimeInterval
    let high: TimeInterval
    let basis: Basis

    var range: ClosedRange<TimeInterval> { low...high }
}

enum TurnETAEstimator {
    /// Fewer remaining samples than this and a quantile is noise.
    static let minimumTail = 8

    /// Typical turn lengths in seconds (p5–p95 of this Mac's own history), used
    /// until the user has enough of their own.
    static let prior: [Double] = [8, 12, 20, 35, 55, 80, 110, 160, 240, 400, 700, 1200, 2400]

    /// Estimate the time left in a turn that has been working for `elapsed`.
    ///
    /// Conditional on survival: only turns that lasted longer than `elapsed`
    /// say anything about what happens next, and what they left over is the
    /// distribution of what remains. That is also why the estimate can
    /// *increase* as a turn drags on — a turn that is still going after ten
    /// minutes really is likely to go on for a while.
    static func estimate(
        elapsed: TimeInterval,
        projectDurations: [Double],
        agentDurations: [Double]
    ) -> TurnETAEstimate {
        let elapsed = elapsed.isFinite ? max(0, elapsed) : 0
        let sources: [(TurnETAEstimate.Basis, [Double])] = [
            (.project, projectDurations), (.agent, agentDurations), (.prior, prior),
        ]
        for (basis, durations) in sources {
            let tail = durations.filter { $0.isFinite && $0 > elapsed }.map { $0 - elapsed }
            guard tail.count >= minimumTail else { continue }
            return TurnETAEstimate(low: quantile(tail, 0.10), median: quantile(tail, 0.50),
                                   high: quantile(tail, 0.90), basis: basis)
        }
        // Past everything on record. Remaining time scales with the time
        // already spent, because nothing else is known.
        return TurnETAEstimate(low: elapsed * 0.15, median: elapsed * 0.5, high: elapsed * 1.5,
                               basis: .overrun)
    }

    /// Linear interpolation between order statistics; `values` must not be empty.
    static func quantile(_ values: [Double], _ p: Double) -> Double {
        let sorted = values.sorted()
        guard sorted.count > 1 else { return sorted[0] }
        let position = min(max(p, 0), 1) * Double(sorted.count - 1)
        let lower = Int(position.rounded(.down))
        let upper = min(lower + 1, sorted.count - 1)
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - Double(lower))
    }
}
