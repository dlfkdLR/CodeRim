import Darwin
import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class CodexAccountSwitchingTests: XCTestCase {
    func testAccountIdentityUsesWorkspaceAndSubjectNotEmail() throws {
        let first = try account(workspace: "workspace-a", subject: "subject-a", email: "first@example.test")
        let renamed = try account(workspace: "workspace-a", subject: "subject-a", email: "renamed@example.test")
        let otherSubject = try account(workspace: "workspace-a", subject: "subject-b", email: first.email)
        let otherWorkspace = try account(workspace: "workspace-b", subject: "subject-a", email: first.email)

        XCTAssertEqual(first.id, renamed.id)
        XCTAssertNotEqual(first.id, otherSubject.id)
        XCTAssertNotEqual(first.id, otherWorkspace.id)
        XCTAssertEqual(first.workspaceID, "workspace-a")
        XCTAssertEqual(first.email, "first@example.test")
        XCTAssertEqual(try account(workspace: "a", subject: "bc").id, "1:abc")
        XCTAssertNotEqual(try account(workspace: "a", subject: "bc").id,
                          try account(workspace: "ab", subject: "c").id)
    }

    func testInvalidCredentialShapesAreRejected() throws {
        let valid = try account().loginData
        var invalid = [Data(), Data("not JSON".utf8), Data("[]".utf8), Data("{}".utf8)]
        invalid.append(try changing(valid) { $0["OPENAI_API_KEY"] = "synthetic-api-key" })
        invalid.append(try changing(valid) { $0["auth_mode"] = "chatgptAuthTokens" })
        invalid.append(try changing(valid) { $0["auth_mode"] = "apiKey" })
        invalid.append(try changing(valid) { $0["tokens"] = NSNull() })

        for key in ["access_token", "refresh_token", "id_token", "account_id"] {
            for value in [NSNull(), "", 7] as [Any] {
                invalid.append(try changingToken(valid, key: key, value: value))
            }
        }
        for key in ["access_token", "refresh_token", "id_token"] {
            for value in ["fake token", "fake\ntoken", "fake\u{0000}token", String(repeating: "x", count: 65_537)] {
                invalid.append(try changingToken(valid, key: key, value: value))
            }
        }
        for malformedJWT in ["one-part", "two.parts", "a.%%%.c", "a.W10.c", "a.e30.c", "a.b.c.d"] {
            invalid.append(try changingToken(valid, key: "id_token", value: malformedJWT))
        }
        invalid.append(try changingToken(valid, key: "account_id", value: "workspace\nunsafe"))
        invalid.append(try changingToken(valid, key: "account_id", value: String(repeating: "a", count: 257)))
        invalid.append(try loginData(claims: ["email": "account@example.test"]))
        invalid.append(try loginData(claims: ["sub": "subject-a"]))
        invalid.append(try loginData(claims: ["sub": "subject-a", "email": "bad\n@example.test"]))
        invalid.append(try loginData(claims: ["sub": "", "email": "account@example.test"]))
        invalid.append(try loginData(claims: ["sub": "subject-a", "email": String(repeating: "a", count: 257)]))
        invalid.append(try loginData(claims: [
            "sub": "subject-a", "email": "account@example.test",
            "https://api.openai.com/auth": ["chatgpt_account_id": "different-workspace"]
        ]))
        invalid.append(Data(repeating: 0x20, count: 262_145))

        for (index, data) in invalid.enumerated() {
            XCTAssertThrowsError(try SavedCodexAccount(loginData: data), "Invalid fixture \(index)") {
                XCTAssertEqual($0 as? AccountSwitchError, .invalidLogin, "Invalid fixture \(index)")
            }
        }
    }

    func testNullAPIKeyAndUnknownMetadataDoNotInvalidateChatGPTLogin() throws {
        let data = try changing(account().loginData) {
            $0["OPENAI_API_KEY"] = NSNull()
            $0["future_metadata"] = ["synthetic": true]
        }

        XCTAssertEqual(try SavedCodexAccount(loginData: data).loginData, data)
    }

    func testVaultRejectsOversizedEncodedPayloadBeforeKeychainMutation() throws {
        let accounts = try (0..<12).map { index -> SavedCodexAccount in
            let base = try changing(account(workspace: "workspace-\(index)").loginData) { $0["padding"] = "" }
            let data = try changing(base) { $0["padding"] = String(repeating: "x", count: 262_144 - base.count) }
            XCTAssertEqual(data.count, 262_144)
            return try SavedCodexAccount(loginData: data)
        }
        XCTAssertGreaterThan(try JSONEncoder().encode(accounts).count, 4_194_304)
        // Pure encoding boundary: never instantiate or access the real Keychain.
        XCTAssertThrowsError(try KeychainAccountVault.encodedForStorage(accounts)) {
            XCTAssertEqual($0 as? AccountSwitchError, .vaultFull)
        }
        let smaller = Array(accounts.prefix(11))
        let encoded = try KeychainAccountVault.encodedForStorage(smaller)
        XCTAssertEqual(try JSONDecoder().decode([SavedCodexAccount].self, from: encoded), smaller)
    }

    func testLoginReadAcceptsPrivateAndReadOnlyOwnerFiles() throws {
        let original = try account().loginData
        for permissions in [mode_t(0o600), mode_t(0o400)] {
            let fixture = try AccountSwitchFileFixture(data: original)
            defer { fixture.remove() }
            XCTAssertEqual(chmod(fixture.auth.path, permissions), 0)

            XCTAssertEqual(try fixture.login.read(), original)
        }
    }

    func testLoginReadAndReplaceRejectCredentialSymlinkWithoutChangingTarget() throws {
        let original = try account().loginData
        let replacement = try account(workspace: "workspace-b").loginData
        let fixture = try AccountSwitchFileFixture(data: original)
        defer { fixture.remove() }
        let backing = fixture.directory.appendingPathComponent("backing.json")
        try FileManager.default.moveItem(at: fixture.auth, to: backing)
        try FileManager.default.createSymbolicLink(at: fixture.auth, withDestinationURL: backing)

        XCTAssertThrowsError(try fixture.login.read())
        XCTAssertThrowsError(try fixture.login.replace(with: replacement, expecting: original))
        XCTAssertEqual(try Data(contentsOf: backing), original)
        XCTAssertEqual(try fixture.contents(), ["auth.json", "backing.json"])
    }

    func testLoginReadAndReplaceRejectDirectorySymlink() throws {
        let original = try account().loginData
        let fixture = try AccountSwitchFileFixture(data: original)
        defer { fixture.remove() }
        let alias = fixture.directory.appendingPathComponent("directory-alias")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: fixture.directory)
        let login = CodexLoginFile(directory: alias)

        XCTAssertThrowsError(try login.read()) { XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile) }
        XCTAssertThrowsError(try login.replace(with: original, expecting: original)) {
            XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
        }
        XCTAssertEqual(try fixture.login.read(), original)
    }

    func testLoginRejectsGroupAndOtherFilePermissions() throws {
        let original = try account().loginData
        for permissions in [mode_t(0o640), mode_t(0o604), mode_t(0o610), mode_t(0o601)] {
            let fixture = try AccountSwitchFileFixture(data: original)
            defer { fixture.remove() }
            XCTAssertEqual(chmod(fixture.auth.path, permissions), 0)

            XCTAssertThrowsError(try fixture.login.read()) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
            }
            XCTAssertThrowsError(try fixture.login.replace(with: original, expecting: original))
            XCTAssertEqual(try Data(contentsOf: fixture.auth), original)
        }
    }

    func testLoginAndLockRejectWritableSharedDirectory() throws {
        for permissions in [mode_t(0o770), mode_t(0o702)] {
            let fixture = try AccountSwitchFileFixture(data: account().loginData)
            defer { fixture.remove() }
            XCTAssertEqual(chmod(fixture.directory.path, permissions), 0)

            XCTAssertThrowsError(try fixture.login.read()) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
            }
            XCTAssertThrowsError(try CodexAccountOperationLock.acquire(directory: fixture.directory)) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
            }
        }
    }

    func testLoginReadAndReplaceRejectHardLinkedCredential() throws {
        let original = try account().loginData
        let fixture = try AccountSwitchFileFixture(data: original)
        defer { fixture.remove() }
        let alias = fixture.directory.appendingPathComponent("hard-link.json")
        XCTAssertEqual(link(fixture.auth.path, alias.path), 0)

        XCTAssertThrowsError(try fixture.login.read()) { XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile) }
        XCTAssertThrowsError(try fixture.login.replace(with: original, expecting: original))
        XCTAssertEqual(try Data(contentsOf: alias), original)
    }

    func testLoginRejectsNonregularFilesWithoutBlocking() throws {
        let directoryFixture = try AccountSwitchFileFixture()
        defer { directoryFixture.remove() }
        try FileManager.default.createDirectory(at: directoryFixture.auth, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        XCTAssertThrowsError(try directoryFixture.login.read()) {
            XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
        }

        let pipeFixture = try AccountSwitchFileFixture()
        defer { pipeFixture.remove() }
        XCTAssertEqual(mkfifo(pipeFixture.auth.path, 0o600), 0)
        XCTAssertThrowsError(try pipeFixture.login.read()) {
            XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
        }
    }

    func testLoginReadIsBoundedAndRejectsEmptyFiles() throws {
        let maximum = Data(repeating: 0x20, count: 262_144)
        let boundary = try AccountSwitchFileFixture(data: maximum)
        defer { boundary.remove() }
        XCTAssertEqual(try boundary.login.read(), maximum)

        for data in [Data(), Data(repeating: 0x20, count: 262_145)] {
            let fixture = try AccountSwitchFileFixture(data: data)
            defer { fixture.remove() }
            XCTAssertThrowsError(try fixture.login.read()) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
            }
        }
    }

    func testReplacementUsesNewPrivateInodeAndPreservesOpenOriginal() throws {
        let original = try account().loginData
        let replacement = try account(workspace: "workspace-b").loginData
        let fixture = try AccountSwitchFileFixture(data: original)
        defer { fixture.remove() }
        let oldDescriptor = open(fixture.auth.path, O_RDONLY | O_CLOEXEC)
        guard oldDescriptor >= 0 else { throw CocoaError(.fileReadNoPermission) }
        defer { close(oldDescriptor) }
        var originalInfo = stat()
        XCTAssertEqual(fstat(oldDescriptor, &originalInfo), 0)

        try fixture.login.replace(with: replacement, expecting: original)

        XCTAssertEqual(try fixture.login.read(), replacement)
        var buffer = [UInt8](repeating: 0, count: original.count + 1)
        let count = Darwin.read(oldDescriptor, &buffer, buffer.count)
        XCTAssertEqual(count, original.count)
        XCTAssertEqual(Data(buffer.prefix(max(0, count))), original)
        var replacementInfo = stat()
        XCTAssertEqual(lstat(fixture.auth.path, &replacementInfo), 0)
        XCTAssertNotEqual(originalInfo.st_ino, replacementInfo.st_ino)
        XCTAssertEqual(replacementInfo.st_mode & 0o777, 0o600)
        XCTAssertEqual(replacementInfo.st_nlink, 1)
        XCTAssertEqual(replacementInfo.st_uid, getuid())
        var directoryInfo = stat()
        XCTAssertEqual(lstat(fixture.directory.path, &directoryInfo), 0)
        XCTAssertEqual(directoryInfo.st_mode & 0o777, 0o700)
        XCTAssertEqual(try fixture.contents(), ["auth.json"])
    }

    func testReplacementCompareAndSwapRejectsStaleOriginalWithoutMutation() throws {
        let stale = try account(revision: "stale").loginData
        let current = try account(revision: "current").loginData
        let replacement = try account(workspace: "workspace-b").loginData
        let fixture = try AccountSwitchFileFixture(data: current)
        defer { fixture.remove() }
        var before = stat()
        XCTAssertEqual(lstat(fixture.auth.path, &before), 0)

        XCTAssertThrowsError(try fixture.login.replace(with: replacement, expecting: stale)) {
            XCTAssertEqual($0 as? AccountSwitchError, .changedLogin)
        }

        XCTAssertEqual(try fixture.login.read(), current)
        var after = stat()
        XCTAssertEqual(lstat(fixture.auth.path, &after), 0)
        XCTAssertEqual(before.st_ino, after.st_ino)
        XCTAssertEqual(try fixture.contents(), ["auth.json"])
    }

    func testInvalidReplacementNeverChangesOriginalLogin() throws {
        let original = try account().loginData
        let fixture = try AccountSwitchFileFixture(data: original)
        defer { fixture.remove() }

        XCTAssertThrowsError(try fixture.login.replace(with: Data("{}".utf8), expecting: original)) {
            XCTAssertEqual($0 as? AccountSwitchError, .invalidLogin)
        }
        XCTAssertEqual(try fixture.login.read(), original)
        XCTAssertEqual(try fixture.contents(), ["auth.json"])
    }

    func testMissingLoginCanBeRestoredWithPrivateNoClobberPublication() throws {
        let replacement = try account().loginData
        let fixture = try AccountSwitchFileFixture()
        defer { fixture.remove() }
        XCTAssertNil(try fixture.login.read())

        try fixture.login.replace(with: replacement, expecting: nil)

        XCTAssertEqual(try fixture.login.read(), replacement)
        var info = stat()
        XCTAssertEqual(lstat(fixture.auth.path, &info), 0)
        XCTAssertEqual(info.st_mode & 0o777, 0o600)
        XCTAssertEqual(info.st_nlink, 1)
        XCTAssertEqual(try fixture.contents(), ["auth.json"])
    }

    func testAbsentSnapshotNeverOverwritesNewlyCreatedLogin() throws {
        let fixture = try AccountSwitchFileFixture()
        defer { fixture.remove() }
        XCTAssertNil(try fixture.login.read())
        let concurrent = try account(revision: "concurrent-create").loginData
        XCTAssertTrue(FileManager.default.createFile(atPath: fixture.auth.path, contents: concurrent,
                                                     attributes: [.posixPermissions: 0o600]))
        let replacement = try account(workspace: "workspace-b").loginData

        XCTAssertThrowsError(try fixture.login.replace(with: replacement, expecting: nil)) {
            XCTAssertEqual($0 as? AccountSwitchError, .changedLogin)
        }
        XCTAssertEqual(try fixture.login.read(), concurrent)
        XCTAssertEqual(try fixture.contents(), ["auth.json"])
    }

    func testDanglingSymlinkIsNotTreatedAsMissingLogin() throws {
        let fixture = try AccountSwitchFileFixture()
        defer { fixture.remove() }
        let missing = fixture.directory.appendingPathComponent("missing-target")
        try FileManager.default.createSymbolicLink(at: fixture.auth, withDestinationURL: missing)

        XCTAssertThrowsError(try fixture.login.read())
        XCTAssertThrowsError(try fixture.login.replace(with: account().loginData, expecting: nil))
        XCTAssertFalse(FileManager.default.fileExists(atPath: missing.path))
        XCTAssertEqual(try fixture.contents(), ["auth.json"])
    }

    func testOperationLockRejectsConflictAndReleasesWithLease() throws {
        let fixture = try AccountSwitchFileFixture()
        defer { fixture.remove() }
        var first: CodexAccountOperationLock? = try CodexAccountOperationLock.acquire(directory: fixture.directory)
        XCTAssertThrowsError(try CodexAccountOperationLock.acquire(directory: fixture.directory)) {
            XCTAssertEqual($0 as? AccountSwitchError, .busy)
        }
        withExtendedLifetime(first) {}
        first = nil

        let second = try CodexAccountOperationLock.acquire(directory: fixture.directory)
        var info = stat()
        XCTAssertEqual(lstat(fixture.lock.path, &info), 0)
        XCTAssertEqual(info.st_mode & 0o777, 0o600)
        XCTAssertEqual(info.st_nlink, 1)
        withExtendedLifetime(second) {}
    }

    func testOperationLockRejectsSymlinkHardlinkAndSharedPermissions() throws {
        for shape in ["symlink", "hardlink", "shared"] {
            let fixture = try AccountSwitchFileFixture()
            defer { fixture.remove() }
            let backing = fixture.directory.appendingPathComponent("lock-backing")
            XCTAssertTrue(FileManager.default.createFile(atPath: backing.path, contents: Data(),
                                                         attributes: [.posixPermissions: 0o600]))
            switch shape {
            case "symlink":
                try FileManager.default.createSymbolicLink(at: fixture.lock, withDestinationURL: backing)
            case "hardlink":
                XCTAssertEqual(link(backing.path, fixture.lock.path), 0)
            default:
                try FileManager.default.moveItem(at: backing, to: fixture.lock)
                XCTAssertEqual(chmod(fixture.lock.path, 0o640), 0)
            }
            XCTAssertThrowsError(try CodexAccountOperationLock.acquire(directory: fixture.directory), shape) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsafeFile)
            }
        }
    }

    func testPolicyAcceptsDefaultAndExplicitFileStorageButRejectsOtherBackends() throws {
        for config in [[:], ["cli_auth_credentials_store": "file"], ["cli_auth_credentials_store": NSNull()]] as [[String: Any]] {
            XCTAssertNoThrow(try CodexAccountPolicy.validate(config: config, workspaceID: "workspace-a"))
        }
        for backend in ["keyring", "auto", "ephemeral", true, 42] as [Any] {
            XCTAssertThrowsError(try CodexAccountPolicy.validate(config: ["cli_auth_credentials_store": backend],
                                                                workspaceID: "workspace-a")) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsupportedStorage)
            }
        }
    }

    func testPolicyEnforcesLoginMethodAndManagedWorkspace() throws {
        XCTAssertNoThrow(try CodexAccountPolicy.validate(config: ["forced_login_method": "chatgpt"], workspaceID: "workspace-a"))
        for method in ["api", "api-key", false] as [Any] {
            XCTAssertThrowsError(try CodexAccountPolicy.validate(config: ["forced_login_method": method], workspaceID: "workspace-a")) {
                XCTAssertEqual($0 as? AccountSwitchError, .managedAccount)
            }
        }
        for forced in ["workspace-a", ["workspace-a", "workspace-b"]] as [Any] {
            let config: [String: Any] = ["forced_chatgpt_workspace_id": forced]
            XCTAssertNoThrow(try CodexAccountPolicy.validate(config: config, workspaceID: nil))
            XCTAssertNoThrow(try CodexAccountPolicy.validate(config: config, workspaceID: "workspace-a"))
            XCTAssertThrowsError(try CodexAccountPolicy.validate(config: config, workspaceID: "workspace-other")) {
                XCTAssertEqual($0 as? AccountSwitchError, .managedAccount)
            }
        }
        for forced in [false, 42, ["workspace-a", 42], [String]()] as [Any] {
            XCTAssertThrowsError(try CodexAccountPolicy.validate(config: ["forced_chatgpt_workspace_id": forced], workspaceID: "workspace-a")) {
                XCTAssertEqual($0 as? AccountSwitchError, .managedAccount)
            }
        }
    }

    func testProcessIdentityRecognizesCodexAndArchitectureSpecificExecutables() {
        for path in ["/Applications/Codex.app/Contents/Resources/codex",
                     "/synthetic/vendor/codex-aarch64-apple-darwin",
                     "/synthetic/vendor/codex-x86_64-apple-darwin"] {
            XCTAssertTrue(CodexProcessIdentity.isCodexExecutable(path: path))
        }
        for path in ["/usr/bin/true", "/synthetic/coderim", "/synthetic/codex-helper",
                     "/synthetic/codex-aarch64-unknown-linux-musl", "/synthetic/codex-aarch64-apple-darwin.backup"] {
            XCTAssertFalse(CodexProcessIdentity.isCodexExecutable(path: path))
        }
    }

    func testProjectedHomeRespectsArgumentCountPaddingAndProjectsOnlyHome() throws {
        let bytes = processArguments(
            padding: 8,
            arguments: ["codex", "", "app-server", "HOME=/ignored-argument", "CODEX_HOME=/ignored-argument"],
            environment: ["HOME=/Users/synthetic", "SYNTHETIC_ACCESS_TOKEN=not-a-real-token",
                          "CODEX_HOME=/Users/synthetic/custom-codex", "SYNTHETIC_REFRESH_TOKEN=also-not-real"],
            trailingPadding: 16
        )

        XCTAssertEqual(try CodexDesktopSession.projectedHome(from: bytes), "/Users/synthetic/custom-codex")
    }

    func testProjectedHomeDefaultsMissingAndEmptyCodexHomeToUserHome() throws {
        for environment in [["HOME=/Users/synthetic"], ["HOME=/Users/synthetic", "CODEX_HOME="]] {
            let bytes = processArguments(environment: environment)
            XCTAssertEqual(try CodexDesktopSession.projectedHome(from: bytes), "/Users/synthetic/.codex")
        }
    }

    func testProjectedHomeRejectsMissingEmptyOrRelativeUserHomeWithoutOverride() throws {
        for environment in [[String](), ["SYNTHETIC_TOKEN=not-a-real-token"], ["HOME="],
                            ["HOME=relative-home"], ["HOME=relative-home", "CODEX_HOME="]] {
            XCTAssertThrowsError(try CodexDesktopSession.projectedHome(from: processArguments(environment: environment))) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsupportedStorage)
            }
        }
    }

    func testProjectedHomeRejectsDuplicateHomeKeysIncludingEmptyValues() throws {
        for environment in [["HOME=/Users/first", "HOME=/Users/second"],
                            ["HOME=", "HOME=/Users/synthetic"],
                            ["CODEX_HOME=/first", "CODEX_HOME=/second"],
                            ["CODEX_HOME=", "CODEX_HOME=/second", "HOME=/Users/synthetic"]] {
            XCTAssertThrowsError(try CodexDesktopSession.projectedHome(from: processArguments(environment: environment))) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsupportedStorage)
            }
        }
    }

    func testProjectedHomeEnforcesArgumentCountBounds() throws {
        for count in [Int32.min, -1, 0, 16_385, Int32.max] {
            let bytes = processArguments(argumentCount: count, environment: ["HOME=/Users/synthetic"])
            XCTAssertThrowsError(try CodexDesktopSession.projectedHome(from: bytes)) {
                XCTAssertEqual($0 as? AccountSwitchError, .openCodexFirst)
            }
        }
        let maximum = processArguments(arguments: Array(repeating: "synthetic-argument", count: 16_384),
                                       environment: ["HOME=/Users/synthetic"])
        XCTAssertEqual(try CodexDesktopSession.projectedHome(from: maximum), "/Users/synthetic/.codex")
    }

    func testProjectedHomeRejectsTruncatedHeaderPathArgumentsAndEnvironment() throws {
        var missingPathTerminator = processArguments(argumentCount: 1, arguments: [], environment: [])
        missingPathTerminator = Data(missingPathTerminator.prefix(MemoryLayout<Int32>.size))
        missingPathTerminator.append(Data("/synthetic/no-terminator".utf8))
        let missingArgument = processArguments(argumentCount: 2, arguments: ["codex"], environment: [])
        let truncatedArgument = Data(processArguments(environment: []).dropLast())
        let truncatedEnvironment = Data(processArguments(environment: ["HOME=/Users/synthetic"]).dropLast())

        for bytes in [Data(), Data([1, 0, 0]), missingPathTerminator, missingArgument, truncatedArgument, truncatedEnvironment] {
            XCTAssertThrowsError(try CodexDesktopSession.projectedHome(from: bytes)) {
                XCTAssertEqual($0 as? AccountSwitchError, .openCodexFirst)
            }
        }
    }

    func testProjectedHomeBoundsDecodedValuesAndRejectsInvalidUTF8() throws {
        for key in ["HOME=", "CODEX_HOME="] {
            let oversized = key + "/" + String(repeating: "a", count: 4_096)
            XCTAssertThrowsError(try CodexDesktopSession.projectedHome(from: processArguments(environment: [oversized]))) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsupportedStorage)
            }
            var malformed = processArguments(environment: [])
            malformed.append(Data(key.utf8))
            malformed.append(contentsOf: [0xFF, 0x00])
            XCTAssertThrowsError(try CodexDesktopSession.projectedHome(from: malformed)) {
                XCTAssertEqual($0 as? AccountSwitchError, .unsupportedStorage)
            }
        }
        let boundaryPath = "/" + String(repeating: "a", count: 4_096 - "CODEX_HOME=".utf8.count - 1)
        XCTAssertEqual(try CodexDesktopSession.projectedHome(from: processArguments(environment: ["CODEX_HOME=" + boundaryPath])),
                       boundaryPath)
    }

    func testProjectedHomeIgnoresOtherTokenLikeEnvironmentEntries() throws {
        var bytes = processArguments(environment: ["HOME=/Users/synthetic", "OPENAI_API_KEY=synthetic-not-a-key",
                                                   "ACCESS_TOKEN=synthetic-not-a-token", "ACCESS_TOKEN=second-synthetic-value"])
        bytes.append(contentsOf: [0xFF, 0xFE, 0x00])

        XCTAssertEqual(try CodexDesktopSession.projectedHome(from: bytes), "/Users/synthetic/.codex")
    }

    func testReadOnlyInstalledDesktopRuntimeIntegration() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["CODERIM_ACCOUNT_RUNTIME_INTEGRATION"] == "1",
                          "Set CODERIM_ACCOUNT_RUNTIME_INTEGRATION=1 for the read-only installed-desktop policy probe.")
        do {
            let session = try await CodexDesktopSession.running()
            XCTAssertTrue(["/Applications/Codex.app", "/Applications/ChatGPT.app"].contains(session.bundle.path),
                          "The detected desktop must be an allowed application bundle.")
            XCTAssertTrue(session.executable == session.bundle.appendingPathComponent("Contents/Resources/codex"),
                          "The detected executable must be the pinned desktop app-server.")
            try await LocalCodexAccountRuntime().checkPolicy(for: nil)
            let inspector = SystemCodexProcessInspector()
            let identified = try CodexProcessGate.runningCodexPIDs(using: inspector)
            XCTAssertFalse(identified.isEmpty, "The running desktop's Codex process must be identified.")
            for pid in identified {
                if let path = inspector.executablePath(for: pid) {
                    XCTAssertTrue(CodexProcessIdentity.isCodexExecutable(path: path))
                } else if let info = inspector.metadata(for: pid) {
                    XCTAssertTrue(info.command == "codex" || info.command.hasPrefix("codex-"))
                }
            }
            // Identification alone no longer blocks: only a process holding the
            // login file open does. The read-only probe records which applies.
            var loginStat = stat()
            let loginPath = CodexLoginFile.defaultDirectory.appendingPathComponent("auth.json").path
            let holdsLogin: Bool
            if stat(loginPath, &loginStat) == 0 {
                let identity = CodexFileIdentity(device: UInt64(UInt32(bitPattern: loginStat.st_dev)),
                                                 inode: loginStat.st_ino)
                holdsLogin = identified.contains { inspector.holdsFile($0, identity: identity) == true }
            } else {
                holdsLogin = false
            }
            print("Read-only process verification: \(identified.count) Codex processes identified; "
                  + "login-file holders: \(holdsLogin ? "present" : "none"); no application or login changed.")
        } catch let error as AccountSwitchError {
            XCTFail("Read-only desktop policy probe failed: \(error.errorDescription ?? "Unavailable")")
        } catch {
            XCTFail("Read-only desktop policy probe failed without changing credentials.")
        }
    }

    func testIsolatedKeychainRoundTripIntegration() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["CODERIM_ACCOUNT_KEYCHAIN_INTEGRATION"] == "1",
                          "Opt-in only: exercise a unique test Keychain item containing synthetic credentials.")
        let vault = KeychainAccountVault(service: "dev.coderim.synthetic-test.\(UUID().uuidString)")
        XCTAssertTrue(try vault.load().isEmpty)
        let first = try account(email: "fixture@example.test")
        try vault.save([first])
        // Deletes only this newly created, unique synthetic test item.
        defer { try? vault.save([]) }
        XCTAssertEqual(try vault.load(), [first])
        let updated = try account(email: "updated-fixture@example.test")
        try vault.save([updated])
        XCTAssertEqual(try vault.load(), [updated])
        try vault.save([])
        XCTAssertTrue(try vault.load().isEmpty)
    }

    func testSuccessfulSwitchOrdersPolicyQuitPreservationReplacementAndReopen() async throws {
        let harness = try makeHarness()
        let generation = AccountSwitchActivity.generation

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.trace.events, [
            "will-change", "vault.load", "policy:workspace-b", "login.read", "quit", "login.read",
            "vault.load", "vault.save", "stopped", "login.replace", "open", "verify-cli", "login.read", "did-finish"
        ])
        XCTAssertEqual(harness.login.data, harness.target.loginData)
        XCTAssertEqual(harness.login.expectedOriginal, harness.departing.loginData)
        XCTAssertEqual(harness.vault.accounts.first(where: { $0.id == harness.target.id }), harness.target)
        XCTAssertEqual(harness.store.currentID, harness.target.id)
        XCTAssertFalse(harness.store.isBusy)
        XCTAssertFalse(harness.store.isError)
        XCTAssertFalse(AccountSwitchActivity.isSwitching)
        XCTAssertEqual(AccountSwitchActivity.generation, generation &+ 2)
    }

    func testCLIVerificationFailureKeepsCommittedLoginAndReopenedDesktop() async throws {
        let harness = try makeHarness()
        harness.runtime.verificationError = .unavailable
        await harness.store.switchAccount(to: harness.target.id)
        XCTAssertEqual(harness.login.data, harness.target.loginData)
        XCTAssertEqual(harness.login.replaceCount, 1)
        XCTAssertEqual(harness.runtime.openCount, 1)
        XCTAssertEqual(harness.store.currentID, harness.target.id)
        XCTAssertEqual(harness.store.message, AccountSwitchError.cliVerificationFailed.errorDescription)
        XCTAssertTrue(harness.store.isError)
        XCTAssertEqual(harness.trace.events.last, "did-finish")
    }

    func testCLIProbeCannotHideAConcurrentWorkspaceChangeWithTheSameEmail() async throws {
        let harness = try makeHarness()
        let concurrent = try account(workspace: "different-workspace", email: harness.target.email)
        harness.runtime.onVerify = { harness.login.data = concurrent.loginData }
        await harness.store.switchAccount(to: harness.target.id)
        XCTAssertEqual(harness.login.data, concurrent.loginData)
        XCTAssertEqual(harness.store.currentID, concurrent.id)
        XCTAssertEqual(harness.store.message, AccountSwitchError.cliVerificationFailed.errorDescription)
        XCTAssertTrue(harness.store.isError)
    }

    func testCLIProbeAllowsSameAccountTokenRotationWithoutOverwritingIt() async throws {
        let harness = try makeHarness()
        let rotated = try account(workspace: "workspace-b", subject: "subject-b", revision: "rotated")
        harness.runtime.onVerify = { harness.login.data = rotated.loginData }
        await harness.store.switchAccount(to: harness.target.id)
        XCTAssertEqual(harness.login.data, rotated.loginData)
        XCTAssertFalse(harness.store.isError)
    }

    func testFailedAlreadyActiveProbeRefreshesBadgeAfterExternalSignIn() async throws {
        let harness = try makeHarness()
        harness.runtime.onVerify = { harness.login.data = harness.target.loginData }
        harness.runtime.verificationError = .unavailable
        await harness.store.switchAccount(to: harness.departing.id)
        XCTAssertEqual(harness.store.currentID, harness.target.id)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 0)
        XCTAssertTrue(harness.store.isError)
    }

    func testAlreadySelectedAccountStillChecksCLIWithoutRestartOrWrite() async throws {
        let harness = try makeHarness()
        await harness.store.switchAccount(to: harness.departing.id)
        XCTAssertTrue(harness.trace.events.contains("verify-cli"))
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 0)
        XCTAssertFalse(harness.store.isError)
    }

    func testSwitchPreservesDepartingCredentialRefreshedDuringGracefulQuit() async throws {
        let harness = try makeHarness()
        let latest = try account(revision: "latest-after-quit")
        harness.runtime.onQuit = { [login = harness.login] in login.data = latest.loginData }

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.vault.accounts.first(where: { $0.id == latest.id }), latest)
        XCTAssertEqual(harness.login.expectedOriginal, latest.loginData)
        XCTAssertEqual(harness.login.data, harness.target.loginData)
        XCTAssertFalse(harness.store.isError)
    }

    func testSwitchReloadsLatestSavedTargetWithoutRenewingOrChangingIt() async throws {
        let harness = try makeHarness()
        let latestTarget = try account(workspace: "workspace-b", subject: "subject-b", revision: "latest-saved")
        let index = try XCTUnwrap(harness.vault.accounts.firstIndex(where: { $0.id == harness.target.id }))
        harness.vault.accounts[index] = latestTarget

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.login.data, latestTarget.loginData)
        XCTAssertEqual(harness.vault.accounts.first(where: { $0.id == latestTarget.id }), latestTarget)
        XCTAssertEqual(harness.runtime.signInCount, 0)
        XCTAssertFalse(harness.store.isError)
    }

    func testSwitchRestoresSavedAccountWhenCurrentlyLoggedOut() async throws {
        let harness = try makeHarness()
        harness.login.data = nil
        harness.store.load()
        harness.trace.events.removeAll()
        let saved = harness.vault.accounts

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.login.data, harness.target.loginData)
        XCTAssertNil(harness.login.expectedOriginal)
        XCTAssertEqual(harness.login.replaceCount, 1)
        XCTAssertEqual(harness.store.currentID, harness.target.id)
        XCTAssertEqual(harness.vault.accounts, saved)
        XCTAssertEqual(harness.vault.saveCount, 0)
        XCTAssertEqual(harness.trace.events, [
            "will-change", "vault.load", "policy:workspace-b", "login.read", "quit", "login.read",
            "stopped", "login.replace", "open", "verify-cli", "login.read", "did-finish"
        ])
        XCTAssertFalse(harness.store.isError)
    }

    func testLoginCreatedWhileQuittingFromLoggedOutStateIsPreserved() async throws {
        let harness = try makeHarness()
        harness.login.data = nil
        let concurrent = try account(workspace: "workspace-c", subject: "subject-c")
        harness.runtime.onQuit = { [login = harness.login] in login.data = concurrent.loginData }

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.changedLogin.errorDescription)
        XCTAssertEqual(harness.login.data, concurrent.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.vault.saveCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 1)
    }

    func testLoginCreatedImmediatelyBeforeLoggedOutReplacementWins() async throws {
        let harness = try makeHarness()
        harness.login.data = nil
        let concurrent = try account(workspace: "workspace-c", subject: "subject-c")
        harness.runtime.onRequireStopped = { [login = harness.login] in login.data = concurrent.loginData }

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.changedLogin.errorDescription)
        XCTAssertEqual(harness.login.data, concurrent.loginData)
        XCTAssertNil(harness.login.expectedOriginal)
        XCTAssertEqual(harness.login.replaceCount, 1)
        XCTAssertEqual(harness.runtime.openCount, 1)
    }

    func testInvalidSavedLoginFailsBeforeQuittingOrReplacingLogin() async throws {
        for invalidData in [Data("{}".utf8), try account(workspace: "workspace-unrelated").loginData] {
            let harness = try makeHarness()
            let encoded = try changing(JSONEncoder().encode(harness.target)) { $0["loginData"] = invalidData.base64EncodedString() }
            let corrupt = try JSONDecoder().decode(SavedCodexAccount.self, from: encoded)
            harness.vault.accounts = [harness.departing, corrupt]

            await harness.store.switchAccount(to: harness.target.id)

            XCTAssertEqual(harness.store.message, AccountSwitchError.invalidLogin.errorDescription)
            XCTAssertEqual(harness.login.data, harness.departing.loginData)
            XCTAssertEqual(harness.login.replaceCount, 0)
            XCTAssertEqual(harness.runtime.quitCount, 0)
            XCTAssertEqual(harness.runtime.openCount, 0)
            XCTAssertEqual(harness.vault.saveCount, 0)
        }
    }

    func testPolicyFailureDoesNotQuitOrReplaceCurrentLogin() async throws {
        let harness = try makeHarness()
        harness.runtime.policyConfig = ["cli_auth_credentials_store": "keyring"]

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.unsupportedStorage.errorDescription)
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 0)
        XCTAssertEqual(harness.vault.saveCount, 0)
        XCTAssertFalse(harness.store.isBusy)
        XCTAssertFalse(AccountSwitchActivity.isSwitching)
    }

    func testQuitCancellationNeverReplacesLogin() async throws {
        let harness = try makeHarness()
        harness.runtime.quitError = .quitCancelled

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.quitCancelled.errorDescription)
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 0)
        XCTAssertEqual(harness.vault.saveCount, 0)
    }

    func testRemainingCodexProcessPreventsReplacementAndReopensOriginal() async throws {
        let harness = try makeHarness()
        harness.runtime.stoppedError = .codexRunning

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.codexRunning.errorDescription)
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 1)
        XCTAssertEqual(harness.store.currentID, harness.departing.id)
    }

    func testAsynchronousStoppedGateFailureReopensUnchangedDepartingLogin() async throws {
        let harness = try makeHarness()
        harness.runtime.onWaitForStopped = { [trace = harness.trace] in
            trace.record("wait-stopped")
            await Task.yield()
            trace.record("wait-stopped-failed")
            throw AccountSwitchError.codexRunning
        }

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.trace.events, [
            "will-change", "vault.load", "policy:workspace-b", "login.read", "quit", "login.read",
            "vault.load", "vault.save", "wait-stopped", "wait-stopped-failed", "open", "did-finish"
        ])
        XCTAssertEqual(harness.store.message, AccountSwitchError.codexRunning.errorDescription)
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 1)
        XCTAssertEqual(harness.runtime.openCount, 1)
        XCTAssertEqual(harness.store.currentID, harness.departing.id)
        XCTAssertFalse(harness.store.isBusy)
        XCTAssertFalse(AccountSwitchActivity.isSwitching)
    }

    func testUnidentifiedProcessReopensOriginalLoginWithoutClaimingCodexIsRunning() async throws {
        let harness = try makeHarness()
        harness.runtime.stoppedError = .processInspectionFailed

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.processInspectionFailed.errorDescription)
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 1)
        XCTAssertEqual(harness.store.currentID, harness.departing.id)
    }

    func testKeychainReadFailureDoesNotMutateAnything() async throws {
        let harness = try makeHarness()
        harness.vault.loadError = .keychain
        let saved = harness.vault.accounts

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.keychain.errorDescription)
        XCTAssertEqual(harness.vault.accounts, saved)
        XCTAssertEqual(harness.vault.saveCount, 0)
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 0)
    }

    func testKeychainWriteFailurePreservesLoginAndSavedAccounts() async throws {
        let harness = try makeHarness()
        harness.vault.saveError = .keychain
        let saved = harness.vault.accounts
        let latest = try account(revision: "fresh-departing")
        harness.runtime.onQuit = { [login = harness.login] in login.data = latest.loginData }

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.keychain.errorDescription)
        XCTAssertEqual(harness.vault.accounts, saved)
        XCTAssertEqual(harness.login.data, latest.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 1)
    }

    func testConcurrentIdentityChangeDuringQuitIsNeverOverwritten() async throws {
        let harness = try makeHarness()
        let concurrent = try account(workspace: "workspace-c", subject: "subject-c")
        harness.runtime.onQuit = { [login = harness.login] in login.data = concurrent.loginData }

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.changedLogin.errorDescription)
        XCTAssertEqual(harness.login.data, concurrent.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.vault.saveCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 1)
    }

    func testConcurrentCredentialChangeAtReplacementIsGuardedByCompareAndSwap() async throws {
        let harness = try makeHarness()
        let concurrent = try account(revision: "concurrent-rotation")
        harness.runtime.onRequireStopped = { [login = harness.login] in login.data = concurrent.loginData }

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.store.message, AccountSwitchError.changedLogin.errorDescription)
        XCTAssertEqual(harness.login.expectedOriginal, harness.departing.loginData)
        XCTAssertEqual(harness.login.data, concurrent.loginData)
        XCTAssertEqual(harness.login.replaceCount, 1)
        XCTAssertEqual(harness.runtime.openCount, 1)
    }

    func testAlreadyActiveAccountDoesNotWriteOrRestartCodex() async throws {
        let harness = try makeHarness()

        await harness.store.switchAccount(to: harness.departing.id)

        XCTAssertEqual(harness.store.currentID, harness.departing.id)
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 0)
        XCTAssertEqual(harness.vault.saveCount, 0)
        XCTAssertFalse(harness.store.isError)
    }

    func testRemovingActiveSavedAccountDoesNotSignOutOrRestartCodex() throws {
        let harness = try makeHarness()

        harness.store.remove(harness.departing.id)

        XCTAssertEqual(harness.vault.accounts, [harness.target])
        XCTAssertEqual(harness.store.accounts, [harness.target])
        XCTAssertEqual(harness.store.currentID, harness.departing.id)
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 0)
        XCTAssertEqual(harness.trace.events, ["vault.load", "vault.save"])
    }

    func testLaunchFailureKeepsCommittedTargetWithoutRollingBackLogin() async throws {
        let harness = try makeHarness()
        harness.runtime.openError = .unavailable

        await harness.store.switchAccount(to: harness.target.id)

        XCTAssertEqual(harness.login.data, harness.target.loginData)
        XCTAssertEqual(harness.store.currentID, harness.target.id)
        XCTAssertEqual(harness.login.replaceCount, 1)
        XCTAssertEqual(harness.runtime.openCount, 1)
        XCTAssertTrue(harness.store.isError)
        XCTAssertTrue(harness.store.message?.contains("login was changed") == true)
        XCTAssertFalse(harness.store.isBusy)
        XCTAssertFalse(AccountSwitchActivity.isSwitching)
    }

    func testSaveCurrentUpdatesSameIdentityWithoutDuplicatingOrRestarting() async throws {
        let harness = try makeHarness()
        let latest = try account(email: "renamed@example.test", revision: "fresh-current")
        harness.login.data = latest.loginData

        await harness.store.saveCurrent()

        XCTAssertEqual(harness.vault.accounts.count, 2)
        XCTAssertEqual(harness.vault.accounts.first(where: { $0.id == latest.id }), latest)
        XCTAssertEqual(harness.login.data, latest.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 0)
        XCTAssertFalse(harness.store.isError)
    }

    func testAddedAccountIsSavedWithoutChangingActiveLogin() async throws {
        let harness = try makeHarness()
        let added = try account(workspace: "workspace-c", subject: "subject-c")
        harness.runtime.signedInAccount = added

        harness.store.addAccount()
        try await waitForSignIn(harness.store)

        XCTAssertTrue(harness.vault.accounts.contains(added))
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.store.currentID, harness.departing.id)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 0)
        XCTAssertEqual(harness.trace.events, ["policy:nil", "sign-in", "policy:workspace-c", "vault.load", "vault.save"])
        XCTAssertFalse(harness.store.isError)
    }

    func testAddedAccountFromDisallowedWorkspaceIsNotSavedOrActivated() async throws {
        let harness = try makeHarness()
        let disallowed = try account(workspace: "workspace-c", subject: "subject-c")
        harness.runtime.policyConfig = ["forced_chatgpt_workspace_id": "workspace-a"]
        harness.runtime.signedInAccount = disallowed
        let saved = harness.vault.accounts

        harness.store.addAccount()
        try await waitForSignIn(harness.store)

        XCTAssertEqual(harness.store.message, AccountSwitchError.managedAccount.errorDescription)
        XCTAssertEqual(harness.runtime.signInCount, 1)
        XCTAssertEqual(harness.vault.accounts, saved)
        XCTAssertFalse(harness.vault.accounts.contains(disallowed))
        XCTAssertEqual(harness.vault.saveCount, 0)
        XCTAssertEqual(harness.login.data, harness.departing.loginData)
        XCTAssertEqual(harness.login.replaceCount, 0)
        XCTAssertEqual(harness.runtime.quitCount, 0)
        XCTAssertEqual(harness.runtime.openCount, 0)
        XCTAssertFalse(harness.store.isSigningIn)
    }

    func testAccountLimitResultStartedBeforeSwitchIsDiscarded() async throws {
        let snapshot = AccountLimitsSnapshot(windows: [], resetCredits: nil, fetchedAt: Date(timeIntervalSince1970: 1))
        try await assertLimitResultDiscarded(.success(snapshot))
    }

    func testAccountLimitFailureStartedBeforeSwitchIsDiscarded() async throws {
        try await assertLimitResultDiscarded(.failure(AccountLimitError.timedOut))
    }

    func testAccountSwitchCancellationReachesInFlightLimitProvider() async throws {
        let harness = try makeHarness()
        let provider = AccountSwitchPausedLimitProvider(finishesOnCancellation: true)
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "CodexAccountSwitchingTests.\(UUID().uuidString)"))
        let limits = AccountLimitStore(provider: provider, defaults: defaults, pollingInterval: nil)
        harness.store.onAccountWillChange = { limits.clearForAccountSwitch() }
        let refresh = Task { await limits.refresh() }
        let requested = await provider.waitUntilRequested()

        if requested { await harness.store.switchAccount(to: harness.target.id) }
        let cancelled = requested ? await provider.waitUntilCancelled() : false
        // Release the synthetic request even when a regression prevents cancellation.
        if !cancelled { await provider.finish(.failure(CancellationError())) }
        await refresh.value
        let cancellationCount = await provider.cancellationCount

        XCTAssertTrue(requested, "The in-memory limit provider was not called before the deadline")
        XCTAssertTrue(cancelled, "Account switching must cancel the in-flight provider task")
        XCTAssertEqual(cancellationCount, 1)
        XCTAssertNil(limits.snapshot)
        XCTAssertEqual(limits.status, .loading)
        XCTAssertEqual(limits.statusMessage, "Switching account…")
        XCTAssertFalse(limits.isRefreshing)
        XCTAssertEqual(harness.login.data, harness.target.loginData)
        XCTAssertFalse(harness.store.isError)
    }

    private func assertLimitResultDiscarded(_ result: Result<AccountLimitsSnapshot, Error>) async throws {
        let harness = try makeHarness()
        let provider = AccountSwitchPausedLimitProvider()
        // No persistent preference is written, and no standard-user preference is read.
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "CodexAccountSwitchingTests.\(UUID().uuidString)"))
        let limits = AccountLimitStore(provider: provider, defaults: defaults, pollingInterval: nil)
        harness.store.onAccountWillChange = { limits.clearForAccountSwitch() }
        let refresh = Task { await limits.refresh() }
        let requested = await provider.waitUntilRequested()

        if requested { await harness.store.switchAccount(to: harness.target.id) }
        await provider.finish(result)
        await refresh.value
        XCTAssertTrue(requested, "The in-memory limit provider was not called before the deadline")
        guard requested else { return }

        XCTAssertNil(limits.snapshot)
        XCTAssertEqual(limits.status, .loading)
        XCTAssertEqual(limits.statusMessage, "Switching account…")
        XCTAssertFalse(limits.isRefreshing)
    }

    private func waitForSignIn(_ store: CodexAccountStore) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while store.isBusy, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(1))
        }
        XCTAssertFalse(store.isBusy, "Mock sign-in did not finish")
        XCTAssertFalse(store.isSigningIn)
    }

    private func makeHarness() throws -> AccountSwitchHarness {
        AccountSwitchHarness(departing: try account(),
                             target: try account(workspace: "workspace-b", subject: "subject-b"))
    }

    private func processArguments(
        argumentCount: Int32? = nil,
        padding: Int = 3,
        arguments: [String] = ["codex", "app-server"],
        environment: [String],
        trailingPadding: Int = 0
    ) -> Data {
        var count = argumentCount ?? Int32(arguments.count)
        var data = withUnsafeBytes(of: &count) { Data($0) }
        data.append(Data("/Applications/Codex.app/Contents/Resources/codex".utf8))
        data.append(0)
        data.append(Data(repeating: 0, count: padding))
        for value in arguments + environment {
            data.append(Data(value.utf8))
            data.append(0)
        }
        data.append(Data(repeating: 0, count: trailingPadding))
        return data
    }

    private func account(
        workspace: String = "workspace-a",
        subject: String = "subject-a",
        email: String = "account@example.test",
        revision: String = "initial"
    ) throws -> SavedCodexAccount {
        try SavedCodexAccount(loginData: loginData(workspace: workspace, revision: revision, claims: [
            "sub": subject,
            "email": email,
            "https://api.openai.com/auth": ["chatgpt_account_id": workspace]
        ]))
    }

    private func loginData(
        workspace: String = "workspace-a",
        revision: String = "initial",
        claims: [String: Any]
    ) throws -> Data {
        let payload = try JSONSerialization.data(withJSONObject: claims, options: [.sortedKeys])
            .base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        return try JSONSerialization.data(withJSONObject: [
            "auth_mode": "chatgpt",
            "OPENAI_API_KEY": NSNull(),
            "tokens": ["access_token": "synthetic-access-\(revision)",
                       "refresh_token": "synthetic-refresh-\(revision)",
                       "id_token": "e30.\(payload).synthetic-signature",
                       "account_id": workspace]
        ], options: [.sortedKeys])
    }

    private func changing(_ data: Data, _ update: (inout [String: Any]) -> Void) throws -> Data {
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        update(&object)
        return try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
    }

    private func changingToken(_ data: Data, key: String, value: Any) throws -> Data {
        try changing(data) { object in
            var tokens = object["tokens"] as? [String: Any] ?? [:]
            tokens[key] = value
            object["tokens"] = tokens
        }
    }
}

