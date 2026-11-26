import CryptoKit
import Foundation

private func digest(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }

private struct State: Encodable {
    let fixture: Int
    let step: Int
    let offset: UInt64
    let read: UInt64
    let discarding: Bool
    let carry: String
    let lines: [String]
    let starts: [Int]
}

private func cases() throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
    defer { try? FileManager.default.removeItem(at: root) }
    let limit = TranscriptLineCursor.lineBytes
    let chunk = TranscriptLineCursor.chunkBytes
    var inputs: [Data] = [Data(), Data([10]), Data([0, 13, 10, 0xc3, 0x28, 10, 0xff]),
                          Data(repeating: 10, count: 257), Data("한글\r\n🙂\nend".utf8)]
    for n in [chunk - 1, chunk, chunk + 1, limit - 1, limit, limit + 1, 2 * limit] {
        var bytes = Data("a\n".utf8)
        bytes.append(Data(repeating: 119, count: n))
        bytes.append(Data("\n\nkept\npart".utf8))
        inputs.append(bytes)
    }
    var seed: UInt64 = 23
    for i in 1...45 {
        var bytes = Data()
        for _ in 0..<(i * 41) {
            seed = seed &* 6364136223846793005 &+ 1
            bytes.append(seed.isMultiple(of: UInt64(1 + i % 12)) ? 10 : UInt8(truncatingIfNeeded: seed >> 38))
        }
        inputs.append(bytes)
    }
    var states = [State]()
    let file = root.appendingPathComponent("synthetic.jsonl")
    for (i, bytes) in inputs.enumerated() {
        try bytes.write(to: file)
        let handle = try FileHandle(forReadingFrom: file)
        var cursor = TranscriptLineCursor()
        var retained = [Data]()
        let stops = Set([0, 1, 2, bytes.count / 2, bytes.count - 1, bytes.count, chunk - 1, chunk + 1, limit, limit + 1])
            .filter { $0 >= 0 && $0 <= bytes.count }.sorted() + [bytes.count]
        for (step, end) in stops.enumerated() {
            var emitted = [Data]()
            try cursor.read(handle, through: UInt64(end)) { emitted.append($0) }
            retained.append(contentsOf: emitted)
            states.append(State(fixture: i, step: step, offset: cursor.offset, read: cursor.readOffset,
                                discarding: cursor.discarding, carry: digest(cursor.carry),
                                lines: emitted.map(digest), starts: emitted.map(\.startIndex)))
        }
        let writer = try FileHandle(forWritingTo: file)
        try writer.seekToEnd()
        try writer.write(contentsOf: Data("\nagain\n".utf8))
        try writer.close()
        try cursor.read(handle, through: UInt64(bytes.count + 7)) { retained.append($0) }
        states.append(State(fixture: i, step: -1, offset: cursor.offset, read: cursor.readOffset,
                            discarding: cursor.discarding, carry: digest(cursor.carry),
                            lines: retained.map(digest), starts: retained.map(\.startIndex)))
        try handle.close()
    }
    let encoder = JSONEncoder()
    encoder.outputFormatting = .sortedKeys
    FileHandle.standardOutput.write(try encoder.encode(states))
}

private func bench(_ gap: Int) throws {
    let length = 8 * 1024 * 1024
    var bytes = Data(repeating: 65, count: length)
    for i in stride(from: gap - 1, to: length, by: gap) { bytes[i] = 10 }
    let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try bytes.write(to: file)
    defer { try? FileManager.default.removeItem(at: file) }
    let h = try FileHandle(forReadingFrom: file)
    defer { try? h.close() }
    var total = 0
    let start = DispatchTime.now().uptimeNanoseconds
    for _ in 0..<3 {
        var cursor = TranscriptLineCursor()
        try cursor.read(h, through: UInt64(length)) { total &+= $0.count &+ Int($0.first ?? 0) }
        total &+= cursor.carry.count
    }
    print(total, (DispatchTime.now().uptimeNanoseconds - start) / 3)
}
if CommandLine.arguments.dropFirst().first == "filter" {
    try await runFilter(Array(CommandLine.arguments.dropFirst(2)))
} else if let gap = CommandLine.arguments.dropFirst().first.flatMap(Int.init) {
    try bench(gap)
} else {
    try cases()
}
