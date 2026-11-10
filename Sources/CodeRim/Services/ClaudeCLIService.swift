import AppKit
import ClaudeBridgeCore
import CryptoKit
import Foundation

struct ClaudeAccount: Equatable, Sendable {
    let email: String?
    let subscriptionType: String?
    let authenticationMethod: String?
    let rateLimitTier: String?
    let linkIdentifier: String

    init(
        email: String?,
        subscriptionType: String?,
        authenticationMethod: String?,
        rateLimitTier: String? = nil,
        stableIdentity: String? = nil
    ) {
        self.email = email
        self.subscriptionType = subscriptionType
        self.authenticationMethod = authenticationMethod
        self.rateLimitTier = rateLimitTier
        let identity = stableIdentity ?? email ?? [authenticationMethod, subscriptionType]
            .compactMap { $0 }
            .joined(separator: ":")
        linkIdentifier = SHA256.hash(data: Data(identity.utf8))
            .map { String(format: "%02x", $0) }
            .joined()
    }

    var displayName: String { email ?? "Claude account" }
    var planName: String? {
        guard let subscriptionType else { return nil }
        if subscriptionType.lowercased() == "max" {
            switch rateLimitTier?.lowercased() {
            case "default_claude_max_5x": return "Max 5x"
            case "default_claude_max_20x": return "Max 20x"
            default: break
            }
        }
        return subscriptionType.replacingOccurrences(of: "_", with: " ").capitalized
    }
}

enum ClaudeIntegrationStatus: Equatable, Sendable {
    case disabled
    case checking
    case needsAccount
    case ready
    case stale
    case waitingForLimits
    case unavailable
}

enum ClaudeIntegrationError: Error, LocalizedError, Equatable {
    case cliNotFound
    case processFailed
    case timedOut
    case responseTooLarge
    case malformedResponse
    case noSignedInAccount
    case bridgeNotFound
    case invalidSettings
    case settingsChanged

    var errorDescription: String? {
        switch self {
        case .cliNotFound: "Claude Code was not found. Make sure the `claude` command runs in your terminal."
        case .processFailed: "Claude Code could not be opened."
        case .timedOut: "Checking the Claude account took too long."
        case .responseTooLarge, .malformedResponse: "Claude returned an unsupported account response."
        case .noSignedInAccount: "Sign in to Claude Code, then add the account again."
        case .bridgeNotFound: "The Claude limits helper is missing. Reinstall CodeRim."
        case .invalidSettings: "Claude settings could not be updated safely."
        case .settingsChanged: "Claude Code kept changing its settings file. Nothing was overwritten. Try again."
        }
    }
}

protocol ClaudeAuthenticating: Sendable {
    func accountStatus() async throws -> ClaudeAccount?
    /// Signs the Claude Code CLI itself in, through its own browser flow.
    func signIn() async throws
}

extension ClaudeAuthenticating {
    func signIn() async throws { throw ClaudeIntegrationError.malformedResponse }
}

protocol ClaudeStatusLineInstalling: Sendable {
    func install() throws
    func uninstall() throws
}

struct ClaudeCLIService: ClaudeAuthenticating {
    private static let maximumStatusBytes = 131_072
    private static let maximumConfigurationBytes = 8_388_608

    func accountStatus() async throws -> ClaudeAccount? {
        let data = try await ClaudeCommandRunner.run(
            executable: try ClaudeExecutable.resolve(),
            arguments: ["auth", "status"],
            timeout: .seconds(10),
            maximumOutputBytes: Self.maximumStatusBytes,
            acceptsNonzeroExit: true
        )
        let account = try Self.account(from: data)
        guard account?.subscriptionType?.lowercased() == "max",
              account?.rateLimitTier == nil else { return account }
        return try Self.account(from: data, configurationData: Self.readConfiguration())
    }