private struct AccountSwitchFileFixture {
    let directory: URL
    var auth: URL { directory.appendingPathComponent("auth.json") }
    var lock: URL { directory.appendingPathComponent(".codexmeter-accounts.lock") }
    var login: CodexLoginFile { CodexLoginFile(directory: directory) }

    init(data: Data? = nil) throws {
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodeRimAccountSwitchTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        if let data, !FileManager.default.createFile(atPath: auth.path, contents: data,
                                                      attributes: [.posixPermissions: 0o600]) {
            try? FileManager.default.removeItem(at: directory)
            throw CocoaError(.fileWriteNoPermission)
        }
    }

    func contents() throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted()
    }

    func remove() { try? FileManager.default.removeItem(at: directory) }
}

private final class AccountSwitchTrace {
    var events: [String] = []
    func record(_ event: String) { events.append(event) }
}

private final class AccountSwitchMemoryVault: AccountVault {
    var accounts: [SavedCodexAccount]
    var loadError: AccountSwitchError?
    var saveError: AccountSwitchError?
    private(set) var saveCount = 0
    private let trace: AccountSwitchTrace

    init(accounts: [SavedCodexAccount], trace: AccountSwitchTrace) {
        self.accounts = accounts
        self.trace = trace
    }

    func load() throws -> [SavedCodexAccount] {
        trace.record("vault.load")
        if let loadError { throw loadError }
        return accounts
    }

