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
    var metadataHit: Bool? = nil
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
@MainActor
func add(_ name: String, _ value: String, fast: Bool) {
    cases.append(Case(name: name, data: Data(value.utf8), fast: fast))
}
@MainActor
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


// Exercise strict metadata shapes and every host field through the production parser.
@MainActor
func metadata(_ name: String, _ line: String, hit: Bool) {
    cases.append(Case(name: name, data: Data(line.utf8), fast: false, metadataHit: hit))
}
let session = #"{"type":"session_meta","timestamp":"2001-01-01T00:00:00Z","payload":{"id":"session","model":"compact","cwd":"/tmp/example","forked_from_id":"fork","parent_thread_id":"parent","subagent_history_start_ordinal":8}}"#
let context = #"{"payload":{"cwd":"/tmp","model":"x"},"type":"turn_context"}"#
let task = #"{"ordinal":9,"type":"event_msg","payload":{"type":"task_started","started_at":1800000000},"timestamp":"2001-01-01T00:00:00Z"}"#
let ignored = #"{"payload":{"message":"private test fixture","type":"agent_message"},"type":"event_msg"}"#
metadata("full-session", session, hit: true)
metadata("empty-session", #"{"type":"session_meta","payload":{}}"#, hit: true)
metadata("session-date-invalid", session.replacingOccurrences(of: "2001-01-01T00:00:00Z", with: "no-date"), hit: true)
metadata("null-fields", #"{"type":"session_meta","timestamp":null,"payload":{"id":null,"cwd":null,"model":null,"subagent_history_start_ordinal":null}}"#, hit: true)
for n in [0, 255, 256, 257] {
    metadata("session-id-\(n)", session.replacingOccurrences(of: #""session""#, with: "\"" + String(repeating: "a", count: n) + "\""), hit: true)
}
for n in [4096, 4097] {
    metadata("session-path-\(n)", session.replacingOccurrences(of: "/tmp/example", with: String(repeating: "a", count: n)), hit: true)
}
metadata("context", context, hit: true)
metadata("task", task, hit: true)
metadata("task-with-turn-id", task.replacingOccurrences(of: #""started_at":"#, with: #""turn_id":"ignored","started_at":"#), hit: true)
metadata("context-with-scalar-content", context.replacingOccurrences(of: #""model":"#, with: #""content":"ignored","model":"#), hit: true)
metadata("task-no-timestamp", #"{"type":"event_msg","payload":{"type":"task_started"}}"#, hit: true)
metadata("task-invalid-timestamp", task.replacingOccurrences(of: "2001-01-01T00:00:00Z", with: "bad"), hit: true)
metadata("ignored-message", ignored, hit: true)
for (name, value) in [("fractional","0.25"),("whole-float","2.0"),("scientific","1e2"),("negative","-1"),("large","9007199254740993"),("bool","false"),("string",#""2""#)] {
    metadata("task-started-\(name)", task.replacingOccurrences(of: "1800000000", with: value), hit: false)
}
metadata("task-string-ordinal", task.replacingOccurrences(of: #""ordinal":9"#, with: #""ordinal":"9""#), hit: false)
metadata("session-duplicate-null", session.replacingOccurrences(of: #""model":"compact""#, with: #""model":null,"model":"compact""#), hit: false)
metadata("session-escaped-key", session.replacingOccurrences(of: #""id":"session""#, with: #""\u0069d":"session""#), hit: false)
metadata("context-non-ascii", context.replacingOccurrences(of: "/tmp", with: "/학교"), hit: false)
metadata("context-escaped-path", context.replacingOccurrences(of: "/tmp", with: #"\/tmp"#), hit: false)
metadata("context-NUL", context.replacingOccurrences(of: "/tmp", with: #"a\u0000b"#), hit: false)
metadata("context-positional", #"{"type":"turn_context","payload":["x"]}"#, hit: false)
metadata("unknown-context-field", context.replacingOccurrences(of: #""cwd""#, with: #""future":null,"cwd""#), hit: false)
metadata("message-is-not-token", ignored.replacingOccurrences(of: "private test fixture", with: "token_count"), hit: false)
metadata("wrong-shaped-message", ignored.replacingOccurrences(of: #""message":"private test fixture""#, with: #""message":{"x":1}"#), hit: false)
metadata("late-type-outside-hint", String(repeating: " ", count: 9000) + session, hit: false)
metadata("long-message-with-early-key", ignored.replacingOccurrences(of: "private test fixture", with: String(repeating: "m", count: 9000)), hit: true)


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
            let tokenHit = cr_parse_token_json(bytes.baseAddress, bytes.count).timestamp_length > 0
            let kind = Int(cr_parse_codex_event(bytes.baseAddress, bytes.count).kind)
            if let metadataHit = sample.metadataHit {
                precondition((kind != CR_EVENT_MISS) == metadataHit, "Rust metadata coverage mismatch for \(sample.name)")
            } else {
                precondition((kind == CR_EVENT_TOKEN) == expected, "Rust event token coverage mismatch for \(sample.name)")
            }
            return tokenHit
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
