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

    /// How much a turn from another project counts next to one from this
    /// project. Backtested on this Mac's transcripts, blending beat both "this
    /// project only" and "everything equally": it keeps the project's habits
    /// without going blind when the project has few turns.
    static let otherProjectWeight = 0.25

    /// Estimate the time left in a turn that has been working for `elapsed`.
    ///
    /// Conditional on survival: only turns that lasted longer than `elapsed`
    /// say anything about what happens next, and what they left over is the
    /// distribution of what remains. That is also why the estimate can
    /// *increase* as a turn drags on — a turn that is still going after ten
    /// minutes really is likely to go on for a while.
    static func estimate(
        elapsed: TimeInterval,
        samples: [TurnHistory.Sample],
        project: String?
    ) -> TurnETAEstimate {
        let elapsed = elapsed.isFinite ? max(0, elapsed) : 0
        let tail = samples.filter { $0.seconds.isFinite && $0.seconds > elapsed }
        let local = tail.filter { project != nil && $0.project == project }

        if tail.count >= minimumTail {
            let weighted = tail.map { sample -> (Double, Double) in
                let isLocal = project != nil && sample.project == project
                return (sample.seconds - elapsed, isLocal ? 1 : otherProjectWeight)
            }
            return TurnETAEstimate(
                low: weightedQuantile(weighted, 0.10), median: weightedQuantile(weighted, 0.50),
                high: weightedQuantile(weighted, 0.90),
                basis: local.count >= minimumTail ? .project : .agent)
        }
        let priorTail = prior.filter { $0 > elapsed }.map { $0 - elapsed }
        if priorTail.count >= minimumTail {
            return TurnETAEstimate(low: quantile(priorTail, 0.10), median: quantile(priorTail, 0.50),
                                   high: quantile(priorTail, 0.90), basis: .prior)
        }
        // Past everything on record. Remaining time scales with the time
        // already spent, because nothing else is known.
        return TurnETAEstimate(low: elapsed * 0.15, median: elapsed * 0.5, high: elapsed * 1.5,
                               basis: .overrun)
    }

    /// The smallest value whose cumulative weight reaches `p`; `values` must
    /// not be empty.
    static func weightedQuantile(_ values: [(value: Double, weight: Double)], _ p: Double) -> Double {
        let sorted = values.sorted { $0.value < $1.value }
        let total = sorted.reduce(0) { $0 + $1.weight }
        var running = 0.0
        for entry in sorted {
            running += entry.weight
            if running / total >= p { return entry.value }
        }
        return sorted[sorted.count - 1].value
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
