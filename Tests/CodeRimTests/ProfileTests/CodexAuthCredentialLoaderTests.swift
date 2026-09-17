import Darwin
import Foundation
import XCTest
@testable import CodeRim

final class CodexAuthCredentialLoaderTests: XCTestCase {
    func testLoadsOnlyRequiredCredentialFieldsFromOwnerOnlyRegularFile() throws {
        let fixture = try makeFixture(
            """
            {
              "tokens": {
                "access_token": "access-token",
                "account_id": "account-id",
                "refresh_token": "must-not-be-decoded",
                "id_token": "must-not-be-decoded"
              },
              "api_key": "must-not-be-decoded"
            }
            """
        )
        defer { fixture.remove() }

        let credential = try CodexAuthCredentialLoader(authFileURL: fixture.file).load()

        XCTAssertEqual(
            credential,
            ProfileCredential(accessToken: "access-token", accountID: "account-id")
        )
    }

    func testReadOnlyOwnerPermissionIsAccepted() throws {
        let fixture = try makeFixture(validJSON, permissions: 0o400)
        defer { fixture.remove() }

        XCTAssertNoThrow(try CodexAuthCredentialLoader(authFileURL: fixture.file).load())
    }

    func testGroupOrOtherPermissionsAreRejected() throws {
        for permissions in [mode_t(0o640), mode_t(0o604), mode_t(0o700)] {
            let fixture = try makeFixture(validJSON, permissions: permissions)
            defer { fixture.remove() }

            XCTAssertThrowsError(try CodexAuthCredentialLoader(authFileURL: fixture.file).load()) {
                XCTAssertEqual($0 as? ProfileUsageError, .unsafeCredentialFile)
            }
        }
    }

    func testSymlinkIsRejectedWithoutFollowingIt() throws {
        let fixture = try makeFixture(validJSON)
        defer { fixture.remove() }
        let symlink = fixture.directory.appendingPathComponent("linked-auth.json")
        try FileManager.default.createSymbolicLink(at: symlink, withDestinationURL: fixture.file)

        XCTAssertThrowsError(try CodexAuthCredentialLoader(authFileURL: symlink).load()) {
            XCTAssertEqual($0 as? ProfileUsageError, .unsafeCredentialFile)
        }
    }

    func testOversizedFileIsRejectedBeforeJSONDecoding() throws {
        let fixture = try makeFixture(
            String(repeating: "x", count: CodexAuthCredentialLoader.maximumFileSize + 1)
        )
        defer { fixture.remove() }

        XCTAssertThrowsError(try CodexAuthCredentialLoader(authFileURL: fixture.file).load()) {
            XCTAssertEqual($0 as? ProfileUsageError, .unsafeCredentialFile)
        }
    }

    func testMalformedMissingAndUnsafeValuesAreRejected() throws {
        let fixtures = [
            "{}",
            "{\"tokens\":{\"access_token\":\"token\"}}",
            "{\"tokens\":{\"access_token\":\"token\\nvalue\",\"account_id\":\"account\"}}",
            "{\"tokens\":{\"access_token\":\"token\",\"account_id\":\" account\"}}",
            "{\"tokens\":{\"access_token\":\"\",\"account_id\":\"account\"}}",
            "{\"tokens\":{\"access_token\":\"token\",\"account_id\":\"\"}}",
            "{\"tokens\":{\"access_token\":\"\(String(repeating: "t", count: CodexAuthCredentialLoader.maximumAccessTokenLength + 1))\",\"account_id\":\"account\"}}",
            "{\"tokens\":{\"access_token\":\"token\",\"account_id\":\"\(String(repeating: "a", count: CodexAuthCredentialLoader.maximumAccountIDLength + 1))\"}}"
        ]

        for contents in fixtures {
            let fixture = try makeFixture(contents)
            defer { fixture.remove() }

            XCTAssertThrowsError(try CodexAuthCredentialLoader(authFileURL: fixture.file).load()) {
                XCTAssertEqual($0 as? ProfileUsageError, .invalidCredentials)
            }
        }
    }

    private var validJSON: String {
        "{\"tokens\":{\"access_token\":\"access-token\",\"account_id\":\"account-id\"}}"
    }

    private func makeFixture(
        _ contents: String,
        permissions: mode_t = 0o600
    ) throws -> AuthFixture {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodeRimProfileTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
        let file = directory.appendingPathComponent("auth.json")
        try Data(contents.utf8).write(to: file, options: .withoutOverwriting)
        guard chmod(file.path, permissions) == 0 else {
            throw CocoaError(.fileWriteNoPermission)
        }
        return AuthFixture(directory: directory, file: file)
    }
}

private struct AuthFixture {
    let directory: URL
    let file: URL

    func remove() {
        try? FileManager.default.removeItem(at: directory)
    }
}
