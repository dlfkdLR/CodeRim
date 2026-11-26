import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

struct Fixture: Decodable { let name: String; let line: String }
struct Case {
    let name: String
    let data: Data
    // nil for native fixtures where an optimization's coverage is unconstrained.
    let fast: Bool?
}
struct Record: Encodable {
    var name: String
    var kind: String
    var text: [String?] = []
    var counts: [Int64?] = []
    var dates: [UInt64?] = []
    var usage: [TokenUsage?] = []
    var pipeline: Transition?
}
struct Transition: Encodable {
    let delta: TokenUsage?
    let watermark: TokenUsage?
    let observed: UInt64?
    let quality: DataQuality
    let diagnostic: String?
}

let fixtures = try JSONDecoder().decode([Fixture].self, from: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
var cases = fixtures.map { Case(name: $0.name, data: Data($0.line.utf8), fast: nil) }
let stamp = "2026-11-25T02:03:04.125Z"
let counts = #"{"input_tokens":100,"cached_input_tokens":20,"output_tokens":5}"#
let line = #"{"timestamp":"STAMP","type":"event_msg","ordinal":4,"payload":{"type":"token_count","info":{"last_token_usage":COUNTS,"total_token_usage":COUNTS}}}"#
    .replacingOccurrences(of: "STAMP", with: stamp).replacingOccurrences(of: "COUNTS", with: counts)
func add(_ name: String, _ value: String, fast: Bool) {
    cases.append(Case(name: name, data: Data(value.utf8), fast: fast))
}
func replacing(_ key: String, _ value: String) -> String { line.replacingOccurrences(of: key, with: value) }

add("canonical", line, fast: true)
add("ignored-integral-counts", replacing(#""output_tokens":5"#, #""output_tokens":5,"total_tokens":105,"reasoning_output_tokens":0"#), fast: true)
let quota = #""rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":10.5,"window_minutes":300,"resets_at":1795723200},"secondary":{"used_percent":0.0,"window_minutes":10080},"credits":{"has_credits":false,"unlimited":false,"balance":null},"plan_type":"pro","rate_limit_reached_type":null},"info":"#
let quotaLine = replacing(#""info":"#, quota)
add("canonical-rate-limits", quotaLine, fast: true)
add("quota-exponent", quotaLine.replacingOccurrences(of: "10.5,", with: "1e99,"), fast: false)
add("quota-duplicate-key", quotaLine.replacingOccurrences(of: "10.5,", with: #"20,"used_percent":10.5,"#), fast: false)
add("unknown-quota", quotaLine.replacingOccurrences(of: #""limit_id":"#, with: #""future":true,"limit_id":"#), fast: false)
add("known-write", replacing(#""output_tokens":5"#, #""output_tokens":5,"cache_write_input_tokens":80"#), fast: true)
add("at-component-limit", replacing("100,", "1000000000000,"), fast: true)
add("invalid-date", replacing(stamp, "unparseable"), fast: true)
add("bare-null-ordinal", replacing(#""ordinal":4"#, #""ordinal":null"#), fast: true)
for (tag, value) in [
    ("write-null", #""output_tokens":5,"cache_write_input_tokens":null"#),
    ("write-false", #""output_tokens":5,"cache_write_input_tokens":false"#),
    ("write-negative", #""output_tokens":5,"cache_write_input_tokens":-1"#),
    ("write-over-limit", #""output_tokens":5,"cache_write_input_tokens":81"#),
    ("fractional", #""output_tokens":5.5"#), ("whole-float", #""output_tokens":5.0"#),
    ("exponent", #""output_tokens":5e1"#), ("decimal-string", #""output_tokens":"50""#),
    ("signed-string", #""output_tokens":"+50""#), ("boolean", #""output_tokens":true"#),
    ("output-over-limit", #""output_tokens":1000000000001"#),
    ("beyond-uint64", #""output_tokens":18446744073709551616"#),
    ("duplicate-component", #""output_tokens":5,"output_tokens":7"#),
    ("escaped-key", #""output_\u0074okens":5"#),
] {
    add(tag, replacing(#""output_tokens":5"#, value), fast: false)
}
add("duplicate-type", replacing(#""type":"event_msg""#, #""type":"response_item","type":"event_msg""#), fast: false)
add("escaped-timestamp", replacing(stamp, #"2026-11-25T02:03:04.125\u005a"#), fast: false)
add("nul-timestamp", replacing(stamp, #"2026-11-25T02:03:04.125Z\u0000"#), fast: false)
add("unicode-extra-key", replacing(#""ordinal":4"#, #""ordinal":4,"caf\u00e9":0,"cafe\u0301":1"#), fast: false)
add("non-ascii-timestamp", replacing(stamp, "시각"), fast: false)
add("long-timestamp", replacing(stamp, String(repeating: "2", count: 65)), fast: false)
add("array-counts", replacing(counts, "[100,20,5]"), fast: false)
add("array-payload", #"{"type":"event_msg","timestamp":"x","payload":["token_count",{}]}"#, fast: false)
add("unknown-field", replacing(#""ordinal":4"#, #""ordinal":4,"future":{"a":1}"#), fast: false)
add("extra-document", line + " {}", fast: false)
let limit = CodexJSONLParser.maximumLineBytes
var padded = Data(line.utf8)
padded.append(Data(repeating: 32, count: limit - padded.count))
cases.append(Case(name: "exact-size", data: padded, fast: true))
padded.append(32)
cases.append(Case(name: "oversize", data: padded, fast: false))
cases.append(Case(name: "bad-utf8", data: Data(line.utf8) + Data([0xff]), fast: false))
for encoding in [String.Encoding.utf16, .utf16LittleEndian, .utf32BigEndian] {
    cases.append(Case(name: "encoding-\(encoding.rawValue)", data: line.data(using: encoding)!, fast: false))
}

func bits(_ date: Date?) -> UInt64? { date?.timeIntervalSinceReferenceDate.bitPattern }
let parser = CodexJSONLParser()
let normalizer = UsageNormalizer()
var state = UsageNormalizationState.empty
var rows: [Record] = []
for sample in cases {
#if CODERIM_RUST_ACCOUNTING
    if let expected = sample.fast {
        let matched = sample.data.withUnsafeBytes { raw in
            let bytes = raw.bindMemory(to: UInt8.self)
            return cr_parse_token_json(bytes.baseAddress, bytes.count).timestamp_length > 0
        }
        precondition(matched == expected, "Rust coverage mismatch for \(sample.name)")
    }
#endif
    var row = Record(name: sample.name, kind: "")
    switch parser.parse(sample.data) {
    case .token(let obs):
        row.kind = "token"
        row.counts = [obs.ordinal]
        row.dates = [bits(obs.occurredAt)]
        row.usage = [obs.lastUsage, obs.cumulativeUsage]
        let result = normalizer.normalize(obs, state: state)
        state = result.state
        row.pipeline = Transition(delta: result.delta, watermark: state.cumulativeHighWaterMark,
                                  observed: bits(state.lastObservedAt), quality: state.quality,
                                  diagnostic: result.diagnostic)
    case .sessionMetadata(let m):
        row.kind = "session"
        row.text = [m.id, m.model, m.workingDirectory, m.forkedFromID, m.parentThreadID]
        row.counts = [m.subagentHistoryStartOrdinal]
        row.dates = [bits(m.occurredAt)]
    case .taskStarted(let m):
        row.kind = "task"
        row.counts = [m.ordinal]
        row.dates = [bits(m.occurredAt), bits(m.startedAt)]
    case .turnContext(let m):
        row.kind = "turn"
        row.text = [m.model, m.workingDirectory]
    case .imageAttachments(let m):
        row.kind = "images"
        row.counts = [Int64(m.count), m.ordinal]
        row.dates = [bits(m.occurredAt)]
    case .ignored:
        row.kind = "ignored"
    case .malformed(let reason):
        row.kind = "malformed"
        row.text = [reason]
    }
    rows.append(row)
}
let encoder = JSONEncoder()
encoder.outputFormatting = [.sortedKeys]
FileHandle.standardOutput.write(try encoder.encode(rows))
