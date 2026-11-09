import Darwin
import Foundation
import XCTest
@testable import CodeRim

final class CredentialACLCompatibilityTests: XCTestCase {
    func testInheritedGrantsBlockReplacementAndRestoreBeforeCreatingStagingFile() throws {
        for originalExists in [true, false] {
            for grant in ["everyone allow read,file_inherit,directory_inherit",
                          "everyone allow read,only_inherit,file_inherit",
                          "everyone allow write,writeattr,writesecurity"] {
                let fixture = try Fixture(originalExists: originalExists)
                defer { fixture.remove() }
                let before = try fixture.contents()
                try fixture.addACL(grant, to: fixture.directory)
                XCTAssertThrowsError(try fixture.login.replace(with: fixture.data,
                                                              expecting: originalExists ? fixture.data : nil)) {
                    XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
                }
                XCTAssertThrowsError(try CodexAccountOperationLock.acquire(directory: fixture.directory))
                XCTAssertEqual(try fixture.contents(), before, "No lock or staging file may be created")
                if originalExists { XCTAssertEqual(try Data(contentsOf: fixture.auth), fixture.data) }
                XCTAssertTrue(try fixture.aclText(fixture.directory).contains("allow"), "Do not rewrite the user's ACL")
            }
        }
    }

    func testBothCredentialReadersRejectExtendedReadAndWriteGrantsAtMode600() throws {
        for grant in ["everyone allow read", "everyone allow write", "everyone allow writesecurity"] {
            let fixture = try Fixture()
            defer { fixture.remove() }
            try fixture.addACL(grant, to: fixture.auth)
            var info = stat()
            XCTAssertEqual(lstat(fixture.auth.path, &info), 0)
            XCTAssertEqual(info.st_mode & 0o777, 0o600, "ACL grants are independent of mode bits")
            XCTAssertThrowsError(try fixture.login.read()) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
            }
            XCTAssertThrowsError(try CodexAuthCredentialLoader(authFileURL: fixture.auth).load()) {
                XCTAssertEqual($0 as? ProfileUsageError, .unsafeCredentialFile)
            }
        }
    }

    func testOperationLockRejectsExtendedGrantOnExistingLock() throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        let lock = fixture.directory.appendingPathComponent(".codexmeter-accounts.lock")
        XCTAssertTrue(FileManager.default.createFile(atPath: lock.path, contents: Data(), attributes: [.posixPermissions: 0o600]))
        try fixture.addACL("everyone allow write", to: lock)
        XCTAssertThrowsError(try CodexAccountOperationLock.acquire(directory: fixture.directory)) {
            XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
        }
    }

    func testOwnerReadOnlyAndDenyOnlyACLsRemainReadable() throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.addACL("everyone deny delete", to: fixture.auth)
        XCTAssertEqual(chmod(fixture.auth.path, 0o400), 0)
        XCTAssertEqual(try fixture.login.read(), fixture.data)
        XCTAssertEqual(try CodexAuthCredentialLoader(authFileURL: fixture.auth).load().accountID, "synthetic-account")
    }

    func testOwnerGrantsAndDenyOnlyDirectorySupportPrivatePublication() throws {
        for rule in ["everyone deny delete", "user:\(NSUserName()) allow read,file_inherit,directory_inherit"] {
            let fixture = try Fixture()
            defer { fixture.remove() }
            try fixture.addACL(rule, to: fixture.directory)
            try fixture.login.replace(with: fixture.data, expecting: fixture.data)
            XCTAssertEqual(try fixture.login.read(), fixture.data)
            XCTAssertNoThrow(try CodexAuthCredentialLoader(authFileURL: fixture.auth).load())
            var info = stat()
            XCTAssertEqual(lstat(fixture.auth.path, &info), 0)
            XCTAssertEqual(info.st_mode & 0o777, 0o600)
            XCTAssertEqual(try fixture.contents(), ["auth.json"])
        }
    }

    func testRegistrationRejectsUnsafeInheritanceBeforeCreatingTemporaryHome() throws {
        let fixture = try Fixture(originalExists: false)
        defer { fixture.remove() }
        try fixture.addACL("everyone allow read,execute,file_inherit,directory_inherit", to: fixture.directory)
        XCTAssertThrowsError(try CredentialFileSafety.makeTemporaryHome(in: fixture.directory)) {
            XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
        }
        XCTAssertEqual(try fixture.contents(), [])
    }

    func testRegistrationCreatesPrivateHomeWithoutChangingParentACL() throws {
        let fixture = try Fixture(originalExists: false)
        defer { fixture.remove() }
        try fixture.addACL("everyone deny delete", to: fixture.directory)
        let originalACL = try fixture.aclText(fixture.directory)
        let home = try CredentialFileSafety.makeTemporaryHome(in: fixture.directory)
        XCTAssertEqual(home.deletingLastPathComponent().standardizedFileURL, fixture.directory.standardizedFileURL)
        var info = stat()
        XCTAssertEqual(lstat(home.path, &info), 0)
        XCTAssertEqual(info.st_uid, geteuid())
        XCTAssertEqual(info.st_mode & 0o777, 0o700)
        XCTAssertEqual(try fixture.aclText(fixture.directory), originalACL)
        try CodexLoginFile(directory: home).replace(with: fixture.data, expecting: nil)
        XCTAssertEqual(try CodexLoginFile(directory: home).read(), fixture.data)
    }

    func testACLInspectionFailsClosedOnInvalidDescriptor() {
        XCTAssertTrue(CredentialFileSafety.hasPermissiveACL(fileDescriptor: -1))
    }

    func testRegistrationRejectsSymlinkAndSharedWritableParents() throws {
        let fixture = try Fixture(originalExists: false)
        defer { fixture.remove() }
        let alias = fixture.directory.appendingPathComponent("alias")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: fixture.directory)
        XCTAssertThrowsError(try CredentialFileSafety.makeTemporaryHome(in: alias))
        XCTAssertEqual(chmod(fixture.directory.path, 0o777), 0)
        XCTAssertThrowsError(try CredentialFileSafety.makeTemporaryHome(in: fixture.directory))
        XCTAssertEqual(try fixture.contents(), ["alias"])
    }
}