    /// `claude auth login` opens the browser and returns once the CLI holds
    /// the new login. It writes the CLI's own credentials; CodeRim reads them
    /// afterwards exactly as it would after a terminal sign-in.
    func signIn() async throws {
        _ = try await ClaudeCommandRunner.run(
            executable: try ClaudeExecutable.resolve(),
            arguments: ["auth", "login", "--claudeai"],
            timeout: .seconds(240),
            maximumOutputBytes: Self.maximumStatusBytes,
            environment: ClaudeProcessEnvironment.sanitized
        )
    }

    static func account(from data: Data, configurationData: Data? = nil) throws -> ClaudeAccount? {
        guard !data.isEmpty else { return nil }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let loggedIn = object["loggedIn"] as? Bool
        else { throw ClaudeIntegrationError.malformedResponse }
        guard loggedIn else { return nil }
        let email = Self.safeText(object["email"])
        let organizationID = Self.safeText(object["orgId"])
        let stableIdentity = [organizationID, email]
            .compactMap { $0 }
            .joined(separator: "\u{1F}")
        guard !stableIdentity.isEmpty else { throw ClaudeIntegrationError.malformedResponse }
        return ClaudeAccount(
            email: email,
            subscriptionType: Self.safeText(object["subscriptionType"]),
            authenticationMethod: Self.safeText(object["authMethod"]),
            rateLimitTier: Self.safeText(object["rateLimitTier"])
                ?? Self.cachedRateLimitTier(from: configurationData, email: email, organizationID: organizationID),
            stableIdentity: stableIdentity
        )
    }

    /// The CLI's local profile carries Max's tier even when auth status only
    /// says "max". It is display metadata, never a credential or account identity.
    /// Match both identity fields so a previous account/workspace cannot supply it.
    private static func cachedRateLimitTier(from data: Data?, email: String?, organizationID: String?) -> String? {
        guard let email, let organizationID, let data, data.count <= maximumConfigurationBytes,
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let account = root["oauthAccount"] as? [String: Any],
              safeText(account["emailAddress"]) == email,
              safeText(account["organizationUuid"]) == organizationID else { return nil }
        return safeText(account["userRateLimitTier"]) ?? safeText(account["organizationRateLimitTier"])
    }

    static func configurationURL(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> URL {
        if let configured = environment["CLAUDE_CONFIG_DIR"], configured.hasPrefix("/"),
           !configured.contains("\0") {
            return URL(fileURLWithPath: configured, isDirectory: true).appendingPathComponent(".claude.json")
        }
        return home.appendingPathComponent(".claude.json")
    }

    private static func readConfiguration() -> Data? {
        let url = configurationURL()
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              attributes[.type] as? FileAttributeType == .typeRegular,
              let size = attributes[.size] as? NSNumber, size.intValue <= maximumConfigurationBytes,
              let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let data = try? handle.read(upToCount: maximumConfigurationBytes + 1),
              data.count <= maximumConfigurationBytes else { return nil }
        return data
    }

    private static func safeText(_ value: Any?) -> String? {
        guard let text = value as? String else { return nil }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.utf8.count <= 320,
              !trimmed.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains)
        else { return nil }
        return trimmed
    }
}

enum ClaudeExecutable {
    static func resolve(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        fileManager: FileManager = .default,
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) throws -> URL {
        var candidates = [
            home.appendingPathComponent(".local/bin/claude"),
            home.appendingPathComponent(".claude/local/claude"),
            URL(fileURLWithPath: "/opt/homebrew/bin/claude"),
            URL(fileURLWithPath: "/usr/local/bin/claude")
        ]
        // A GUI launch usually inherits a minimal PATH, but honor a fuller one
        // (Node version managers, custom prefixes) when the launch environment has it.
        for directory in (environment["PATH"] ?? "").split(separator: ":") where directory.hasPrefix("/") {
            candidates.append(URL(fileURLWithPath: String(directory), isDirectory: true)
                .appendingPathComponent("claude"))
        }
        guard let executable = candidates.first(where: {
            fileManager.isExecutableFile(atPath: $0.path) && isPrivatelyOwned($0)
        }) else {
            throw ClaudeIntegrationError.cliNotFound
        }
        return executable.standardizedFileURL
    }

