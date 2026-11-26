import Darwin
import Foundation

private let key = Data("synthetic-fingerprint".utf8)
private let args = CommandLine.arguments
private let handle = try FileHandle(forReadingFrom: URL(fileURLWithPath: args[1]))
private let fd = handle.fileDescriptor
private let chunk: Int64 = 256 * 1024

private func observe(_ name: String, _ state: inout SourceFingerprintAccumulator,
                     fd: Int32, target: Int64, budget: Int64, legacy: Int64?) {
    let outcome: String
    do {
        let result = try state.advance(fileDescriptor: fd, to: target, maximumBytes: budget)
        outcome = "ok/\(result.bytesRead)/\(result.reachedTarget)"
    } catch is CancellationError {
        outcome = "cancelled"
    } catch SourceReadError.sourceChangedDuringRead {
        outcome = "changed"
    } catch {
        fatalError("Unexpected reader error: \(error)")
    }
    print(name, outcome, state.authenticatedOffset, state.fingerprint,
          legacy.map(String.init) ?? "-", state.legacyFingerprint ?? "-", separator: "\t")
}

private func grid() async throws {
    let size = Int64(try handle.seekToEnd())
    try handle.seek(toOffset: 23)
    for legacy in [nil, 0, chunk + 1] as [Int64?] {
        for target in [-1, 0, 1, chunk - 1, chunk, chunk + 1, 2 * chunk, size, size + 1, size + chunk] {
            for budget in [-1, 0, 1, chunk - 1, chunk, chunk + 1, size + chunk] {
                var state = SourceFingerprintAccumulator(key: key, legacyOffset: legacy)
                observe("first", &state, fd: fd, target: target, budget: budget, legacy: legacy)
                var fork = state
                observe("fork", &fork, fd: fd, target: size, budget: chunk + 1, legacy: legacy)
                observe("resume", &state, fd: fd, target: target, budget: size + chunk, legacy: legacy)
                state.discardLegacyFingerprint()
                observe("discarded", &state, fd: fd, target: 0, budget: 1, legacy: nil)
            }
        }
    }
    var bad = SourceFingerprintAccumulator(key: key, legacyOffset: size)
    observe("invalid-fd", &bad, fd: -1, target: 1, budget: 1, legacy: size)
    observe("empty-invalid-fd", &bad, fd: -1, target: 1, budget: 0, legacy: size)
    let canceled = Task {
        var state = SourceFingerprintAccumulator(key: key)
        observe("before-cancel", &state, fd: fd, target: chunk, budget: chunk, legacy: nil)
        withUnsafeCurrentTask { $0?.cancel() }
        observe("cancelled-empty", &state, fd: fd, target: chunk, budget: 1, legacy: nil)
        observe("cancelled-read", &state, fd: fd, target: chunk + 1, budget: chunk, legacy: nil)
    }
    await canceled.value
    guard try handle.offset() == 23 else { fatalError("Reader moved the caller's descriptor") }
}

if args.count == 2 {
    try await grid()
} else {
    let count = Int64(args[2])!
    let loops = Int(args[3])!
    let legacy = args[4] == "dual" ? count : nil
    var elapsed: UInt64 = 0
    var last = ""
    for _ in 0..<loops {
        var state = SourceFingerprintAccumulator(key: key, legacyOffset: legacy)
        let clock = DispatchTime.now().uptimeNanoseconds
        let result = try state.advance(fileDescriptor: fd, to: count, maximumBytes: count)
        elapsed += DispatchTime.now().uptimeNanoseconds - clock
        precondition(result.reachedTarget && result.bytesRead == count)
        last = state.fingerprint + (state.legacyFingerprint ?? "")
    }
    print(last, Double(elapsed) / Double(loops))
}