    func save(_ accounts: [SavedCodexAccount]) throws {
        trace.record("vault.save")
        saveCount += 1
        if let saveError { throw saveError }
        self.accounts = accounts
    }
}

private final class AccountSwitchMemoryLogin: CodexLoginStoring {
    var data: Data?
    private(set) var replaceCount = 0
    private(set) var expectedOriginal: Data?
    private let trace: AccountSwitchTrace

    init(data: Data, trace: AccountSwitchTrace) {
        self.data = data
        self.trace = trace
    }

    func read() throws -> Data? {
        trace.record("login.read")
        return data
    }

    func replace(with data: Data, expecting original: Data?) throws {
        trace.record("login.replace")
        replaceCount += 1
        expectedOriginal = original
        guard self.data == original else { throw AccountSwitchError.changedLogin }
        self.data = data
    }
}

@MainActor
private final class AccountSwitchMemoryRuntime: CodexAccountRuntime {
    var policyConfig: [String: Any] = [:]
    var signedInAccount: SavedCodexAccount?
    var quitError: AccountSwitchError?
    var stoppedError: AccountSwitchError?
    var openError: AccountSwitchError?
    var verificationError: AccountSwitchError?
    var onVerify: () -> Void = {}
    var onQuit: () -> Void = {}
    var onRequireStopped: () -> Void = {}
    var onWaitForStopped: (@MainActor () async throws -> Void)?
    private(set) var signInCount = 0
    private(set) var quitCount = 0
    private(set) var openCount = 0
    private let trace: AccountSwitchTrace

