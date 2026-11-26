import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

let inputs = try JSONDecoder().decode([Data].self, from: FileHandle.standardInput.readDataToEndOfFile())

func parse(_ data: Data) throws -> OllamaUsage.Result {
    #if CODERIM_RUST_ACCOUNTING
    return try OllamaUsage.parse(data)
    #else
    return try OllamaUsage.parse(String(data: data, encoding: .utf8) ?? "")
    #endif
}

struct Reading: Encodable {
    let headline: String?
    let windows: [LimitWindow]
    let fractionBits: [UInt64?]
}

if let loops = CommandLine.arguments.dropFirst().first.flatMap(Int.init) {
    func run(_ iterations: Int) -> Int {
        var checksum = 0
        for _ in 0..<iterations {
            for data in inputs {
                do {
                    let value = try parse(data)
                    checksum &+= value.headlineID?.utf8.count ?? 0
                    for row in value.windows {
                        checksum &+= row.label.utf8.count &+ row.id.utf8.count &+ (row.used ?? 0)
                        checksum &+= Int(truncatingIfNeeded: (row.usedFraction ?? 0).bitPattern)
                    }
                } catch { checksum &-= 1 }
            }
        }
        return checksum
    }
    _ = run(5)
    let start = DispatchTime.now().uptimeNanoseconds
    let checksum = run(loops)
    print(checksum, DispatchTime.now().uptimeNanoseconds - start)
} else if CommandLine.arguments.dropFirst().first == "coverage" {
    #if CODERIM_RUST_ACCOUNTING
    for data in inputs {
        var called = false
        let status = data.withUnsafeBytes { bytes in
            cr_ollama_quotas(bytes.bindMemory(to: UInt8.self).baseAddress, bytes.count, {
                context, _, _, _ in context!.assumingMemoryBound(to: Bool.self).pointee = true
            }, &called)
        }
        precondition(called == (status == 1))
        print(status)
    }
    #endif
} else {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    for data in inputs {
        do {
            let value = try parse(data)
            let reading = Reading(headline: value.headlineID, windows: value.windows,
                                  fractionBits: value.windows.map { $0.usedFraction?.bitPattern })
            print(String(decoding: try encoder.encode(reading), as: UTF8.self))
        } catch { print(String(describing: error)) }
    }
}
