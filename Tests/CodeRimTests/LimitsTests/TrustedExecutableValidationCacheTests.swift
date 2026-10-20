import Foundation
import XCTest
@testable import CodeRim

final class TrustedExecutableValidationCacheTests: XCTestCase {
    func testSuccessfulValidationIsReusedWhileExecutableIsUnchanged() throws {
        let executable = try makeExecutable()
        let cache = TrustedExecutableValidationCache()
        var validationCount = 0

        XCTAssertTrue(cache.validate(executable) { _ in
            validationCount += 1
            return true
        })
        XCTAssertTrue(cache.validate(executable) { _ in
            validationCount += 1
            return true
        })
        XCTAssertEqual(validationCount, 1)
    }

    func testFailedValidationIsNeverCached() throws {
        let executable = try makeExecutable()
        let cache = TrustedExecutableValidationCache()
        var validationCount = 0

        for _ in 0..<2 {
            XCTAssertFalse(cache.validate(executable) { _ in
                validationCount += 1
                return false
            })
        }
        XCTAssertEqual(validationCount, 2)
    }

    func testExecutableReplacementInvalidatesSuccessfulValidation() throws {
        let executable = try makeExecutable(contents: "first")
        let cache = TrustedExecutableValidationCache()
        var validationCount = 0

        XCTAssertTrue(cache.validate(executable) { _ in
            validationCount += 1
            return true
        })
        try Data("replacement".utf8).write(to: executable, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        XCTAssertTrue(cache.validate(executable) { _ in
            validationCount += 1
            return true
        })
        XCTAssertEqual(validationCount, 2)
    }

    private func makeExecutable(contents: String = "fixture") throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("coderim-trusted-executable-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        let executable = directory.appendingPathComponent("codex")
        try Data(contents.utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        return executable
    }
}
