import AppKit
import Darwin
import Foundation

@MainActor
protocol CodexAccountRuntime {
    func checkPolicy(for workspaceID: String?) async throws
    func signIn() async throws -> SavedCodexAccount
    func quitCodex() async throws
    func waitForStopped() async throws
    func requireStopped() throws
    func openCodex() async throws
}

extension CodexAccountRuntime {
    func waitForStopped() async throws { try requireStopped() }
}

struct CodexAccountPolicy {
    static func validate(config: [String: Any], workspaceID: String?) throws {
        if let store = config["cli_auth_credentials_store"], !(store is NSNull) {
            guard store as? String == "file" else { throw AccountSwitchError.unsupportedStorage }
        }
        if let method = config["forced_login_method"], !(method is NSNull) {
            guard method as? String == "chatgpt" else { throw AccountSwitchError.managedAccount }
        }
        if let forced = config["forced_chatgpt_workspace_id"], !(forced is NSNull) {
            let allowed: [String]
            if let value = forced as? String { allowed = [value] }
            else if let values = forced as? [String] { allowed = values }
            else { throw AccountSwitchError.managedAccount }
            // The first check gates the sign-in method. The returned account is
            // checked against this allowlist before it can be saved or activated.
            if let workspaceID, !allowed.contains(workspaceID) { throw AccountSwitchError.managedAccount }
        }
    }
}

@MainActor
final class LocalCodexAccountRuntime: CodexAccountRuntime {
    private var desktop: CodexDesktopSession?

    func checkPolicy(for workspaceID: String?) async throws {
        if let home = ProcessInfo.processInfo.environment["CODEX_HOME"], !home.isEmpty,
           URL(fileURLWithPath: home).standardizedFileURL != CodexLoginFile.defaultDirectory.standardizedFileURL {
            throw AccountSwitchError.unsupportedStorage
        }
        desktop = try await CodexDesktopSession.running()
        let response = try await request("config/read", params: ["includeLayers": false])
        guard let result = response["result"] as? [String: Any], let config = result["config"] as? [String: Any] else {
            throw AccountSwitchError.unavailable
        }
        try CodexAccountPolicy.validate(config: config, workspaceID: workspaceID)
    }

    func signIn() async throws -> SavedCodexAccount {
        let directory = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: directory) }
        let process = Process()
        process.executableURL = try executable()
        process.arguments = ["-c", "cli_auth_credentials_store=\"file\"", "login"]
        process.currentDirectoryURL = directory
        let inherited = ProcessInfo.processInfo.environment
        process.environment = ["HOME": FileManager.default.homeDirectoryForCurrentUser.path,
                               "CODEX_HOME": directory.path, "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
                               "TMPDIR": NSTemporaryDirectory(), "LANG": inherited["LANG"] ?? "en_US.UTF-8"]
        // Codex owns browser OAuth. Neither OAuth codes nor callback URLs enter logs/UI.
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { throw AccountSwitchError.loginFailed }
        do {
            let deadline = ContinuousClock.now.advanced(by: .seconds(180))
            while process.isRunning {
                try Task.checkCancellation()
                guard ContinuousClock.now < deadline else { throw AccountSwitchError.loginFailed }
                try await Task.sleep(for: .milliseconds(100))
            }
        } catch {
            // Only the isolated login child is stopped; never a user's Codex task.
            if process.isRunning { process.terminate() }
            for _ in 0..<20 where process.isRunning { try? await Task.sleep(for: .milliseconds(50)) }
            if process.isRunning { kill(process.processIdentifier, SIGKILL) }
            process.waitUntilExit()
            throw Task.isCancelled ? AccountSwitchError.loginCancelled : AccountSwitchError.loginFailed
        }
        guard process.terminationStatus == 0 else { throw AccountSwitchError.loginFailed }
        guard let data = try CodexLoginFile(directory: directory).read() else { throw AccountSwitchError.loginFailed }
        return try SavedCodexAccount(loginData: data)
    }

    func quitCodex() async throws {
        guard let desktop else { throw AccountSwitchError.openCodexFirst }
        let current = try await CodexDesktopSession.running()
        guard current.bundle == desktop.bundle else { throw AccountSwitchError.changedLogin }
        let bundle = try applicationURL()
        let apps = NSWorkspace.shared.runningApplications.filter { $0.bundleURL?.standardizedFileURL == bundle.standardizedFileURL }
        for app in apps {
            guard app.terminate() else { throw AccountSwitchError.quitCancelled }
        }
        let deadline = ContinuousClock.now.advanced(by: .seconds(15))
        while apps.contains(where: { !$0.isTerminated }) {
            guard ContinuousClock.now < deadline else { throw AccountSwitchError.quitCancelled }
            try await Task.sleep(for: .milliseconds(100))
        }
    }

    func waitForStopped() async throws {
        // Child app-servers may need a moment to finish normal shutdown.
        // Keep this separate from quit: if a remaining client blocks the switch,
        // the coordinator knows the desktop closed and can reopen its old login.
        for _ in 0..<30 {
            do { try requireStopped(); return }
            catch { try await Task.sleep(for: .milliseconds(100)) }
        }
        try requireStopped()
    }

    func requireStopped() throws {
        try CodexProcessGate.requireStopped()
    }

    func openCodex() async throws {
        let url = try applicationURL()
        let session = CodexDesktopSession(bundle: url, executable: url.appendingPathComponent("Contents/Resources/codex"))
        try await Task.detached { try session.verifySignature() }.value
        _ = try await NSWorkspace.shared.openApplication(at: url, configuration: .init())
    }

    private func applicationURL() throws -> URL {
        if let desktop { return desktop.bundle }
        return try executable().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    }

    private func executable() throws -> URL {
        if let desktop { return desktop.executable }
        do { return try TrustedCodexExecutable.resolve() }
        catch { throw AccountSwitchError.unavailable }
    }

    private func temporaryHome() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("coderim-account-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        return directory
    }

    private func request(_ method: String, params: [String: Any]) async throws -> [String: Any] {
        let initialize: [String: Any] = ["method": "initialize", "id": 1, "params": ["clientInfo": ["name": "coderim-accounts", "version": "1"]]]
        let messages: [[String: Any]] = [initialize, ["method": "initialized", "params": [:]], ["method": method, "id": 2, "params": params]]
        var data = Data()
        for message in messages { data.append(try JSONSerialization.data(withJSONObject: message)); data.append(0x0A) }
        let output: Data
        do {
            output = try await AppServerProcessRunner(workingDirectory: FileManager.default.homeDirectoryForCurrentUser).run(
                executable: executable(),
                arguments: ["app-server"],
                standardInput: data, timeout: .seconds(20), maximumOutputBytes: 1_048_576
            )
        } catch { throw AccountSwitchError.unavailable }
        for line in output.split(separator: 0x0A) {
            if let object = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any],
               object["id"] as? Int == 2 { return object }
        }
        throw AccountSwitchError.unavailable
    }
}
