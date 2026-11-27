import CodeRimUsageCore
import Foundation
import CryptoKit

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

if !CommandLine.arguments.contains("--benchmark") {
    // Compare complete import identities and checkpoint ancestry through the production parser.
    for filename in ["session.jsonl", "agent-worker.jsonl", "agent-K.jsonl", "agent-e\u{0301}.jsonl"] {
        for cutoff in [nil, Date(timeIntervalSince1970: 1_793_491_200)] as [Date?] {
            var importer = SourceImportSession(
                checkpoint: .fresh(sourcePath: "fixture", fileIdentity: "1:2"), state: .empty,
                provider: .claude, sourceURL: URL(fileURLWithPath: "/tmp/fixture/" + filename),
                importCutoff: cutoff, projectKey: Data(repeating: 7, count: 32), loadState: { _ in .empty })
            let agents: [String?] = [nil, "K", "agent-K", "", "bad!", "agent-worker", nil, "a.b_C-0"]
            for n in 0..<47 {
                var row: [String: Any] = [
                    "type": "assistant", "timestamp": n.isMultiple(of: 3) ? "2026-10-31T23:59:59Z" : "2026-11-01T00:00:01Z",
                    "sessionId": n.isMultiple(of: 7) ? "other" : "stored-session",
                    "message": ["role": "assistant", "model": "claude-synthetic", "id": "msg_\(n % 13)",
                                "usage": ["input_tokens": n + 1, "output_tokens": 3]]
                ]
                row["agentId"] = agents[n % agents.count]
                let data = try JSONSerialization.data(withJSONObject: row, options: [.sortedKeys])
                try await importer.consume(data, position: Int64(n))
                print(importer.checkpoint.sessionID ?? "-", importer.checkpoint.parentSessionID ?? "-",
                      importer.pendingEvents.count, importer.excludedEventKeys.sorted().joined(separator: ","))
            }
            for row in importer.pendingEvents {
                print(row.eventKey, row.sessionID ?? "-", row.importParentSessionID ?? "-",
                      row.sourcePosition, row.usage.inputTokens, row.usage.outputTokens)
            }
        }
    }
}

if !CommandLine.arguments.contains("--benchmark") {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    let originalDirectory = FileManager.default.currentDirectoryPath
    defer {
        _ = FileManager.default.changeCurrentDirectoryPath(originalDirectory)
        try? FileManager.default.removeItem(at: root)
    }
    // Both binaries use the same logical spelling. Their disposable absolute roots differ.
    // Compare the projected digest to the release formula using each current absolute root.
    precondition(FileManager.default.changeCurrentDirectoryPath(root.path))
    let paths: [String?] = [nil, "", ".", "..", "relative", "a/../b", "dir//name", "../../A",
        "kept%2Ftext", "e\u{301}", "é", "literal\\slash", "?q#f", "a\0b", "link", "link/../x",
        "/tmp/synthetic/../bounded", String(repeating: "a", count: 128), String(repeating: "b", count: 129),
        String(repeating: "./a", count: 1365), String(repeating: "a", count: 4097)]
    try FileManager.default.createDirectory(atPath: "one/child", withIntermediateDirectories: true)
    try FileManager.default.createDirectory(atPath: "two/child", withIntermediateDirectories: true)
    for target in ["one/child", "two/child", "missing"] {
        if (try? FileManager.default.destinationOfSymbolicLink(atPath: "link")) != nil {
            try FileManager.default.removeItem(atPath: "link")
        }
        try FileManager.default.createSymbolicLink(atPath: "link", withDestinationPath: target)
        for count in [0, 1, 31, 32, 64, 65, 257, 4097] {
            let key = Data((0..<count).map { UInt8(truncatingIfNeeded: $0 * 17) })
            for path in paths {
                let result = UsageStorageIdentity.project(path, keyData: key)
                let expected = referenceProject(path, key: key)
                precondition(result?.id == expected?.id)
                if let expected { precondition(result?.name.utf8.elementsEqual(expected.name.utf8) == true) }
                print(result == nil ? "rejected" : "project matches")
            }
        }
    }
}

func referenceProject(_ input: String?, key: Data) -> (id: String, name: String)? {
    guard let input, !input.isEmpty, input.utf8.count <= 4096, !input.contains("\0") else { return nil }
    let path = URL(fileURLWithPath: input).standardizedFileURL.path
    let name = URL(fileURLWithPath: path).lastPathComponent
    guard !name.isEmpty, name != ".", name != "..", name.utf8.count <= 128, !name.contains("\0") else { return nil }
    let hash = HMAC<SHA256>.authenticationCode(for: Data("CodexMeter.project.v1|\(path)".utf8), using: SymmetricKey(data: key))
    return (hash.map { String(format: "%02x", $0) }.joined(), name)
}
