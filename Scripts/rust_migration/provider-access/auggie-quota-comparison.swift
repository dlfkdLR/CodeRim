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
    static func main() throws {
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
