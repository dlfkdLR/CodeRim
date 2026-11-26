import CodeRimUsageCore
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif
import Darwin
import Foundation

// Compile this same driver with each production implementation. The measured call goes through
// UsageNormalizer and includes host-side argument packing and result conversion in the Rust build.
struct Step: Decodable {
    let occurredAt: Double
    let lastUsage: TokenUsage?
    let cumulativeUsage: TokenUsage?
}

struct Scenario: Decodable {
    let steps: [Step]
}

struct Row {
    let startsSession: Bool
    let observation: CodexTokenObservation
}

func usage(_ input: Int64, _ cached: Int64, _ written: Int64?, _ output: Int64) -> TokenUsage {
    TokenUsage(inputTokens: input, cachedInputTokens: cached, cacheWriteInputTokens: written, outputTokens: output)
}

func rows(_ workload: String, _ fixture: URL) throws -> [Row] {
    if workload == "contract" {
        let scenarios = try JSONDecoder().decode([Scenario].self, from: Data(contentsOf: fixture))
        return scenarios.flatMap { scenario in scenario.steps.enumerated().map { index, step in
            Row(startsSession: index == 0, observation: CodexTokenObservation(
                occurredAt: Date(timeIntervalSince1970: step.occurredAt), ordinal: Int64(index),
                lastUsage: step.lastUsage, cumulativeUsage: step.cumulativeUsage))
        }}
    }
    let large = workload == "large"
    // The normal workload is within parser limits. The large workload directly exercises
    // the accounting boundary using a valid initial snapshot with a saturating total.
    let base: Int64 = large ? Int64.max - 1_000_000 : 0
    var cumulative = usage(base, 0, 0, base)
    return (0..<128).map { index in
        let n = Int64(index)
        let next = usage(15 + (n % 13), n % 8, n % 4, 7 + (n % 5))
        cumulative = cumulative.adding(next)
        let value = CodexTokenObservation(occurredAt: Date(timeIntervalSince1970: 1_800_000_000 + Double(n)),
            ordinal: n, lastUsage: index == 0 ? cumulative : next, cumulativeUsage: cumulative)
        return Row(startsSession: index == 0, observation: value)
    }
}

func jsonLine(_ observation: CodexTokenObservation, index: Int, workload: String) throws -> Data {
    func counters(_ n: TokenUsage?) -> Any {
        guard let n else { return NSNull() }
        var fields = ["input_tokens": n.inputTokens, "cached_input_tokens": n.cachedInputTokens,
                      "output_tokens": n.outputTokens, "reasoning_output_tokens": n.outputTokens / 2,
                      "total_tokens": n.totalTokens]
        fields["cache_write_input_tokens"] = n.cacheWriteInputTokens
        return fields
    }
    let timestamp = observation.occurredAt.ISO8601Format(Date.ISO8601FormatStyle(includingFractionalSeconds: true))
    let kind: String
    switch workload {
    case "parse-mixed":
        kind = ["token", "rate", "session", "future", "context", "ignored", "image", "rate"][index % 8]
    case "parse-nontoken":
        kind = ["session", "task", "context", "image", "ignored", "malformed"][index % 6]
    case "parse-rate-untagged": kind = "rate"
    case "parse-fallback": kind = "future"
    default: kind = "token"
    }
    var object: [String: Any] = ["timestamp": timestamp]
    switch kind {
    case "session":
        object["type"] = "session_meta"
        object["payload"] = ["id": "fixture", "cwd": "/fixture", "model": "test-model"]
    case "task":
        object["type"] = "event_msg"
        object["payload"] = ["type": "task_started", "started_at": 1_800_000_000]
    case "context":
        object["type"] = "turn_context"
        object["payload"] = ["cwd": "/fixture", "model": "test-model"]
    case "image":
        object["type"] = "response_item"
        object["payload"] = ["type": "message", "role": "user", "content": [["type": "input_image"]]]
    case "ignored":
        object["type"] = "event_msg"
        object["payload"] = ["type": "agent_message", "message": "synthetic fixture"]
    case "malformed":
        return Data("{".utf8)
    default:
        var payload: [String: Any] = ["type": "token_count", "info": [
            "last_token_usage": counters(observation.lastUsage), "total_token_usage": counters(observation.cumulativeUsage),
            "model_context_window": 258_400]]
        if kind == "rate" {
            payload["rate_limits"] = ["limit_id": "codex", "limit_name": NSNull(), "plan_type": "plus",
                "primary": ["used_percent": 12.5, "window_minutes": 300, "resets_at": 1_800_000_300],
                "secondary": ["used_percent": 3.0, "window_minutes": 10_080, "resets_at": 1_800_086_400],
                "credits": ["has_credits": false, "unlimited": false, "balance": NSNull()]]
        } else if kind == "future" {
            // Unknown input properties must delegate the complete token row to Foundation.
            payload["future_key"] = ["sample": 1]
        } else {
            object["ordinal"] = observation.ordinal
        }
        object["type"] = "event_msg"
        object["payload"] = payload
    }
    return try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])
}

