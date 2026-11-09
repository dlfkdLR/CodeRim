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
    private let payloadTerms = ["body", "response", "payload", "credits", "json", "token", "credential",
                                "title", "summary", ".name", ".path", "error", "description"]

    func testNoResponsePayloadIsLoggedPublicly() throws {
        var offenders: [String] = []

        for file in try swiftSources() {
            let source = try String(contentsOf: file, encoding: .utf8)
            guard source.contains("privacy: .public") else { continue }
            for (index, line) in source.split(separator: "\n", omittingEmptySubsequences: false).enumerated() {
                let text = String(line)
                guard text.contains("privacy: .public") else { continue }
                guard try !sensitivePublicFields(in: text).isEmpty else { continue }
                offenders.append("\(file.lastPathComponent):\(index + 1) — \(text.trimmingCharacters(in: .whitespaces))")
            }
        }

        XCTAssertTrue(
            offenders.isEmpty,
            "a response payload is being written to the system log in the clear:\n"
                + offenders.joined(separator: "\n")
        )
    }

    func testCountsCannotHideAnotherPublicTitleOrPath() throws {
        let source = #"log.info("\(sessions.count, privacy: .public) \(session.name, privacy: .public) \(directory.path, privacy: .public)")"#
        XCTAssertEqual(try sensitivePublicFields(in: source), ["session.name", "directory.path"])
        XCTAssertTrue(try sensitivePublicFields(in: #"log.debug("\(body.utf8.count, privacy: .public) bytes")"#).isEmpty)
        XCTAssertEqual(try sensitivePublicFields(in: #"log.error("\(error.localizedDescription, privacy: .public)")"#),
                       ["error.localizedDescription"])
    }

    private func sensitivePublicFields(in source: String) throws -> [String] {
        let interpolation = try NSRegularExpression(pattern: #"\\\((.*?),\s*privacy:\s*\.public\)"#)
        return interpolation.matches(in: source, range: NSRange(source.startIndex..., in: source)).compactMap { match in
            guard let range = Range(match.range(at: 1), in: source) else { return nil }
            let field = String(source[range]).trimmingCharacters(in: .whitespaces)
            // Inspect each field: a numeric count must not exempt a title on the same line.
            if field.hasSuffix(".count") || field.hasSuffix(".code") { return nil }
            let lowered = field.lowercased()
            return lowered == "name" || payloadTerms.contains(where: lowered.contains) ? field : nil
        }
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
