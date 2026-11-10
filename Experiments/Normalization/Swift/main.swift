import Foundation
import NormalizationTrial

precondition(MemoryLayout<TrialUsage>.size == 32 && MemoryLayout<TrialState>.size == 64
    && MemoryLayout<TrialObservation>.size == 88 && MemoryLayout<TrialResult>.size == 120)

struct Fixture: Decodable { let name: String; let steps: [Step] }
struct Step: Decodable {
    let occurredAt: Double
    let lastUsage: TokenUsage?
    let cumulativeUsage: TokenUsage?
}

func packed(_ usage: TokenUsage?) -> TrialUsage {
    TrialUsage(input: usage?.inputTokens ?? 0, cached: usage?.cachedInputTokens ?? 0,
        output: usage?.outputTokens ?? 0, written: usage?.cacheWriteInputTokens ?? -1)
}
func unpacked(_ usage: TrialUsage) -> TokenUsage {
    TokenUsage(inputTokens: usage.input, cachedInputTokens: usage.cached,
        cacheWriteInputTokens: usage.written == -1 ? nil : usage.written, outputTokens: usage.output)
}
let diagnostics: [String?] = [nil, "out-of-order token snapshot ignored", "missing cumulative token usage",
    "initial cumulative baseline is unresolved", "cumulative counter restarted",
    "cumulative token usage decreased or interleaved", "inconsistent token delta clamped",
    "inconsistent token usage clamped"]
let qualities: [DataQuality] = [.exact, .partial, .stale, .unavailable, .error]
let fixtures = try JSONDecoder().decode([Fixture].self, from: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
precondition(!fixtures.isEmpty)
let normalizer = UsageNormalizer()
var workload: [(UsageNormalizationState, CodexTokenObservation, TrialState, TrialObservation)] = []
for fixture in fixtures {
    var native = UsageNormalizationState.empty
    var rust = TrialState()
    for step in fixture.steps {
        let observation = CodexTokenObservation(occurredAt: Date(timeIntervalSince1970: step.occurredAt),
            ordinal: nil, lastUsage: step.lastUsage, cumulativeUsage: step.cumulativeUsage)
        let ffi = TrialObservation(cumulative: packed(step.cumulativeUsage), last: packed(step.lastUsage),
            at: step.occurredAt, has_cumulative: step.cumulativeUsage == nil ? 0 : 1,
            has_last: step.lastUsage == nil ? 0 : 1)
        workload.append((native, observation, rust, ffi))
        let expected = normalizer.normalize(observation, state: native)
        let actual = trial_normalize(rust, ffi, 0)
        precondition(expected.delta == (actual.has_delta == 0 ? nil : unpacked(actual.delta)), fixture.name)
        precondition(expected.state.cumulativeHighWaterMark == (actual.state.has_usage == 0 ? nil : unpacked(actual.state.usage)), fixture.name)
        precondition(expected.state.lastObservedAt?.timeIntervalSince1970 == (actual.state.has_time == 0 ? nil : actual.state.observed_at), fixture.name)
        precondition(expected.state.quality == qualities[Int(actual.state.quality)], fixture.name)
        precondition(expected.diagnostic == diagnostics[Int(actual.diagnostic)], fixture.name)
        native = expected.state
        rust = actual.state
    }
}
let iterations = 1_000_000
let mode = CommandLine.arguments.count > 2 ? CommandLine.arguments[2] : "both"
precondition(["native", "rust", "both"].contains(mode))
var nativeTimes: [UInt64] = [], rustTimes: [UInt64] = []
var nativeChecksum: Int64 = 0, rustChecksum: Int64 = 0
for _ in 0..<4 {
    if mode != "rust" {
        let start = DispatchTime.now().uptimeNanoseconds
        var checksum: Int64 = 0
        for index in 0..<iterations {
            let row = workload[index % workload.count]
            checksum &+= normalizer.normalize(row.1, state: row.0).delta?.inputTokens ?? 0
        }
        nativeTimes.append(DispatchTime.now().uptimeNanoseconds - start)
        nativeChecksum = checksum
    }
    if mode != "native" {
        let start = DispatchTime.now().uptimeNanoseconds
        var checksum: Int64 = 0
        for index in 0..<iterations {
            let row = workload[index % workload.count]
            let result = trial_normalize(row.2, row.3, 0)
            checksum &+= result.has_delta == 0 ? 0 : result.delta.input
        }
        rustTimes.append(DispatchTime.now().uptimeNanoseconds - start)
        rustChecksum = checksum
    }
}
if mode == "both" { precondition(nativeChecksum == rustChecksum) }
let lines = try fixtures.flatMap { fixture in
    try fixture.steps.map { step -> Data in
        func usage(_ value: TokenUsage?) -> Any {
            guard let value else { return NSNull() }
            var result: [String: Any] = ["input_tokens": value.inputTokens,
                "cached_input_tokens": value.cachedInputTokens, "output_tokens": value.outputTokens]
            if let written = value.cacheWriteInputTokens { result["cache_write_input_tokens"] = written }
            return result
        }
        return try JSONSerialization.data(withJSONObject: ["type": "event_msg",
            "timestamp": "2026-08-27T01:02:03.125Z", "payload": ["type": "token_count",
            "info": ["last_token_usage": usage(step.lastUsage), "total_token_usage": usage(step.cumulativeUsage)]]])
    }
}
let parser = CodexJSONLParser()
let pipelineIterations = 20_000
var pipelineNative: [UInt64] = [], pipelineRust: [UInt64] = []
var pipelineChecksums: [Int64] = []
for _ in 0..<4 {
    for variant in ["native", "rust"] where mode == "both" || mode == variant {
        let start = DispatchTime.now().uptimeNanoseconds
        var checksum: Int64 = 0
        for index in 0..<pipelineIterations {
            guard case let .token(observation) = parser.parse(lines[index % lines.count]) else { continue }
            checksum &+= observation.cumulativeUsage?.inputTokens ?? observation.lastUsage?.inputTokens ?? 0
            if variant == "native" {
                checksum &+= normalizer.normalize(observation, state: .empty).delta?.inputTokens ?? 0
            } else {
                let ffi = TrialObservation(cumulative: packed(observation.cumulativeUsage), last: packed(observation.lastUsage),
                    at: observation.occurredAt.timeIntervalSince1970,
                    has_cumulative: observation.cumulativeUsage == nil ? 0 : 1,
                    has_last: observation.lastUsage == nil ? 0 : 1)
                let result = trial_normalize(TrialState(), ffi, 0)
                checksum &+= result.has_delta == 0 ? 0 : result.delta.input
            }
        }
        let elapsed = DispatchTime.now().uptimeNanoseconds - start
        if variant == "native" { pipelineNative.append(elapsed) } else { pipelineRust.append(elapsed) }
        pipelineChecksums.append(checksum)
    }
}
precondition(Set(pipelineChecksums).count == 1)
let report: [String: Any] = ["language": "swift", "cases": fixtures.count, "transitions": workload.count,
    "iterations": iterations, "mode": mode, "nativeNanoseconds": nativeTimes,
    "rustNanoseconds": rustTimes, "checksum": mode == "rust" ? rustChecksum : nativeChecksum,
    "pipelineIterations": pipelineIterations, "pipelineNativeNanoseconds": pipelineNative,
    "pipelineRustNanoseconds": pipelineRust, "pipelineChecksum": pipelineChecksums[0]]
print(String(decoding: try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys]), as: UTF8.self))
