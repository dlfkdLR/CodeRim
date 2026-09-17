import Darwin
import Foundation
import XCTest
@testable import CodeRim

/// Mode bits are not the whole story on macOS.
///
/// A credential file can read `-rw-------` and still carry an extended ACL
/// granting another principal write access, which `stat` never reports. These
/// drive the real `chmod +a` syscall rather than a stub, because the point is
/// whether the check sees what the filesystem actually did.
final class CredentialFileSafetyTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("acl-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        try XCTSkipUnless(supportsACLs(), "this filesystem does not support extended ACLs")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    func testAPlainOwnerOnlyFileIsAccepted() throws {
        let file = try makeFile()
        XCTAssertFalse(try withDescriptor(file, CredentialFileSafety.hasPermissiveACL))
    }

    /// The hole this closes: 0600 on paper, writable by anyone in practice.
    func testAFileGrantingWriteToEveryoneIsRejected() throws {
        let file = try makeFile()
        try applyACL("everyone allow write", to: file)

        // The mode bits still say private — which is exactly the problem.
        let mode = try FileManager.default.attributesOfItem(atPath: file.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(mode?.int32Value ?? 0 & 0o777, 0o600)

        XCTAssertTrue(try withDescriptor(file, CredentialFileSafety.hasPermissiveACL))
    }

    func testAFileGrantingReadToEveryoneIsRejected() throws {
        let file = try makeFile()
        try applyACL("everyone allow read", to: file)
        XCTAssertTrue(try withDescriptor(file, CredentialFileSafety.hasPermissiveACL))
    }

    /// A `deny` entry is *more* restrictive than the mode bits, and macOS adds
    /// them routinely. Refusing those would reject files safer than the ones
    /// already accepted.
    func testADenyOnlyACLIsAccepted() throws {
        let file = try makeFile()
        try applyACL("everyone deny delete", to: file)
        XCTAssertFalse(try withDescriptor(file, CredentialFileSafety.hasPermissiveACL))
    }

    func testADirectoryGrantingWriteToEveryoneIsRejected() throws {
        let directory = root.appendingPathComponent("dir", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        XCTAssertFalse(try withDescriptor(directory, CredentialFileSafety.hasPermissiveACL))

        try applyACL("everyone allow write", to: directory)
        XCTAssertTrue(try withDescriptor(directory, CredentialFileSafety.hasPermissiveACL))
    }

    /// End to end: the reader itself must refuse, not just the predicate.
    func testTheLoginReaderRefusesAFileWithAPermissiveACL() throws {
        let directory = root.appendingPathComponent("codex", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let login = directory.appendingPathComponent("auth.json")
        FileManager.default.createFile(atPath: login.path, contents: Data(#"{"tokens":{}}"#.utf8),
                                       attributes: [.posixPermissions: 0o600])

        let store = CodexLoginFile(directory: directory)
        XCTAssertNoThrow(try store.read(), "an owner-only login should be readable")

        try applyACL("everyone allow write", to: login)
        XCTAssertThrowsError(try store.read(), "a world-writable login was read anyway") { error in
            XCTAssertEqual(error as? AccountSwitchError, .unsafeFile)
        }
    }

    /// A directory can stamp its ACL onto files created inside it, so a staging
    /// file opened with `0600` is born world-writable and the mode it asked for
    /// never applied.
    ///
    /// Asserts the directory refusal, which is the deterministic half: a write
    /// into such a directory never starts. The matching check on the staging
    /// descriptor covers the race this cannot reach — an ACL added between
    /// opening the directory and creating the file — and is not reachable from
    /// a test without losing that race on purpose.
    func testAWriteIntoADirectoryWithAnInheritableACLIsRefused() throws {
        let directory = root.appendingPathComponent("inherit", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let profile = directory.appendingPathComponent(".claude.json")
        FileManager.default.createFile(atPath: profile.path, contents: Data(#"{"oauthAccount":{}}"#.utf8),
                                       attributes: [.posixPermissions: 0o600])
        let store = ClaudeProfileFile(url: profile)
        let original = try store.read()
        XCTAssertNoThrow(try store.replace(with: Data(#"{"oauthAccount":{"a":1}}"#.utf8), expecting: original),
                         "an owner-only directory should accept a write")

        try applyACL("everyone allow write,file_inherit", to: directory)

        // Confirm the premise before asserting on it: a file created here now
        // inherits the grant regardless of the mode requested.
        let probe = directory.appendingPathComponent("probe.json")
        FileManager.default.createFile(atPath: probe.path, contents: Data(),
                                       attributes: [.posixPermissions: 0o600])
        XCTAssertTrue(try withDescriptor(probe, CredentialFileSafety.hasPermissiveACL),
                      "this filesystem did not apply the inheritable ACL, so the test proves nothing")

        let current = try? store.read()
        XCTAssertThrowsError(
            try store.replace(with: Data(#"{"oauthAccount":{"b":2}}"#.utf8), expecting: current ?? nil),
            "a credential was written into a directory that grants everyone write"
        ) { error in
            XCTAssertEqual(error as? ClaudeAccountError, .unsafeFile)
        }
    }

    // MARK: helpers

    private func makeFile() throws -> URL {
        let file = root.appendingPathComponent("\(UUID().uuidString).json")
        FileManager.default.createFile(atPath: file.path, contents: Data("{}".utf8),
                                       attributes: [.posixPermissions: 0o600])
        return file
    }

    private func withDescriptor(_ url: URL, _ body: (Int32) -> Bool) throws -> Bool {
        var isDirectory: ObjCBool = false
        FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory)
        let flags = isDirectory.boolValue ? O_RDONLY | O_DIRECTORY : O_RDONLY
        let fd = open(url.path, flags | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw Failure.open }
        defer { close(fd) }
        return body(fd)
    }

    private func applyACL(_ rule: String, to url: URL) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/chmod")
        process.arguments = ["+a", rule, url.path]
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw Failure.chmod }
    }

    private func supportsACLs() -> Bool {
        guard let probe = try? makeFile() else { return false }
        defer { try? FileManager.default.removeItem(at: probe) }
        guard (try? applyACL("everyone deny delete", to: probe)) != nil else { return false }
        return true
    }

    private enum Failure: Error { case open, chmod }
}
