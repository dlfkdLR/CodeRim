import Foundation

let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
defer { try? FileManager.default.removeItem(at: root) }
let file = root.appendingPathComponent("synthetic.jsonl")
let start = Data(#"{"type":"event_msg","payload":{"type":"task_started"},"timestamp":"2026-05-21T10:00:00Z"}"#.utf8)
let end = Data(#"{"type":"event_msg","payload":{"type":"task_complete"},"timestamp":"2026-05-21T12:04:12.123Z"}"#.utf8)
let abort = Data(#"{"type":"event_msg","payload":{"type":"turn_aborted"},"timestamp":"2026-05-22T00:04:12+09:00"}"#.utf8)
let fake = Data(#"{"type":"response_item","payload":{"type":"task_started"},"timestamp":"2026-05-21T10:00:00Z"}"#.utf8)
let bad = Data(#"{"type":"event_msg","payload":{"type":"turn_aborted"},"timestamp":"invalid"}"#.utf8)
let lf = Data([10])
func code(_ event: CodexTurnActivity.Event?) -> String {
    guard let event else { return "-" }
    return (event.isRunning ? "busy:" : "idle:") + String(event.since.timeIntervalSince1970.bitPattern)
}

if CommandLine.arguments.count > 1 {
    let spacing = Int(CommandLine.arguments[1])!
    let megabytes = Int(CommandLine.arguments[2])!
    let bytes = max(spacing, megabytes * 1024 * 1024)
    var data = Data(); data.append(start); data.append(lf)
    let line = Data(repeating: 120, count: spacing - 1) + lf
    for _ in 0..<(bytes / spacing) { data.append(line) }
    try data.write(to: file)
    let count = megabytes == 0 ? 250 : 3
    let begin = DispatchTime.now().uptimeNanoseconds
    var busy = 0
    for _ in 0..<count {
        if CodexTurnActivity.read(file)?.isRunning == true { busy += 1 }
    }
    let elapsed = DispatchTime.now().uptimeNanoseconds - begin
    precondition(busy == count)
    print(busy, elapsed / UInt64(count))
} else {
    var rows: [String] = []
    var step = 0
    var reader = CodexTurnActivity.Reader()
    func record(_ data: Data, atomic: Bool = false, limited: [Int] = []) throws {
        try data.write(to: file, options: atomic ? .atomic : [])
        step += 1
        try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSince1970: 1_733_000_000 + Double(step))],
                                              ofItemAtPath: file.path)
        rows += [code(reader.read(file)), code(reader.read(file)), code(CodexTurnActivity.read(file))]
        for limit in limited { rows.append(code(CodexTurnActivity.read(file, maximumBytes: limit))) }
    }
    for a in [Data(), start, end, abort, fake, bad, Data([0xff, 34, 10]), Data([13])] {
        for b in [Data(), start, end, bad, fake] {
            try record(a + lf + b, limited: [0, 1, 20, 128])
            try record(a + lf + b + lf, limited: [1, b.count + 1, b.count + 2, 128, 1024])
        }
    }
    // Move a genuine marker across every position relative to a reverse 64 KiB read.
    for boundary in [start, end, abort] {
        let marker = boundary.range(of: Data("type\":\"t".utf8))!.lowerBound + 6
        for shift in -17...17 {
            let trailer = 65_536 - (boundary.count - marker) + shift
            try record(start + lf + boundary + lf + Data(repeating: 120, count: trailer) + lf,
                       atomic: true, limited: [65_536, 65_600])
        }
    }
    // Oversize, same-size rewrite, partial append, invalid UTF-8, moved identity and missing file.
    try record(end + lf + Data(repeating: 32, count: CodexTurnActivity.maximumRecordBytes + 1) + start + lf)
    let original = start + lf + fake + lf
    try record(original)
    try record(original + end)
    try record(original + end + lf)
    try record(abort + lf + fake + lf + start + lf, atomic: true)
    try FileManager.default.removeItem(at: file)
    rows.append(code(reader.read(file)))
    try record(end + lf, atomic: true)
    try FileHandle.standardOutput.write(contentsOf: JSONEncoder().encode(rows))
}
