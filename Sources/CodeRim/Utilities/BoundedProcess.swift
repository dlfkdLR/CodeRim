import Darwin
import Foundation

enum BoundedProcessError: Error { case launch, io, timedOut, outputTooLarge }

/// Pipe reads and process completion must never block a Swift concurrency worker.
/// A deadline also covers a child that keeps stdout open after its parent exits.
enum BoundedProcess {
    struct Result: Sendable {
        let output: Data
        let error: Data
        let status: Int32
    }

    static func run(
        executable: URL, arguments: [String], environment: [String: String],
        workingDirectory: URL? = nil, initialInput: Data? = nil,
        followingInput: Data? = nil,
        closeInputWhen: (@Sendable (Data) -> Bool)? = nil,
        timeout: Duration, maximumOutputBytes: Int
    ) async throws -> Result {
        try Task.checkCancellation()
        let child = Child()
        child.process.executableURL = executable
        child.process.arguments = arguments
        child.process.environment = environment
        child.process.currentDirectoryURL = workingDirectory
        child.process.standardInput = initialInput == nil ? FileHandle.nullDevice : child.input
        child.process.standardOutput = child.output
        child.process.standardError = child.error
        // Register before launching, including children that exit immediately.
        child.process.terminationHandler = { _ in }
        defer { child.stop(); child.close() }
        for fd in [child.output.fileHandleForReading.fileDescriptor,
                   child.error.fileHandleForReading.fileDescriptor,
                   child.input.fileHandleForWriting.fileDescriptor] {
            let flags = fcntl(fd, F_GETFL)
            guard flags >= 0, fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0 else { throw BoundedProcessError.io }
        }
        guard fcntl(child.input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1) == 0 else { throw BoundedProcessError.io }
        do { try child.process.run() } catch { throw BoundedProcessError.launch }
        try? child.output.fileHandleForWriting.close()
        try? child.error.fileHandleForWriting.close()
        try? child.input.fileHandleForReading.close()

        let deadline = ContinuousClock.now.advanced(by: timeout)
        var output = Data(), error = Data(), pending = initialInput ?? Data()
        var sentFollowing = followingInput == nil
        var inputClosed = initialInput == nil
        while true {
            try Task.checkCancellation()
            guard ContinuousClock.now < deadline else { throw BoundedProcessError.timedOut }
            try drain(child.output.fileHandleForReading, into: &output, maximum: maximumOutputBytes)
            try drain(child.error.fileHandleForReading, into: &error, maximum: maximumOutputBytes)
            if !sentFollowing, output.contains(0x0A) {
                pending.append(followingInput!)
                sentFollowing = true
            }
            if !inputClosed {
                if !pending.isEmpty {
                    let written = pending.withUnsafeBytes { Darwin.write(child.input.fileHandleForWriting.fileDescriptor, $0.baseAddress, $0.count) }
                    if written > 0 { pending.removeFirst(written) }
                    else if written < 0, errno != EAGAIN, errno != EINTR {
                        // An exited child may close stdin before its output is drained.
                        guard errno == EPIPE else { throw BoundedProcessError.io }
                        try? child.input.fileHandleForWriting.close()
                        inputClosed = true
                    }
                }
                if pending.isEmpty, sentFollowing, closeInputWhen?(output) ?? true {
                    try? child.input.fileHandleForWriting.close()
                    inputClosed = true
                }
            }
            if !child.process.isRunning {
                // Drain available bytes, not EOF: a descendant can inherit a pipe.
                try drain(child.output.fileHandleForReading, into: &output, maximum: maximumOutputBytes, untilEmpty: true)
                try drain(child.error.fileHandleForReading, into: &error, maximum: maximumOutputBytes, untilEmpty: true)
                return Result(output: output, error: error, status: child.process.terminationStatus)
            }
            try await Task.sleep(for: .milliseconds(10))
        }
    }

    private static func drain(_ handle: FileHandle, into data: inout Data, maximum: Int, untilEmpty: Bool = false) throws {
        var buffer = [UInt8](repeating: 0, count: 16_384)
        // Normal iterations yield even if a producer writes continuously.
        for _ in 0..<(untilEmpty ? max(1, maximum / buffer.count + 2) : 16) {
            let count = Darwin.read(handle.fileDescriptor, &buffer, min(buffer.count, max(1, maximum - data.count + 1)))
            if count == 0 { return }
            if count < 0 {
                if errno == EAGAIN { return }
                if errno == EINTR { continue }
                throw BoundedProcessError.io
            }
            guard data.count + count <= maximum else { throw BoundedProcessError.outputTooLarge }
            data.append(contentsOf: buffer.prefix(count))
        }
    }

    private final class Child: @unchecked Sendable {
        let process = Process()
        let input = Pipe(), output = Pipe(), error = Pipe()

        func stop() {
            guard process.isRunning else { return }
            // These are disposable read-only/OAuth helper children. Kill a
            // cancelled probe synchronously instead of leaving an escalation
            // timer behind when the application itself exits.
            kill(process.processIdentifier, SIGKILL)
        }

        func close() {
            for pipe in [input, output, error] {
                try? pipe.fileHandleForReading.close()
                try? pipe.fileHandleForWriting.close()
            }
        }
    }
}