private struct Fixture {
    let directory: URL
    let data: Data
    var auth: URL { directory.appendingPathComponent("auth.json") }
    var login: CodexLoginFile { CodexLoginFile(directory: directory) }

    init(originalExists: Bool = true) throws {
        let claims = try JSONSerialization.data(withJSONObject: ["sub": "synthetic", "email": "test@example.test"])
            .base64EncodedString()
        data = try JSONSerialization.data(withJSONObject: ["tokens": [
            "id_token": "e30.\(claims).signature", "access_token": "synthetic-access",
            "refresh_token": "synthetic-refresh", "account_id": "synthetic-account"
        ]])
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("CodeRimACLTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o755])
        if originalExists {
            guard FileManager.default.createFile(atPath: auth.path, contents: data, attributes: [.posixPermissions: 0o600])
            else { throw CocoaError(.fileWriteNoPermission) }
        }
    }

    func contents() throws -> [String] { try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted() }

    func addACL(_ entry: String, to url: URL) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/chmod")
        process.arguments = ["+a", entry, url.path]
        try process.run(); process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw CocoaError(.fileWriteNoPermission) }
    }

    func aclText(_ url: URL) throws -> String {
        guard let acl = acl_get_file(url.path, ACL_TYPE_EXTENDED) else { throw CocoaError(.fileReadUnknown) }
        defer { acl_free(UnsafeMutableRawPointer(acl)) }
        guard let text = acl_to_text(acl, nil) else { throw CocoaError(.fileReadUnknown) }
        defer { acl_free(text) }
        return String(cString: text)
    }

    func remove() {
        // Strip only our synthetic deny-delete ACLs so fixture cleanup succeeds.
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/chmod")
        process.arguments = ["-RN", directory.path]
        do { try process.run(); process.waitUntilExit() } catch { }
        try? FileManager.default.removeItem(at: directory)
    }
}
