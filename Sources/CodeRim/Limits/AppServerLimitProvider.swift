import Darwin
import Foundation
import Security

struct AppServerLimitProvider: AccountLimitProviding {
    static let maximumResponseBytes = 2_097_152

    private let executableResolver: @Sendable () throws -> URL
    private let runner: AppServerProcessRunning
    private let parser = AccountLimitsResponseParser()
    private static let executableQueue = DispatchQueue(label: "dev.coderim.codex-verification", qos: .utility)

    init(
        executableResolver: @escaping @Sendable () throws -> URL = TrustedCodexExecutable.resolve,
        runner: AppServerProcessRunning = AppServerProcessRunner()
    ) {
        self.executableResolver = executableResolver
        self.runner = runner
    }

    func readLimits() async throws -> AccountLimitsSnapshot {
        try Task.checkCancellation()
        let resolver = executableResolver
        let executable = try await withCheckedThrowingContinuation { continuation in
            Self.executableQueue.async {
                continuation.resume(with: Result { try resolver() })
            }
        }
        try Task.checkCancellation()
        let requests = [
            #"{"method":"initialize","id":1,"params":{"clientInfo":{"name":"coderim","title":"CodeRim","version":"2"},"capabilities":{"optOutNotificationMethods":["remoteControl/status/changed"]}}}"#,
            #"{"method":"initialized","params":{}}"#,
            #"{"method":"account/rateLimits/read","id":2}"#
        ].joined(separator: "\n") + "\n"
        let response = try await runner.run(
            executable: executable,
            arguments: ["app-server"],
            standardInput: Data(requests.utf8),
            timeout: .seconds(15),
            maximumOutputBytes: Self.maximumResponseBytes
        )
        return try parser.parse(response)
    }
}

protocol AppServerProcessRunning: Sendable {
    func run(
        executable: URL,
        arguments: [String],
        standardInput: Data,
        timeout: Duration,
        maximumOutputBytes: Int
    ) async throws -> Data
}

struct AppServerProcessRunner: AppServerProcessRunning {
    var workingDirectory: URL? = nil

    func run(
        executable: URL,
        arguments: [String],
        standardInput: Data,
        timeout: Duration,
        maximumOutputBytes: Int
    ) async throws -> Data {
        guard let firstNewline = standardInput.firstIndex(of: 0x0A) else {
            throw AccountLimitError.malformedResponse
        }
        let inherited = ProcessInfo.processInfo.environment
        let allowed = ["HOME", "TMPDIR", "USER", "LOGNAME", "LANG", "LC_ALL", "LC_CTYPE",
                       "__CF_USER_TEXT_ENCODING", "CODEX_HOME", "XDG_CONFIG_HOME",
                       "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy"]
        let environment = allowed.reduce(into: [String: String]()) { $0[$1] = inherited[$1] }
        do {
            let result = try await BoundedProcess.run(
                executable: executable, arguments: arguments, environment: environment,
                workingDirectory: workingDirectory,
                initialInput: Data(standardInput[...firstNewline]),
                followingInput: Data(standardInput[standardInput.index(after: firstNewline)...]),
                closeInputWhen: { containsResponseID2($0) },
                timeout: timeout, maximumOutputBytes: maximumOutputBytes
            )
            guard result.status == 0 else {
                throw AccountLimitError.server("Codex app-server exited unexpectedly.")
            }
            guard containsResponseID2(result.output) else { throw AccountLimitError.malformedResponse }
            return result.output
        } catch BoundedProcessError.timedOut { throw AccountLimitError.timedOut }
        catch BoundedProcessError.outputTooLarge { throw AccountLimitError.responseTooLarge }
        catch is BoundedProcessError { throw AccountLimitError.processLaunchFailed }
    }

    private func containsResponseID2(_ data: Data) -> Bool {
        data.split(separator: 0x0A).contains { line in
            guard let object = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any],
                  let identifier = object["id"] as? NSNumber,
                  CFGetTypeID(identifier) != CFBooleanGetTypeID()
            else { return false }
            return identifier.intValue == 2
        }
    }
}

