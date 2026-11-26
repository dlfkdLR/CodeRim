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

private func checkImportRuns() {
    let marker = Data(#""token_count""#.utf8)
    let quiet = Data("unrelated".utf8), partial = Data(#""token"#.utf8)
    let bytes = [UInt8](marker)
    for budget in [1, 2, 127, 128, 999, 1_000] {
        for carry in 0...marker.count {
            var storage = Data(repeating: 0, count: 9)
            storage.append(contentsOf: bytes)
            storage.append(10)
            for index in 1...2_005 {
                if [127, 999, 1_000, 1_001, 2_004].contains(index) {
                    storage.append(marker)
                } else if index % 2 == 0 {
                    storage.append(quiet)
                }
                storage.append(10)
            }
            storage.append(partial)
            let input = storage.dropFirst(9)
            var reader = SourceLineBreaks(), decoded: [Int] = []
            var offset = input.startIndex, count = 0, batchLines = 0
            reader.start(at: input.startIndex + carry)
            while let run = reader.nextImportRun(in: input, from: offset, codex: true, maxLines: budget - batchLines) {
                precondition(run.lines > 0 && batchLines + run.lines <= budget)
                if let row = run.selected, run.preselected || input[row].range(of: marker) != nil {
                    precondition(input[row].elementsEqual(marker))
                    decoded.append(count + run.lines - 1)
                }
                count += run.lines
                batchLines = (batchLines + run.lines) % budget
                offset = run.end
            }
            precondition(count == 2_006 && input[offset...].elementsEqual(partial))
            precondition(decoded == [0, 127, 999, 1_000, 1_001, 2_004])
        }
        print("Codex rows", budget, marker.count + 1)
    }
}

if CommandLine.arguments.count == 1 {
    checkCases()
    checkImportRuns()
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
