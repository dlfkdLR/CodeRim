import Foundation

/// The words for an estimate. Ranges, never a single promise: the numbers
/// behind them are quantiles of past turns, and the copy should not claim
/// more than that.
enum TurnETACopy {
    struct Line: Equatable {
        /// The headline, e.g. "about 3–8 min left".
        let headline: String
        /// A second, quieter line, e.g. "around 2:41 PM".
        let detail: String?
    }

    static func line(for snapshot: TurnETATracker.Snapshot, now: Date,
                     locale: Locale = Locale(identifier: "en_US"), calendar: Calendar = .current) -> Line? {
        switch snapshot.phase {
        case .waiting:    return Line(headline: "waiting for you", detail: "clock paused")
        case .compacting: return Line(headline: "compacting context", detail: "no progress")
        case .finished:   return nil
        case .working:    break
        }
        guard let estimate = snapshot.estimate else { return nil }

        let prefix = snapshot.extended ? "now " : ""
        switch estimate.basis {
        case .overrun:
            return Line(headline: "\(prefix)running long", detail: "no estimate")
        case .prior, .agent, .project:
            break
        }
        if estimate.high < 20 {
            return Line(headline: "\(prefix)almost done", detail: finish(at: estimate.median, now: now,
                                                                     locale: locale, calendar: calendar))
        }
        // The headline is the typical range; the long tail goes underneath, so
        // a 15-second-to-33-minute spread never reads as the answer.
        let finishText = finish(at: estimate.median, now: now, locale: locale, calendar: calendar)
        let upTo = estimate.high > estimate.typicalHigh * 2.5 ? "up to \(span(estimate.high))" : nil
        return Line(headline: "\(prefix)typically \(range(estimate.typicalLow, estimate.typicalHigh)) left",
                    detail: [upTo, finishText].compactMap { $0 }.joined(separator: " · ").nilIfEmpty)
    }

    /// "3–8 min", "45 sec – 2 min", "1 hr 10 min – 2 hr".
    static func range(_ low: TimeInterval, _ high: TimeInterval) -> String {
        let lo = round(low), hi = round(high)
        if lo == hi { return span(hi) }
        let loText = span(lo), hiText = span(hi)
        // Two ends that read the same are one value, not a range.
        if loText == hiText { return hiText }
        // "3–8 min" reads better than "3 min – 8 min" when the unit is shared.
        for unit in [" min", " sec", " hr"] where loText.hasSuffix(unit) && hiText.hasSuffix(unit) {
            return "\(loText.dropLast(unit.count))–\(hiText)"
        }
        return "\(loText) – \(hiText)"
    }

    /// What goes in the middle of the ring: the range as one short figure and
    /// the word under it. "typically 3–8 min left" gives ("3–8", "min left");
    /// ends in different units are shortened so they still fit, "25 sec – 3 min"
    /// giving ("25s–3m", "left"). Nil when the headline is not a range.
    static func ringLabel(_ headline: String) -> (value: String, unit: String)? {
        var text = headline
        if text.hasPrefix("now ") { text.removeFirst(4) }
        let lead = "typically ", tail = " left"
        guard text.hasPrefix(lead), text.hasSuffix(tail) else { return nil }
        let body = String(text.dropFirst(lead.count).dropLast(tail.count))
        let ends = body.components(separatedBy: " – ")
        if ends.count == 2 { return (ends.map(compact).joined(separator: "–"), "left") }
        for unit in ["min", "sec", "hr"] where body.hasSuffix(" " + unit) {
            let figure = String(body.dropLast(unit.count + 1))
            if figure.allSatisfy({ $0.isNumber || $0 == "–" }) { return (figure, unit + " left") }
        }
        return (compact(body), "left")
    }

    /// "1 hr 10 min" becomes "1h10m".
    private static func compact(_ text: String) -> String {
        text.replacingOccurrences(of: " hr", with: "h").replacingOccurrences(of: " min", with: "m")
            .replacingOccurrences(of: " sec", with: "s").replacingOccurrences(of: " ", with: "")
    }

    /// Coarse on purpose: the estimate is not good to the second.
    static func span(_ seconds: TimeInterval) -> String {
        let value = max(0, seconds)
        if value < 60 { return "\(max(5, Int((value / 5).rounded()) * 5)) sec" }
        let minutes = Int((value / 60).rounded())
        if minutes < 60 { return "\(minutes) min" }
        let hours = minutes / 60, rest = minutes % 60
        let step = rest == 0 ? 0 : (rest / 5) * 5
        return step == 0 ? "\(hours) hr" : "\(hours) hr \(step) min"
    }

    private static func finish(at median: TimeInterval, now: Date, locale: Locale,
                               calendar: Calendar) -> String? {
        guard median >= 60 else { return nil }
        // The copy around it is English, so the time is too; a Korean "오전"
        // in the middle of an English sentence reads as a bug.
        let formatter = DateFormatter()
        formatter.locale = locale
        formatter.calendar = calendar
        formatter.timeZone = calendar.timeZone
        formatter.setLocalizedDateFormatFromTemplate("jmm")
        return "around \(formatter.string(from: now.addingTimeInterval(median)))"
    }
}

private extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
