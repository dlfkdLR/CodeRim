import Foundation
import CCodeRimAccounting

let inputs = try JSONDecoder().decode([Data].self, from: FileHandle.standardInput.readDataToEndOfFile())
if CommandLine.arguments.dropFirst().first == "bench" {
    let data = inputs[0]
    func sample(_ original: Bool) -> (Int, Double) {
        var checksum = 0
        let iterations = max(60, min(3000, 1_000_000 / max(1, data.count)))
        let started = DispatchTime.now().uptimeNanoseconds
        if original {
            for _ in 0..<iterations {
                for row in OriginalUsage.models(in: data) { checksum &+= row.name.utf8.count &+ row.megabytes }
            }
        } else {
            for _ in 0..<iterations {
                for row in OllamaLocalUsage.models(in: data) { checksum &+= row.name.utf8.count &+ row.megabytes }
            }
        }
        return (checksum, Double(DispatchTime.now().uptimeNanoseconds - started) / Double(iterations))
    }
    var reference = [Double](), rust = [Double]()
    for i in 0...7 {
        let first = sample(i.isMultiple(of: 2)), second = sample(!i.isMultiple(of: 2))
        precondition(first.0 == second.0)
        if i > 0 {
            reference.append(i.isMultiple(of: 2) ? first.1 : second.1)
            rust.append(i.isMultiple(of: 2) ? second.1 : first.1)
        }
    }
    let output = ["reference": reference, "rust": rust]
    print(String(decoding: try JSONEncoder().encode(output), as: UTF8.self))
} else {
    var direct = 0
    for (i, data) in inputs.enumerated() {
        let before = OriginalUsage.models(in: data)
        let after = OllamaLocalUsage.models(in: data)
        precondition(before.count == after.count, "Different count at fixture \(i)")
        for (a, b) in zip(before, after) {
            precondition(a.name.utf8.elementsEqual(b.name.utf8) && a.megabytes == b.megabytes, "Different row at fixture \(i)")
        }
        var called = false
        let ok = data.withUnsafeBytes { bytes in
            cr_ollama_local_models(bytes.bindMemory(to: UInt8.self).baseAddress, bytes.count, { context, _, _ in
                context!.assumingMemoryBound(to: Bool.self).pointee = true
            }, &called)
        }
        precondition((ok == 1) == called)
        if i < 6 { precondition(ok == 1) }
        if i == 6 { precondition(ok == 0) }
        if i == 7 { precondition(ok == 1) }
        direct += Int(ok)
    }
    print("\(inputs.count) model lists matched, \(direct) handled by Rust")
}
