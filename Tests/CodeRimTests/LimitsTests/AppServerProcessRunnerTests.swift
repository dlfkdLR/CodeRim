import Foundation
import XCTest
@testable import CodeRim

final class AppServerProcessRunnerTests: XCTestCase {
    private let input = Data("{}\n{}\n".utf8)

    func testOversizedStandardOutputStopsAtTheConfiguredBound() async {
        await assertResponseTooLarge(
            script: "IFS= read -r first; /usr/bin/yes x | /usr/bin/head -c 2048"
        )
    }

    func testOversizedStandardErrorStopsAtTheConfiguredBound() async {
        await assertResponseTooLarge(
            script: "IFS= read -r first; printf '{\"id\":1}\\n{\"id\":2,\"result\":{}}\\n'; "
                + "/usr/bin/yes x | /usr/bin/head -c 2048 >&2"
        )
    }

    func testImmediateExitRepeatedlyCompletes() async throws {
        for _ in 0..<20 {
            let output = try await run("IFS= read -r first; printf '{\"id\":1}\\n'; IFS= read -r second; printf '{\"id\":2,\"result\":{}}\\n'")
            XCTAssertTrue(String(decoding: output, as: UTF8.self).contains("result"))
        }
    }

    func testResponseThenDelayedExitTimesOutAndNextReadSucceeds() async throws {
        let start = ContinuousClock.now
        do {
            _ = try await run("trap '' TERM; printf '{\"id\":1}\\n{\"id\":2}\\n'; while :; do :; done", timeout: .milliseconds(100))
            XCTFail("Expected timeout")
        } catch { XCTAssertEqual(error as? AccountLimitError, .timedOut) }
        XCTAssertLessThan(start.duration(to: .now), .seconds(2))
        _ = try await run("printf '{\"id\":1}\\n{\"id\":2}\\n'")
    }

    func testCancellationInterruptsSilentProcess() async throws {
        let start = ContinuousClock.now
        let task = Task { try await AppServerProcessRunner().run(
            executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["2"],
            standardInput: Data("{}\n{}\n".utf8), timeout: .seconds(5), maximumOutputBytes: 1024) }
        try await Task.sleep(for: .milliseconds(80))
        task.cancel()
        do { _ = try await task.value; XCTFail("Expected cancellation") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertLessThan(start.duration(to: .now), .seconds(1))
    }

    func testInheritedOutputPipeDoesNotWaitForDescendantEOF() async throws {
        let start = ContinuousClock.now
        _ = try await run("printf '{\"id\":1}\\n{\"id\":2}\\n'; /bin/sleep 2 &", timeout: .seconds(1))
        XCTAssertLessThan(start.duration(to: .now), .seconds(1))
    }

    func testClaudeRunnerCompletesAndBoundsSilentAndOversizedCommands() async throws {
        for _ in 0..<10 {
            let output = try await ClaudeCommandRunner.run(executable: URL(fileURLWithPath: "/usr/bin/printf"),
                arguments: ["ready"], timeout: .seconds(1), maximumOutputBytes: 1024)
            XCTAssertEqual(String(decoding: output, as: UTF8.self), "ready")
        }
        do {
            _ = try await ClaudeCommandRunner.run(executable: URL(fileURLWithPath: "/bin/sleep"),
                arguments: ["2"], timeout: .milliseconds(100), maximumOutputBytes: 1024)
            XCTFail("Expected timeout")
        } catch { XCTAssertEqual(error as? ClaudeIntegrationError, .timedOut) }
        do {
            _ = try await ClaudeCommandRunner.run(executable: URL(fileURLWithPath: "/usr/bin/yes"),
                arguments: ["x"], timeout: .seconds(1), maximumOutputBytes: 1024)
            XCTFail("Expected bounded output")
        } catch { XCTAssertEqual(error as? ClaudeIntegrationError, .responseTooLarge) }
    }

    private func run(_ script: String, timeout: Duration = .seconds(2)) async throws -> Data {
        try await AppServerProcessRunner().run(executable: URL(fileURLWithPath: "/bin/sh"),
            arguments: ["-c", script], standardInput: input, timeout: timeout, maximumOutputBytes: 4096)
    }

    private func assertResponseTooLarge(script: String) async {
        do {
            _ = try await AppServerProcessRunner().run(
                executable: URL(fileURLWithPath: "/bin/sh"),
                arguments: ["-c", script],
                standardInput: input,
                timeout: .seconds(2),
                maximumOutputBytes: 1_024
            )
            XCTFail("Expected bounded output rejection")
        } catch {
            XCTAssertEqual(error as? AccountLimitError, .responseTooLarge)
        }
    }
}
