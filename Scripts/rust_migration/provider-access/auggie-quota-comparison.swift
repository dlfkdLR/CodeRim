import Foundation
import CodexBarCore

struct Observation: Equatable {
    let remaining: Double?
    let used: Double?
    let limit: Double?
    let end: Date?
    let email: String?
    let plan: String?
    let raw: String?
    let failure: String?

    init(_ read: () throws -> AugmentStatusSnapshot) {
        var status: AugmentStatusSnapshot?
        var reason: String?
        do { status = try read() } catch { reason = error.localizedDescription }
        remaining = status?.creditsRemaining
        used = status?.creditsUsed
        limit = status?.creditsLimit
        end = status?.billingCycleEnd
        email = status?.accountEmail
        plan = status?.accountPlan
        raw = status?.rawJSON
        failure = reason
    }
}

@main enum Differential {
    private static func benchmark() throws {
        let amounts = "450,000 credits / month\n319,054 credits remaining"
        let cases = [
            ("current", amounts),
            ("legacy", "Max Plan 450,000 credits / month\n11,657 remaining · 953,170 / 964,827 credits used"),
            ("billing-date", amounts + "\n9 days remaining in this billing cycle (ends 6/9/2026)"),
            ("invalid", "this command supplies no quota"),
            ("unicode-decimal", amounts + "\n９９ credits remaining"),
        ]
        var rows: [[String: Any]] = []
        for (name, input) in cases {
            var times: [String: [Double]] = ["original": [], "rust": []]
            let expected = Observation { try Oracle().parse(input) }
            for round in 0..<8 {
                for side in (round.isMultiple(of: 2) ? ["original", "rust"] : ["rust", "original"]) {
                    let start = DispatchTime.now().uptimeNanoseconds
                    for _ in 0..<100 {
                        let observed = side == "original" ? Observation { try Oracle().parse(input) }
                                                         : Observation { try OfficialUsageCLI.parseAuggie(input) }
                        precondition(observed == expected)
                    }
                    if round > 0 {
                        times[side]!.append(Double(DispatchTime.now().uptimeNanoseconds - start) / 100)
                    }
                }
            }
            rows.append(["fixture": name, "samplesNs": times, "medianNs": times.mapValues { $0.sorted()[3] }])
        }
        FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: rows, options: [.sortedKeys]))
    }

    static func main() throws {
        if CommandLine.arguments.dropFirst().first == "benchmark" {
            try benchmark()
            return
        }
        let inputs = try JSONDecoder().decode([String].self, from: FileHandle.standardInput.readDataToEndOfFile())
        var parsed = 0
        for (offset, input) in inputs.enumerated() {
            let original = Observation { try Oracle().parse(input) }
            let candidate = Observation { try OfficialUsageCLI.parseAuggie(input) }
            guard original == candidate else { fatalError("Quota mismatch at synthetic offset \(offset)") }
            if candidate.failure == nil { parsed += 1 }
        }
        print("TZ \(TimeZone.current.identifier) | synthetic cases \(inputs.count) | parsed \(parsed) | equivalent failures \(inputs.count-parsed) | mismatches 0")
    }
}