    init(trace: AccountSwitchTrace) { self.trace = trace }

    func checkPolicy(for workspaceID: String?) async throws {
        trace.record("policy:\(workspaceID ?? "nil")")
        try CodexAccountPolicy.validate(config: policyConfig, workspaceID: workspaceID)
    }

    func signIn() async throws -> SavedCodexAccount {
        trace.record("sign-in")
        signInCount += 1
        guard let signedInAccount else { throw AccountSwitchError.loginFailed }
        return signedInAccount
    }

    func quitCodex() async throws {
        trace.record("quit")
        quitCount += 1
        if let quitError { throw quitError }
        onQuit()
    }

    func requireStopped() throws {
        trace.record("stopped")
        if let stoppedError { throw stoppedError }
        onRequireStopped()
    }

    func waitForStopped() async throws {
        if let onWaitForStopped { try await onWaitForStopped() }
        else { try requireStopped() }
    }

    func verifyCLIAccount(_ account: SavedCodexAccount) async throws {
        trace.record("verify-cli")
        onVerify()
        if let verificationError { throw verificationError }
    }

    func openCodex() async throws {
        trace.record("open")
        openCount += 1
        if let openError { throw openError }
    }
}

@MainActor
private struct AccountSwitchHarness {
    let departing: SavedCodexAccount
    let target: SavedCodexAccount
    let trace: AccountSwitchTrace
    let vault: AccountSwitchMemoryVault
    let login: AccountSwitchMemoryLogin
    let runtime: AccountSwitchMemoryRuntime
    let store: CodexAccountStore