/// Only successful, unchanged executable validations are reused within this
/// process. No persisted record can bypass the vendor signature requirement.
final class TrustedExecutableValidationCache: @unchecked Sendable {
    private struct PathEntry: Equatable {
        let path: String
        let device: UInt64
        let inode: UInt64
        let size: Int64
        let mode: UInt16
        let owner: UInt32
        let group: UInt32
        let modifiedSeconds: Int64
        let modifiedNanoseconds: Int64
        let changedSeconds: Int64
        let changedNanoseconds: Int64

        init?(_ path: String) {
            var value = stat()
            guard Darwin.lstat(path, &value) == 0 else { return nil }
            self.path = path
            device = UInt64(value.st_dev)
            inode = UInt64(value.st_ino)
            size = Int64(value.st_size)
            mode = value.st_mode
            owner = value.st_uid
            group = value.st_gid
            modifiedSeconds = Int64(value.st_mtimespec.tv_sec)
            modifiedNanoseconds = Int64(value.st_mtimespec.tv_nsec)
            changedSeconds = Int64(value.st_ctimespec.tv_sec)
            changedNanoseconds = Int64(value.st_ctimespec.tv_nsec)
        }
    }

    private struct Identity: Equatable {
        let resolvedPath: String
        let entries: [PathEntry]

        init?(_ url: URL) {
            let resolved = url.resolvingSymlinksInPath().path
            var paths: [String] = []
            // Track both routes, including link entries and directory change
            // times, so swapping a path away and back cannot seed the cache.
            for route in [url.path, resolved] {
                guard route.hasPrefix("/") else { return nil }
                var current = ""
                for component in route.split(separator: "/") {
                    current += "/" + component
                    if !paths.contains(current) { paths.append(current) }
                }
            }
            var values: [PathEntry] = []
            for path in paths {
                guard let value = PathEntry(path) else { return nil }
                values.append(value)
            }
            guard let executable = values.first(where: { $0.path == resolved }),
                  executable.mode & S_IFMT == S_IFREG,
                  executable.mode & (S_IXUSR | S_IXGRP | S_IXOTH) != 0 else { return nil }
            resolvedPath = resolved
            entries = values
        }
    }

    private let lock = NSLock()
    private var identities: [String: Identity] = [:]

    func validate(_ executable: URL, using validator: (URL) -> Bool) -> Bool {
        lock.withLock {
            guard let identity = Identity(executable) else {
                identities.removeValue(forKey: executable.path)
                return false
            }
            if identities[executable.path] == identity { return true }
            identities.removeValue(forKey: executable.path)
            let resolved = URL(fileURLWithPath: identity.resolvedPath)
            guard validator(resolved), Identity(executable) == identity else { return false }
            identities[executable.path] = identity
            return true
        }
    }
}

enum TrustedCodexExecutable {
    private static let validationCache = TrustedExecutableValidationCache()
    static func resolve() throws -> URL {
        let candidates = [
            URL(fileURLWithPath: "/Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex"),
            URL(fileURLWithPath: "/Applications/ChatGPT.app/Contents/Resources/codex"),
            URL(fileURLWithPath: "/Applications/Codex.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex"),
            URL(fileURLWithPath: "/Applications/Codex.app/Contents/Resources/codex")
        ]
        for candidate in candidates where FileManager.default.isExecutableFile(atPath: candidate.path) {
            if validationCache.validate(candidate, using: isTrusted) { return candidate }
        }
        throw AccountLimitError.trustedAppServerNotFound
    }

    private static func isTrusted(_ executable: URL) -> Bool {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(executable as CFURL, [], &code) == errSecSuccess,
              let code
        else { return false }
        var requirement: SecRequirement?
        let requirementText = #"anchor apple generic and identifier "codex" and certificate leaf[subject.OU] = "2DC432GLL2""#
        guard SecRequirementCreateWithString(requirementText as CFString, [], &requirement) == errSecSuccess,
              let requirement
        else { return false }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures)
        return SecStaticCodeCheckValidityWithErrors(code, flags, requirement, nil) == errSecSuccess
    }
}
