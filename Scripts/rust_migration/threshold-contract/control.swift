// Benchmark-only control. It uses the candidate's single headline and dictionary lookup
// with the release policy in Swift, to isolate the cost of the numeric ABI.
import CCodeRimAccounting

func swiftThresholdControl(_ previous: CRThresholdMemory, _ fraction: Double, _ reset: Double,
                           _ hasReset: Bool, _ sameWindow: Bool) -> CRThresholdTransition {
    let percent = min(max(fraction * 100, 0), 1000)
    let level: UInt8 = percent >= 100 ? 100 : percent >= 80 ? 80 : 0
    let newPeriod = hasReset && previous.has_reset && reset - previous.reset > 1800
    let previousLevel = newPeriod ? 0 : previous.level
    let first: UInt8 = previousLevel < 80 && level >= 80 ? 1 : 0
    let last: UInt8 = previousLevel < 100 && level >= 100 ? 2 : 0
    return .init(memory: .init(reset: hasReset ? reset : previous.reset, level: level,
                               has_reset: hasReset || (sameWindow && previous.has_reset)),
                 used_percent: first | last == 0 ? 0 : UInt16(percent.rounded()), alerts: first | last)
}
