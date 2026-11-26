import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

func bits(_ date: Date?) -> String { date.map { String($0.timeIntervalSinceReferenceDate.bitPattern, radix: 16) } ?? "-" }
func event(_ stamp: String) -> Data {
    try! JSONSerialization.data(withJSONObject: ["type": "event_msg", "timestamp": stamp,
                                                 "payload": ["type": "task_started"]], options: [.sortedKeys])
}
let parser = CodexJSONLParser()
func parsed(_ line: Data) -> Date? {
    if case .taskStarted(let task) = parser.parse(line) { return task.occurredAt }
    return nil
}

if CommandLine.arguments.count > 1 {
    let inputs = (0..<120).map { String(format: "2026-11-%02dT%02d:%02d:12.%06d+09:00", 1 + $0 % 28, $0 % 24, $0 % 60, $0 * 8149 % 1_000_000) }
    let lines = inputs.map(event)
    let useTranscript = CommandLine.arguments[1] == "transcript"
    var sum: UInt64 = 0
    let begin = DispatchTime.now().uptimeNanoseconds
    for _ in 0..<32 {
        for i in inputs.indices {
            let date = useTranscript ? TranscriptLines.parse(inputs[i]) : parsed(lines[i])
            sum &+= date!.timeIntervalSinceReferenceDate.bitPattern
        }
    }
    print(sum, DispatchTime.now().uptimeNanoseconds - begin)
} else {
    var inputs = ["", "２０２６-11-26T00:00:00Z", "2026-02-29T12:00:00Z","2000-02-29T00:00:00Z",
                  "2100-02-29T00:00:00Z","1900-02-29T00:00:00Z","1582-10-09T00:00:00Z",
                  "1970-01-01T00:00:00.000000001+14:00","2001-01-01T00:00:00.000000001Z"]
    for year in [1969,1970,1972,1999,2000,2001,2004,2024,2026,2038,2096,2099,2100] {
        for month in 1...12 {
            for (n, day) in [1,27,28,29,30,31,32].enumerated() {
                let seconds = ["00:00:00", "01:30:45", "12:04:51", "23:59:59"][n%4]
                let head = String(format: "%04d-%02d-%02dT%@", year,month,day,seconds)
                for fraction in ["",".1",".012",".9995",".000000001",".123456789"] {
                    for zone in ["Z","+05:45","-12:00"] { inputs.append(head + fraction + zone) }
                }
            }
        }
    }
    for i in 0..<2200 {
        let nearEpoch = i.isMultiple(of: 2) ? "2000-12" : "2001-01"
        let sign = i.isMultiple(of: 3) ? "-" : "+"
        inputs.append(String(format: "%@-%02dT%02d:%02d:%02d.%09d%@%02d:%02d",
                             nearEpoch, 1+i%31, i%24, i%60, (i*19)%60, (i*403957)%1_000_000_000,
                             sign, i%14, (i*13)%60))
    }
    for part in ["24:00:00","23:59:60","12:60:00","::","00:00:00.1234567890","00:00:00.","00:00:00"] {
        for zone in ["Z","z","+1400","+14:01","-00:30","+18:00","+09:7","", "Z trailing","Z\u{0000}tail"] {
            inputs.append("2001-01-01T" + part + zone)
        }
    }
    var rows: [String] = []
    for value in inputs { rows.append(value + "\t" + bits(TranscriptLines.parse(value)) + "\t" + bits(parsed(event(value)))) }
    try FileHandle.standardOutput.write(contentsOf: JSONEncoder().encode(rows))
}
