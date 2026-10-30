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
    /// The middle half of what past turns did (25th–75th percentile). Far
    /// narrower than `low...high`, and so the part worth putting in a headline —
    /// but it holds only about half the time, so it is only ever called "typical".
    let typicalLow: TimeInterval
    let typicalHigh: TimeInterval

    init(low: TimeInterval, median: TimeInterval, high: TimeInterval, basis: Basis,
         typicalLow: TimeInterval? = nil, typicalHigh: TimeInterval? = nil) {
        self.low = low
        self.median = median
        self.high = high
        self.basis = basis
        self.typicalLow = min(max(typicalLow ?? low, low), median)
        self.typicalHigh = max(min(typicalHigh ?? high, high), median)
    }

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
    /// Turns this recent count this many times over.
    static let recentWindow: TimeInterval = 2 * 60 * 60
    static let recentBoost = 3.0

    /// Estimate the time left in a turn that has been working for `elapsed`.
    ///
    /// Conditional on survival: only turns that lasted longer than `elapsed`
    /// say anything about what happens next, and what they left over is the
    /// distribution of what remains. That is also why the estimate can
    /// *increase* as a turn drags on — a turn that is still going after ten
    /// minutes really is likely to go on for a while.
    ///
    /// `toolCalls` is how many tools the turn has already called. It says what
    /// kind of turn this is, which the clock alone cannot: a reply that has run
    /// for a minute and called nothing is nearly done, and one that has called
    /// twenty tools is a build. It conditions the same way time does — a turn
    /// that has made twenty calls ends with at least twenty — so only turns
    /// that went at least this far are compared with it.
    static func estimate(
        elapsed: TimeInterval,
        toolCalls: Int = 0,
        samples: [TurnHistory.Sample],
        project: String?,
        now: Date = Date()
    ) -> TurnETAEstimate {
        let elapsed = elapsed.isFinite ? max(0, elapsed) : 0
        var tail = samples.filter { $0.seconds.isFinite && $0.seconds > elapsed }
        if toolCalls > 0 {
            let counted = tail.filter { $0.toolCalls != nil }
            let atLeast = counted.filter { ($0.toolCalls ?? 0) >= toolCalls }
            if atLeast.count >= minimumTail {
                tail = atLeast
            } else if counted.count >= minimumTail {
                // Busier than nearly anything on record: the busiest turns are
                // the nearest company there is.
                tail = Array(counted.sorted { ($0.toolCalls ?? 0) > ($1.toolCalls ?? 0) }.prefix(minimumTail))
            }
        }
        let local = tail.filter { project != nil && $0.project == project }

        if tail.count >= minimumTail {
            let weighted = tail.map { sample -> (Double, Double) in
                let isLocal = project != nil && sample.project == project
                // A chat has a pace: two hours of quick replies say more about
                // the next one than a month of long builds do.
                let recent = now.timeIntervalSince(sample.recordedAt) < recentWindow
                    && now >= sample.recordedAt
                return (sample.seconds - elapsed,
                        (isLocal ? 1 : otherProjectWeight) * (recent ? recentBoost : 1))
            }
            return TurnETAEstimate(
                low: weightedQuantile(weighted, 0.10), median: weightedQuantile(weighted, 0.50),
                high: weightedQuantile(weighted, 0.90),
                basis: local.count >= minimumTail ? .project : .agent,
                typicalLow: weightedQuantile(weighted, 0.35),
                typicalHigh: weightedQuantile(weighted, 0.65))
        }
        let priorTail = prior.filter { $0 > elapsed }.map { $0 - elapsed }
        if priorTail.count >= minimumTail {
            return TurnETAEstimate(low: quantile(priorTail, 0.10), median: quantile(priorTail, 0.50),
                                   high: quantile(priorTail, 0.90), basis: .prior,
                                   typicalLow: quantile(priorTail, 0.35),
                                   typicalHigh: quantile(priorTail, 0.65))
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