    /// Claude Code is an npm install with no code signature, so it cannot be
    /// pinned the way `TrustedCodexExecutable` pins Codex. What can still be
    /// established is that nobody *else* can rewrite it: CodeRim runs this
    /// binary with `auth login`, and a `claude` that any user on the machine
    /// could replace is a way to run their code under this account.
    ///
    /// So: neither the binary nor the directory holding it may be writable by
    /// group or other. That accepts every ordinary install — `~/.local/bin`,
    /// `~/.claude/local`, Homebrew, `/usr/local/bin`, a Node version manager's
    /// shim — including one another admin installed, whose write access is
    /// already equivalent to ours. What it rejects is a world-writable
    /// directory slipped onto `PATH`, which is the case that turns "run the
    /// user's CLI" into "run anyone's code as the user".
    ///
    /// Ownership is deliberately not part of the test: a shared Homebrew
    /// prefix belongs to whichever admin installed it, and refusing that would
    /// disable the integration for a setup that is not actually less safe.
    static func isPrivatelyOwned(_ url: URL, fileManager: FileManager = .default) -> Bool {
        var info = stat()
        for path in [url.path, url.deletingLastPathComponent().path] {
            guard stat(path, &info) == 0, info.st_mode & 0o022 == 0 else { return false }
        }
        return true
    }
}

struct ClaudeStatusLineInstaller: ClaudeStatusLineInstalling, @unchecked Sendable {
    private let settingsURL: URL
    private let managedDirectory: URL
    private let bridgeSource: @Sendable () throws -> URL
    private let fileManager: FileManager

    init(
        settingsURL: URL = ClaudeStatusLineInstaller.defaultSettingsURL(),
        managedDirectory: URL = AppPaths.applicationSupportDirectory
            .appendingPathComponent("Claude", isDirectory: true),
        fileManager: FileManager = .default,
        bridgeSource: @escaping @Sendable () throws -> URL = ClaudeStatusLineInstaller.resolveBundledBridge
    ) {
        self.settingsURL = settingsURL
        self.managedDirectory = managedDirectory
        self.fileManager = fileManager
        self.bridgeSource = bridgeSource
    }

    func install() throws {
        try AppPaths.prepareOwnerOnlyDirectory(at: managedDirectory, fileManager: fileManager)
        let installedBridge = managedDirectory.appendingPathComponent("CodexMeterClaudeBridge")
        try installBridge(from: bridgeSource(), to: installedBridge)
        let output = managedDirectory.appendingPathComponent("ClaudeLimits.json")
        let command = "\(shellQuote(installedBridge.path)) --output \(shellQuote(output.path))"

        let settings = try readJSONObject(at: settingsURL) ?? [:]
        let stateURL = managedDirectory.appendingPathComponent("StatusLineState.json")
        guard statusLineCommand(settings["statusLine"]) != command else { return }

        // Capture the user's real status line exactly once. On any later install
        // (an app move that changes the helper path, or a status line the user
        // edited while connected) keep the first-captured original so uninstall
        // still restores it instead of stranding it behind a CodeRim command.
        let existingState = try? readJSONObject(at: stateURL)
        let alreadyInstalled = (existingState?["installedCommand"] as? String).map { !$0.isEmpty } ?? false
        let currentIsManaged = statusLineCommand(settings["statusLine"])?
            .contains(installedBridge.path) ?? false
        let state: [String: Any]
        if alreadyInstalled, let existingState {
            state = [
                "version": 1,
                "installedCommand": command,
                "hadOriginal": existingState["hadOriginal"] as? Bool ?? false,
                "originalStatusLine": existingState["originalStatusLine"] ?? NSNull()
            ]
        } else {
            state = [
                "version": 1,
                "installedCommand": command,
                "hadOriginal": !currentIsManaged && settings["statusLine"] != nil,
                "originalStatusLine": currentIsManaged ? NSNull() : (settings["statusLine"] ?? NSNull())
            ]
        }
        try writeJSONObject(state, to: stateURL, permissions: 0o600)
        try updateSettings { settings in
            guard statusLineCommand(settings["statusLine"]) != command else { return false }
            settings["statusLine"] = ["type": "command", "command": command]
            return true
        }
    }

