import CodeRimUsageCore
import Foundation

let session = SourceImportSession(checkpoint: .fresh(sourcePath: "fixture", fileIdentity: "1:2"),
    state: .empty, provider: .codex, sourceURL: URL(fileURLWithPath: "/tmp/fixture/../synthetic.jsonl"),
    importCutoff: nil, projectKey: Data(repeating: 7, count: 32), loadState: { _ in .empty })

let counts: [Int64] = [.min, -1, 0, 1, 19, 10_000_000, .max]
let times: [UInt64] = [0, 1, 0x8000_0000_0000_0000, 0x3ff0_0000_0000_0000,
                         0x7ff0_0000_0000_0000, 0xfff0_0000_0000_0000, 0x7ff8_0000_0000_0001]
let ids: [String?] = [nil, "", "unknown", "saved-1", "🙂한글", "e\u{0301}", "|time:0\0|", String(repeating: "s", count: 4095),
                      String(repeating: "s", count: 4096), String(repeating: "s", count: 4097)]
func observation(_ n: Int) -> CodexTokenObservation {
    let usage = TokenUsage(inputTokens: counts[n % 7], cachedInputTokens: counts[(n + 1) % 7],
                           cacheWriteInputTokens: n.isMultiple(of: 2) ? nil : counts[n % 7],
                           outputTokens: counts[(n + 3) % 7])
    return CodexTokenObservation(occurredAt: Date(timeIntervalSinceReferenceDate: Double(bitPattern: times[(n / 7) % 7])),
        ordinal: n.isMultiple(of: 3) ? nil : counts[n % 7], lastUsage: n.isMultiple(of: 4) ? nil : usage,
        cumulativeUsage: n.isMultiple(of: 5) ? nil : usage)
}

if CommandLine.arguments.contains("--benchmark") {
    let id = String(repeating: "a", count: 64)
    let observations = (0..<256).map(observation)
    let start = DispatchTime.now().uptimeNanoseconds
    var checksum: UInt64 = 0
    for index in 0..<8192 {
        let key = session.fixtureKey(id, observations[index % observations.count])
        checksum &+= key.utf8.reduce(0) { $0 &+ UInt64($1) }
    }
    print(checksum, DispatchTime.now().uptimeNanoseconds - start)
} else {
    for id in ids {
        for n in 0..<210 { print(session.fixtureKey(id, observation(n))) }
    }
    for n in 0...256 {
        let bytes = Data((0..<n).map { UInt8(truncatingIfNeeded: $0 + n) })
        print(UsageStorageIdentity.digest(bytes))
        print(UsageStorageIdentity.project("/tmp/합성/../sample", keyData: bytes)?.id ?? "-")
    }
}
