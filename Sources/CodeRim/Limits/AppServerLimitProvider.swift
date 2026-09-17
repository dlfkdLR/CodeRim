import Foundation
import Security

struct AppServerLimitProvider: AccountLimitProviding {
    static let maximumResponseBytes = 2_097_152

    private let executableResolver: @Sendable () throws -> URL
    private let runner: AppServerProcessRunning
    private let parser = AccountLimitsResponseParser()

    init(
        executableResolver: @escaping @Sendable () throws -> URL = TrustedCodexExecutable.resolve,
        runner: AppServerProcessRunning = AppServerProcessRunner()
    ) {
        self.executableResolver = executableResolver
        self.runner = runner
    }

    func readLimits() async throws -> AccountLimitsSnapshot {
        let executable = try executableResolver()
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

enum TrustedCodexExecutable {
    static func resolve() throws -> URL {
        let candidates = [
            URL(fileURLWithPath: "/Applications/ChatGPT.app/Contents/Resources/codex"),
            URL(fileURLWithPath: "/Applications/Codex.app/Contents/Resources/codex")
        ]
        for candidate in candidates where FileManager.default.isExecutableFile(atPath: candidate.path) {
            if isTrusted(candidate) { return candidate }
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