    func uninstall() throws {
        let stateURL = managedDirectory.appendingPathComponent("StatusLineState.json")
        guard let state = try readJSONObject(at: stateURL),
              let installedCommand = state["installedCommand"] as? String,
              try readJSONObject(at: settingsURL) != nil
        else { return }

        try updateSettings { settings in
            // Only ever removes CodeRim's own command. A status line the user
            // set since the install stays exactly as they left it.
            guard statusLineCommand(settings["statusLine"]) == installedCommand else { return false }
            if state["hadOriginal"] as? Bool == true,
               let original = state["originalStatusLine"], !(original is NSNull) {
                settings["statusLine"] = original
            } else {
                settings.removeValue(forKey: "statusLine")
            }
            return true
        }
        try? fileManager.removeItem(at: stateURL)
    }

    static func defaultSettingsURL(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> URL {
        if let configured = environment["CLAUDE_CONFIG_DIR"], configured.hasPrefix("/"),
           !configured.contains("\0") {
            return URL(fileURLWithPath: configured, isDirectory: true)
                .appendingPathComponent("settings.json")
        }
        return home.appendingPathComponent(".claude", isDirectory: true)
            .appendingPathComponent("settings.json")
    }

    static func resolveBundledBridge() throws -> URL {
        let executableDirectory = Bundle.main.executableURL?.deletingLastPathComponent()
        let candidates = [
            Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/CodeRimClaudeBridge"),
            executableDirectory?.appendingPathComponent("CodeRimClaudeBridge")
        ].compactMap { $0 }
        guard let source = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0.path) }) else {
            throw ClaudeIntegrationError.bridgeNotFound
        }
        return source
    }

    private func installBridge(from source: URL, to destination: URL) throws {
        guard fileManager.isExecutableFile(atPath: source.path) else {
            throw ClaudeIntegrationError.bridgeNotFound
        }
        if bridgeMatches(source: source, destination: destination) {
            try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: destination.path)
            // An earlier install (or a Homebrew-quarantined app bundle) may have
            // left the deployed copy flagged; clear it so Claude Code can exec it.
            Self.clearGatekeeperFlags(at: destination)
            return
        }
        let temporary = destination.deletingLastPathComponent()
            .appendingPathComponent(".CodeRimClaudeBridge-\(UUID().uuidString)")
        defer { try? fileManager.removeItem(at: temporary) }
        try fileManager.copyItem(at: source, to: temporary)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: temporary.path)
        // `copyItem` carries `com.apple.quarantine` across from the app bundle.
        // Claude Code runs this helper as its status-line command, and Gatekeeper
        // blocks a quarantined ad-hoc binary launched that way ("cannot be opened
        // because Apple cannot check it for malicious software"). CodeRim
        // wrote this file, for its own use — strip the flag it just inherited.
        Self.clearGatekeeperFlags(at: temporary)
        if fileManager.fileExists(atPath: destination.path) {
            let replaced = try fileManager.replaceItemAt(destination, withItemAt: temporary)
            Self.clearGatekeeperFlags(at: replaced ?? destination)
        } else {
            try fileManager.moveItem(at: temporary, to: destination)
            Self.clearGatekeeperFlags(at: destination)
        }
    }

    /// Remove `com.apple.quarantine` / `com.apple.provenance` from a file we
    /// deployed ourselves. Best-effort: a missing attribute is not an error.
    private static func clearGatekeeperFlags(at url: URL) {
        url.withUnsafeFileSystemRepresentation { path in
            guard let path else { return }
            for name in ["com.apple.quarantine", "com.apple.provenance"] {
                _ = removexattr(path, name, 0)
            }
        }
    }

    private func bridgeMatches(source: URL, destination: URL) -> Bool {
        guard fileManager.fileExists(atPath: destination.path),
              let sourceSize = try? source.resourceValues(forKeys: [.fileSizeKey]).fileSize,
              let destinationSize = try? destination.resourceValues(forKeys: [.fileSizeKey]).fileSize,
              sourceSize == destinationSize,
              let sourceData = try? Data(contentsOf: source, options: .mappedIfSafe),
              let destinationData = try? Data(contentsOf: destination, options: .mappedIfSafe)
        else { return false }
        return sourceData == destinationData
    }

    private func readJSONObject(at url: URL) throws -> [String: Any]? {
        try readJSON(at: url).object
    }

    /// The bytes as well as the decoded object, so a writer can prove nothing
    /// changed underneath it before replacing them.
    private func readJSON(at url: URL) throws -> (data: Data?, object: [String: Any]?) {
        guard fileManager.fileExists(atPath: url.path) else { return (nil, nil) }
        let data = try Data(contentsOf: url)
        guard data.count <= 1_048_576,
              let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { throw ClaudeIntegrationError.invalidSettings }
        return (data, object)
    }

    /// Read `settings.json`, apply `change`, and write it back only if nobody
    /// else wrote to it in between.
    ///
    /// This file belongs to Claude Code, which edits it while running. A plain
    /// read-modify-write drops whatever the CLI saved during the gap — a model
    /// preference, an MCP server, a permission the user just granted — and the
    /// loss is silent. So the write carries the bytes the decision was made
    /// from and is refused if they no longer match, and the whole thing is
    /// retried against the newer file. `change` returns false when the newer
    /// file needs no edit at all, which is how a retry stops.
    ///
    /// The same compare-and-swap the account code uses on `auth.json`, for the
    /// same reason: the other writer's copy is the one that must survive.
    private func updateSettings(_ change: (inout [String: Any]) -> Bool) throws {
        for _ in 0..<4 {
            let current = try readJSON(at: settingsURL)
            var settings = current.object ?? [:]
            guard change(&settings) else { return }
            do {
                return try writeJSONObject(settings, to: settingsURL, permissions: 0o600,
                                           expecting: current.data)
            } catch ClaudeIntegrationError.settingsChanged {
                continue
            }
        }
        throw ClaudeIntegrationError.settingsChanged
    }

    /// `expecting` is the file's contents when the caller last read it. Nil
    /// means it expects no file at all. Pass `.some(nil)`-style "don't care"
    /// only for files CodeRim owns outright, like the state file.
    private func writeJSONObject(_ object: [String: Any], to url: URL, permissions: Int,
                                 expecting original: Data?? = nil) throws {
        guard JSONSerialization.isValidJSONObject(object) else { throw ClaudeIntegrationError.invalidSettings }
        // A settings file kept in a dotfile repo is commonly a symlink. Writing
        // atomically to the link's own path replaces the link with a regular
        // file and quietly detaches it from the repo, so resolve first and
        // write through to whatever it points at.
        let destination = fileManager.fileExists(atPath: url.path)
            ? url.resolvingSymlinksInPath() : url
        try fileManager.createDirectory(
            at: destination.deletingLastPathComponent(),
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
        if let original {
            guard try readJSON(at: destination).data == original else {
                throw ClaudeIntegrationError.settingsChanged
            }
        }
        let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: destination, options: .atomic)
        try fileManager.setAttributes([.posixPermissions: permissions], ofItemAtPath: destination.path)
    }

    private func statusLineCommand(_ value: Any?) -> String? {
        (value as? [String: Any])?["command"] as? String
    }

    private func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }
}
