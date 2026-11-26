import CryptoKit
import Foundation

// Run a single immutable fixture or a private copy mutated by the first callback.
func runFilter(_ args: [String]) async throws {
    let file = URL(fileURLWithPath: args[0])
    let needles = try JSONDecoder().decode([String].self, from: Data(contentsOf: URL(fileURLWithPath: args[1])))
    let action = args[2]
    let copy = args[3] == "copy"
    if action == "timed" {
        var checksum = 0
        let start = DispatchTime.now().uptimeNanoseconds
        for _ in 0..<3 {
            TranscriptLines.forEachLine(of: file, containingAny: needles, copying: copy) {
                checksum &+= $0.count &+ Int($0.first ?? 0)
            }
        }
        print(checksum, (DispatchTime.now().uptimeNanoseconds - start) / 3)
        return
    }
    var lines = [Data]()
    if action == "cancel-before" { withUnsafeCurrentTask { $0?.cancel() } }
    TranscriptLines.forEachLine(of: file, containingAny: needles, copying: copy) { line in
        lines.append(line)
        guard lines.count == 1 else { return }
        switch action {
        case "cancel": withUnsafeCurrentTask { $0?.cancel() }
        case "truncate", "append":
            // Each process gets its own copy of a writable synthetic input.
            let handle = try! FileHandle(forWritingTo: file)
            if action == "append" {
                try! handle.seekToEnd()
                try! handle.write(contentsOf: Data("\nmatch appended\n".utf8))
            } else {
                try! handle.truncate(atOffset: 4096)
            }
            try! handle.close()
        default: break
        }
    }
    var digest = SHA256()
    for line in lines {
        var size = UInt64(line.count).littleEndian
        withUnsafeBytes(of: &size) { digest.update(bufferPointer: $0) }
        digest.update(data: line)
    }
    print(lines.count, lines.reduce(0) { $0 + $1.startIndex }, digest.finalize().map { String(format: "%02x", $0) }.joined())
}