    init(departing: SavedCodexAccount, target: SavedCodexAccount) {
        self.departing = departing
        self.target = target
        let trace = AccountSwitchTrace()
        self.trace = trace
        vault = AccountSwitchMemoryVault(accounts: [departing, target], trace: trace)
        login = AccountSwitchMemoryLogin(data: departing.loginData, trace: trace)
        runtime = AccountSwitchMemoryRuntime(trace: trace)
        store = CodexAccountStore(vault: vault, login: login, runtime: runtime, acquireLock: { nil })
        store.load()
        store.onAccountWillChange = { trace.record("will-change") }
        store.onAccountOperationFinished = { trace.record("did-finish") }
        trace.events.removeAll()
    }
}

private actor AccountSwitchPausedLimitProvider: AccountLimitProviding {
    private var continuation: CheckedContinuation<AccountLimitsSnapshot, Error>?
    private var requested = false
    private var result: Result<AccountLimitsSnapshot, Error>?
    private let finishesOnCancellation: Bool
    private(set) var cancellationCount = 0

    init(finishesOnCancellation: Bool = false) {
        self.finishesOnCancellation = finishesOnCancellation
    }

    func readLimits() async throws -> AccountLimitsSnapshot {
        requested = true
        return try await withTaskCancellationHandler {
            if let result { return try result.get() }
            return try await withCheckedThrowingContinuation { continuation = $0 }
        } onCancel: {
            Task { await self.recordCancellation() }
        }
    }

    func waitUntilRequested() async -> Bool {
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while !requested, ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(1))
        }
        return requested
    }

    func finish(_ result: Result<AccountLimitsSnapshot, Error>) {
        self.result = result
        continuation?.resume(with: result)
        continuation = nil
    }

    func waitUntilCancelled() async -> Bool {
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while cancellationCount == 0, ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(1))
        }
        return cancellationCount > 0
    }

    private func recordCancellation() {
        cancellationCount += 1
        if finishesOnCancellation { finish(.failure(CancellationError())) }
    }
}
