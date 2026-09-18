import AppKit
import Darwin
import Foundation

@MainActor
protocol ClaudeAccountRuntime {
    func checkPolicy() throws
    func requireStopped() throws
    func signIn() async throws -> SavedClaudeAccount
    func verifyCLIAccount(_ account: SavedClaudeAccount) async throws
}

@MainActor
struct LocalClaudeAccountRuntime: ClaudeAccountRuntime {
    func checkPolicy() throws {
        let environment = ProcessInfo.processInfo.environment
        let external = ["CLAUDE_CONFIG_DIR", "CLAUDE_SECURESTORAGE_CONFIG_DIR", "ANTHROPIC_API_KEY",
                        "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_BASE_URL",
                        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"]
        guard !external.contains(where: { !(environment[$0] ?? "").isEmpty }) else { throw ClaudeAccountError.policy }
        let home = FileManager.default.homeDirectoryForCurrentUser
        for path in ["/Library/Application Support/ClaudeCode/managed-settings.json",
                     "/Library/Managed Preferences/com.anthropic.claudecode.plist",
                     home.appendingPathComponent("Library/Managed Preferences/com.anthropic.claudecode.plist").path,
                     home.appendingPathComponent(".claude/.credentials.json").path] {
            if FileManager.default.fileExists(atPath: path) { throw ClaudeAccountError.policy }
        }
        for name in [".claude/settings.json", ".claude.json"] {
            let file = ClaudeProfileFile(url: home.appendingPathComponent(name))
            guard let data = try file.read() else { continue }
            guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw ClaudeAccountError.policy }
            try Self.validatePolicy(object)
        }
    }

    static func validatePolicy(_ object: [String: Any]) throws {
        guard object["forceLoginMethod"] == nil, object["forceLoginOrgUUID"] == nil,
              object["apiKeyHelper"] == nil else { throw ClaudeAccountError.policy }
        if let env = object["env"] as? [String: Any],
           env.keys.contains(where: { $0.hasPrefix("ANTHROPIC_") || $0.hasPrefix("CLAUDE_CODE_USE_") || $0 == "CLAUDE_CODE_OAUTH_TOKEN" }) {
            throw ClaudeAccountError.policy
        }
    }

    func requireStopped() throws {
        let inspector = SystemCodexProcessInspector()
        let executable = try ClaudeExecutable.resolve().resolvingSymlinksInPath().path
        for pid in try inspector.processIDs() where pid != getpid() {
            guard let info = inspector.metadata(for: pid) else {
                if inspector.isAlive(pid) { throw ClaudeAccountError.running }
                continue
            }
            guard info.userID == getuid(), info.status != UInt32(SZOMB) else { continue }
            let path = inspector.executablePath(for: pid) ?? ""
            if path == executable || info.command == "claude" || path.hasSuffix("/claude")
                || path.contains("/claude/versions/") {
                throw ClaudeAccountError.running
            }
        }
        // Older npm installs execute through node; the registry identifies
        // those clients without reading their arguments or environment.
        let directory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude/sessions")
        guard ClaudeSessionMonitor.read(directory: directory).isEmpty else { throw ClaudeAccountError.running }
    }

    func verifyCLIAccount(_ account: SavedClaudeAccount) async throws {
        let status = try await ClaudeCommandRunner.run(executable: ClaudeExecutable.resolve(),
            arguments: ["auth", "status"], timeout: .seconds(10), maximumOutputBytes: 131_072,
            acceptsNonzeroExit: true)
        try CLIAccountVerification.requireClaude(status, account: account)
    }

    nonisolated static func requireSignedOutProbe(_ data: Data) throws {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let loggedIn = object["loggedIn"] as? NSNumber,
              CFGetTypeID(loggedIn) == CFBooleanGetTypeID(), !loggedIn.boolValue else {
            throw ClaudeAccountError.policy
        }
    }

    func signIn() async throws -> SavedClaudeAccount {
        try checkPolicy()
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("coderim-claude-login-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false,
                                               attributes: [.posixPermissions: 0o700])
        let item = ClaudeLoginStore.keychain(configDirectory: directory)
        defer {
            // The official CLI owns sign-in. Remove only the dedicated temporary
            // item after that child has exited, never call auth logout/revoke.
            if let original = try? item.read() { try? item.replace(nil, expecting: original) }
            try? FileManager.default.removeItem(at: directory)
        }
        let executable = try ClaudeExecutable.resolve()
        var env = ClaudeProcessEnvironment.sanitized
        env["CLAUDE_CONFIG_DIR"] = directory.path
        env["CLAUDE_SECURESTORAGE_CONFIG_DIR"] = directory.path
        // Refuse sign-in if a CLI version unexpectedly resolves this disposable
        // config to a live login. No current credential is copied into it.
        let probe = try await ClaudeCommandRunner.run(executable: executable, arguments: ["auth", "status"],
            timeout: .seconds(10), maximumOutputBytes: 131_072, acceptsNonzeroExit: true, environment: env)
        try Self.requireSignedOutProbe(probe)
        do {
            _ = try await ClaudeCommandRunner.run(executable: executable,
                arguments: ["auth", "login", "--claudeai"], timeout: .seconds(240),
                maximumOutputBytes: 131_072, environment: env)
        } catch {
            throw Task.isCancelled ? ClaudeAccountError.cancelled : ClaudeAccountError.loginFailed
        }
        try Task.checkCancellation()
        guard let account = try ClaudeLoginStore.local(configDirectory: directory).read().account() else {
            throw ClaudeAccountError.loginFailed
        }
        let status = try await ClaudeCommandRunner.run(executable: executable, arguments: ["auth", "status"],
            timeout: .seconds(10), maximumOutputBytes: 131_072, environment: env)
        guard try ClaudeCLIService.account(from: status)?.linkIdentifier == account.integrationIdentity else { throw ClaudeAccountError.invalidLogin }
        guard let latest = try ClaudeLoginStore.local(configDirectory: directory).read().account(),
              latest.id == account.id else { throw ClaudeAccountError.changedLogin }
        return latest
    }
}