func rowClass(_ row: CodexParsedLine) -> String {
    switch row {
    case .sessionMetadata: "session"
    case .taskStarted: "task"
    case .turnContext: "context"
    case .imageAttachments: "image"
    case .token: "token"
    case .ignored: "ignored"
    case .malformed: "malformed"
    }
}

func cpuNanoseconds(_ r: rusage) -> Int64 {
    func ns(_ t: timeval) -> Int64 { Int64(t.tv_sec) * 1_000_000_000 + Int64(t.tv_usec) * 1_000 }
    return ns(r.ru_utime) + ns(r.ru_stime)
}

func resources() -> rusage {
    var result = rusage()
    precondition(getrusage(RUSAGE_SELF, &result) == 0)
    return result
}

let fixture = URL(fileURLWithPath: CommandLine.arguments[1])
let workload = CommandLine.arguments[2]
precondition(["contract", "normal", "large", "parse-normal", "parse-rate-untagged",
              "parse-mixed", "parse-nontoken", "parse-fallback"].contains(workload))
let iterations = Int(CommandLine.arguments[3])!
precondition(iterations > 0)
let inputs = try rows(workload, fixture)
precondition(!inputs.isEmpty)
let lines = workload.hasPrefix("parse-") ? try inputs.enumerated().map {
    try jsonLine($0.element.observation, index: $0.offset, workload: workload)
} : []
let parser = CodexJSONLParser()
let normalizer = UsageNormalizer()

@inline(never)
func measureLoop(_ count: Int) -> UInt64 {
    var state = UsageNormalizationState.empty
    var checksum = UInt64(0)
    for index in 0..<count {
        let idx = index % inputs.count
        let row = inputs[idx]
        if row.startsSession { state = .empty }
        let observation: CodexTokenObservation
        if lines.isEmpty {
            observation = row.observation
        } else {
            switch parser.parse(lines[idx]) {
            case .token(let parsed): observation = parsed
            case .sessionMetadata(let metadata):
                checksum = checksum &* 31 &+ 101 &+ UInt64(metadata.id?.utf8.count ?? 0)
                continue
            case .taskStarted(let task):
                checksum = checksum &* 31 &+ 102 &+ UInt64(bitPattern: task.ordinal ?? 0)
                continue
            case .turnContext(let context):
                checksum = checksum &* 31 &+ 103 &+ UInt64(context.model?.utf8.count ?? 0)
                continue
            case .imageAttachments(let image):
                checksum = checksum &* 31 &+ 104 &+ UInt64(image.count)
                continue
            case .ignored:
                checksum = checksum &* 31 &+ 105
                continue
            case .malformed(let reason):
                checksum = checksum &* 31 &+ 106 &+ UInt64(reason.utf8.count)
                continue
            }
        }
        let result = normalizer.normalize(observation, state: state)
        state = result.state
        // Consume the emitted event and upstream checkpoint. This is a consistency check
        // for the timed workload; full field-by-field equivalence is checked by the host tests.
        let delta = result.delta
        let total = UInt64(bitPattern: delta?.totalTokens ?? -1)
        let cached = UInt64(bitPattern: delta?.cachedInputTokens ?? -1)
        let written = UInt64(bitPattern: delta?.cacheWriteInputTokens ?? -1)
        let watermark = UInt64(bitPattern: state.cumulativeHighWaterMark?.inputTokens ?? -1)
        checksum = checksum &* 31 &+ total &+ cached &* 7 &+ written &* 13 &+ watermark
    }
    return checksum
}

// Same work and iteration count in the warm and measured phases, inside the same process.
let classes = Dictionary(grouping: lines.map { rowClass(parser.parse($0)) }, by: { $0 }).mapValues(\.count)
let warm = measureLoop(iterations)
let before = resources()
let start = DispatchTime.now().uptimeNanoseconds
let checksum = measureLoop(iterations)
let elapsed = DispatchTime.now().uptimeNanoseconds - start
let after = resources()
precondition(warm == checksum)
#if CODERIM_RUST_ACCOUNTING
let mode = "rust"
#else
let mode = "swift"
#endif
var report: [String: Any] = ["mode": mode, "workload": workload, "iterations": iterations,
    "rows": inputs.count, "parsedRowClasses": classes, "elapsedNs": elapsed, "cpuNs": cpuNanoseconds(after) - cpuNanoseconds(before),
    "peakRssBytes": after.ru_maxrss, "checksum": String(checksum)]
#if CODERIM_RUST_ACCOUNTING
// Count syntax fast-path hits after timing and resource collection. The benchmarked
// loop enters through the production Swift parser in both modes.
report["acceleratedRows"] = lines.reduce(into: 0) { hits, line in
    let accepted = line.withUnsafeBytes { buffer in
        let bytes = buffer.bindMemory(to: UInt8.self)
        return cr_parse_token_json(bytes.baseAddress, bytes.count).timestamp_length > 0
    }
    if accepted { hits += 1 }
}
#endif
print(String(decoding: try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys]), as: UTF8.self))
