import Foundation
import XCTest

/// The unified log is not a private place.
///
/// `privacy: .public` overrides the redaction macOS applies to interpolated
/// strings by default, so whatever it wraps is written in the clear — readable
/// by other processes on the machine, and collected verbatim by `log collect`
/// and sysdiagnose, which people routinely attach to bug reports.
///
/// Three providers logged the first few hundred bytes of a usage response that
/// way while the notch was being ported. Those bodies carry plan names, spend
/// figures and team identifiers. SECURITY.md already says responses must never
/// be logged; this keeps the next debugging aid from quietly saying otherwise.
final class LogPrivacyPolicyTests: XCTestCase {
    /// Names that mean "the payload", as opposed to a count or an identifier.
    private let payloadTerms = ["body", "response", "payload", "credits", "json", "token", "credential"]

    func testNoResponsePayloadIsLoggedPublicly() throws {
        var offenders: [String] = []

        for file in try swiftSources() {
            let source = try String(contentsOf: file, encoding: .utf8)
            guard source.contains("privacy: .public") else { continue }
            for (index, line) in source.split(separator: "\n", omittingEmptySubsequences: false).enumerated() {
                let text = String(line)
                guard text.contains("privacy: .public") else { continue }
                // A count or a byte total is fine; the thing it counts is not.
                guard !text.contains(".count") else { continue }
                let lowered = text.lowercased()
                guard payloadTerms.contains(where: lowered.contains) else { continue }
                offenders.append("\(file.lastPathComponent):\(index + 1) — \(text.trimmingCharacters(in: .whitespaces))")
            }
        }

        XCTAssertTrue(
            offenders.isEmpty,
            "a response payload is being written to the system log in the clear:\n"
                + offenders.joined(separator: "\n")
        )
    }

    private func swiftSources() throws -> [URL] {
        // From this file up to the package root, then down into Sources.
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // CodeRimTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // package root
            .appendingPathComponent("Sources", isDirectory: true)
        guard let walker = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil) else {
            throw XCTSkip("no Sources directory beside this test")
        }
        return walker.compactMap { $0 as? URL }.filter { $0.pathExtension == "swift" }
    }
}
