import Foundation

func delimiterPositions(_ bytes: Data, skip: Int) -> [Int] {
    var positions = [Int]()
    #if RELEASE_SCANNER
    var cursor = bytes.startIndex
    while let at = bytes[cursor...].firstIndex(of: 10) {
        positions.append(at)
        cursor = at + 1
    }
    #else
    var reader = SourceLineBreaks()
    reader.start(at: bytes.startIndex + skip)
    while let at = reader.next(in: bytes) { positions.append(at) }
    #endif
    return positions
}

func checkCases() {
    for length in [0, 1, 2, 127, 128, 129, 255, 256, 257, 1023, 4096, 262_144] {
        for gap in [1, 2, 7, 127, 128, 129, 512, 4096, length + 1] {
            for prefix in [0, 5, 19] {
                var buffer = Data(repeating: 10, count: prefix)
                buffer.append(Data(repeating: 13, count: prefix + length))
                for i in stride(from: gap - 1, to: length, by: gap) { buffer[prefix * 2 + i] = 10 }
                let bytes = buffer.dropFirst(prefix)
                let actual = delimiterPositions(bytes, skip: prefix)
                let expected = bytes.indices.filter { bytes[$0] == 10 }
                precondition(expected == actual)
                print(length, gap, prefix, actual.count, actual.reduce(UInt64.zero) { $0 &+ UInt64($1) })
            }
        }
    }
    // Carry an incomplete line between real 256 KiB reads, drop a finished prefix,
    // and re-use the same reader across many batches of empty and CRLF lines.
    var buffer = Data()
    var originalOffset = 0
    let chunks = [Data(repeating: 65, count: 262_144), Data(repeating: 66, count: 262_144),
                  Data([13, 10, 10, 13]), Data([10, 10]) + Data(repeating: 10, count: 5000), Data([65])]
    var received = [Int]()
    for chunk in chunks {
        let skip = buffer.count
        buffer.append(chunk)
        let found = delimiterPositions(buffer, skip: skip)
        received += found.map { originalOffset + $0 - buffer.startIndex }
        if let last = found.last {
            let consumed = last + 1 - buffer.startIndex
            buffer.removeSubrange(buffer.startIndex...last)
            originalOffset += consumed
        }
    }
    let joined = chunks.reduce(into: Data()) { $0.append($1) }
    precondition(received == joined.indices.filter { joined[$0] == 10 })
    print("chunks", received.count, originalOffset, buffer.count)
}

if CommandLine.arguments.count == 1 {
    checkCases()
} else {
    let gap = Int(CommandLine.arguments[1])!
    let skip = Int(CommandLine.arguments[2])!
    var buffer = Data(repeating: 65, count: skip + 256 * 1024)
    for at in stride(from: skip + gap - 1, to: buffer.count, by: gap) { buffer[at] = 10 }
    var checksum: UInt64 = 0
    let started = DispatchTime.now().uptimeNanoseconds
    for _ in 0..<32 {
        for offset in delimiterPositions(buffer, skip: skip) { checksum &+= UInt64(offset) }
    }
    print(checksum, DispatchTime.now().uptimeNanoseconds - started)
}
