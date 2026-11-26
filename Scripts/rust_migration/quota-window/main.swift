import Foundation

let instant = Date(timeIntervalSinceReferenceDate: 752_153_400)
func calendar(_ zone: String, _ locale: String) -> Calendar {
    var result = Calendar(identifier: .gregorian)
    result.locale = Locale(identifier: locale)
    result.timeZone = TimeZone(identifier: zone)!
    return result
}

func window(_ fraction: Double?, _ duration: Double?, _ reset: Date?) -> LimitWindow {
    LimitWindow(id: "session", label: "Session", usedFraction: fraction, resetsAt: reset, duration: duration)
}
func account(_ percent: Double, _ minutes: Int, _ reset: Date?) -> AccountLimitWindow {
    AccountLimitWindow(id: "weekly", limitID: "codex", displayName: "Weekly", windowDurationMinutes: minutes,
                       usedPercent: percent, resetsAt: reset)
}

func replay() {
    var fractions: [Double] = [-0.0, -1, 0, .nan, .infinity, -.infinity, .leastNonzeroMagnitude,
                               .greatestFiniteMagnitude, Double(Int.max)/100, Double(Int.max).nextDown/100]
    for edge: Double in [0.0005, 0.001, 0.005, 0.0095, 0.01, 0.095, 0.5, 0.985, 0.99, 0.999, 0.9995, 1, 1.01] {
        fractions += [edge.nextDown, edge, edge.nextUp]
    }
    for i in 0...2000 { fractions.append(Double(i) / 1973) }
    for x in fractions {
        let pair = Percent.halves(for: x)
        let entry = window(x, 18_000, instant.addingTimeInterval(14_513))
        let pace = entry.usagePace(now: instant)
        print("fraction|\(x.bitPattern)|\(Percent.text(for: x))|\(pair.used)|\(pair.left)|\(entry.summary)|\(pace?.percentagePoints.bitPattern.description ?? "-")|\(pace?.summary ?? "-")")
    }
    let durations: [Double?] = [nil,0,-1,.nan,.infinity,.leastNonzeroMagnitude,1,18_000,604_800,.greatestFiniteMagnitude]
    let offsets: [Double?] = [nil,.nan,.infinity,-.infinity,-1,0,1,10,120,17460,17500,18_000,19_000]
    for d in durations {
        for t in offsets {
            for value: Double? in [nil, .nan, -1, -0.0, 0.037, 0.5, 1, 2] {
                let w = window(value, d, t.map { instant.addingTimeInterval($0) })
                let p = w.usagePace(now: instant)
                print("notch|\(p?.percentagePoints.bitPattern.description ?? "-")|\(p?.summary ?? "-")")
            }
        }
    }
    for origin: Double in [0, instant.timeIntervalSinceReferenceDate, .nan, .infinity, -.infinity, 4e18] {
        let now = Date(timeIntervalSinceReferenceDate: origin)
        for minutes in [-1, 0, 1, 300, 10_080, 527_040, 527_041, Int.max] {
            for t in [Double?.none, .some(-1.0), 0.0, 1.0, 58.2, 59.0, 17_459.999, 17_460, 17_460.001, 18_000, 100_000, .infinity, .nan] {
                for value: Double in [.nan, -.infinity, -1, -0.0, 0.9, 3, 4, 5, 99, 100, .infinity] {
                    let a = account(value, minutes, t.map { now.addingTimeInterval($0) }).pace(at: now)
                    print("account|\(a?.summary ?? "-")|\(a?.compactSummary ?? "-")|\(a?.differencePercent.bitPattern.description ?? "-")|\(a?.projectedExhaustion?.timeIntervalSinceReferenceDate.bitPattern.description ?? "-")")
                }
            }
        }
    }
    let times: [Double] = [-.infinity, .nan, -.greatestFiniteMagnitude, -10, 0, 0.001, 29.999, 30, 59.5*60-0.001,
                           59.5*60, 1439.5*60, 1440*60, 7*86400-1, 7*86400, 27*86400, .greatestFiniteMagnitude]
    for (zone, locale) in [("UTC","en_US"),("America/New_York","en_US"),("Asia/Seoul","ko_KR"),("Europe/Berlin","de_DE")] {
        let cal = calendar(zone, locale)
        for origin: Double in [instant.timeIntervalSinceReferenceDate, 794_818_800, 815_381_600] {
            let now = Date(timeIntervalSinceReferenceDate: origin)
            for t in times {
                for format in ResetTimeFormat.allCases {
                    print("reset|\(ResetCopy.text(for: now.addingTimeInterval(t), now: now, calendar: cal, format: format))")
                }
            }
        }
    }
    for x: Double in [-.infinity, -1500, -100, -0.0499, -0.0, .nan, 100, 135, .infinity] {
        print("summary|\(UsagePace(percentagePoints: x).summary)")
    }
}

@inline(never)
func measurement(_ workload: String, _ loops: Int) -> (UInt64, UInt64) {
    let cal = calendar("America/New_York", "en_US")
    var items: [(LimitWindow, AccountLimitWindow)] = []
    for i in 0..<40 {
        let fraction = workload == "whole-ring" ? Double(i * 2) / 100 : [0.0, 0.095, 0.5, 0.76, 1, 0.18, 0.42, 0.0025, 0.991, 0.89][i % 10]
        let reset = instant.addingTimeInterval(workload == "dated" ? Double(86400 * (i%15+1)) : Double(i * 233 + 27))
        items.append((window(fraction, 18000, reset), account(fraction * 100, 300, reset)))
    }
    var sum: UInt64 = 0
    func add(_ text: String) { for byte in text.utf8 { sum = (sum &* 31) &+ UInt64(byte) } }
    let start = DispatchTime.now().uptimeNanoseconds
    for _ in 0..<loops {
        for (w, a) in items {
            switch workload {
            case "ring", "whole-ring": add(Percent.text(for: w.usedFraction!))
            case "rows":
                add(w.summary)
                if let pace = w.usagePace(now: instant) { add(pace.summary) }
                add(ResetCopy.text(for: w.resetsAt!, now: instant, calendar: cal, format: .remaining))
            case "menu":
                if let pace = a.pace(at: instant) {
                    add(pace.summary)
                    if let projection = pace.projectedExhaustion { sum &+= projection.timeIntervalSinceReferenceDate.bitPattern }
                }
                add(ResetCopy.text(for: a.resetsAt!, now: instant, calendar: cal, format: .remaining))
            default: add(ResetCopy.text(for: w.resetsAt!, now: instant, calendar: cal))
            }
        }
    }
    return (sum, DispatchTime.now().uptimeNanoseconds - start)
}

if CommandLine.arguments.count > 2 {
    let workload = CommandLine.arguments[1]
    _ = measurement(workload, workload == "dated" ? 1 : 5)
    let (sum, ns) = measurement(workload, Int(CommandLine.arguments[2])!)
    print(sum, ns)
} else {
    replay()
}
