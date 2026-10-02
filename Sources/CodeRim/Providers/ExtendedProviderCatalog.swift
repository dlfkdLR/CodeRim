import Foundation
import CodexBarCore

/// Provider identities follow the pinned upstream registry. Existing archive keys never change meaning.
enum ExtendedProviderCatalog {
    static let revision = "51ed16bdd3abe35ec53af99818e1b5f0d2a631d3"
    static let nativeIDs: Set<String> = ["codex", "claude", "copilot", "cursor", "grok",
        "opencode", "commandcode", "glm", "ollama", "gemini"]

    static func localID(_ provider: CodexBarCore.UsageProvider) -> String {
        switch provider {
        case .antigravity: "gemini"
        case .gemini: "gemini-cli"
        case .zai: "glm"
        case .opencodego: "opencode"
        case .opencode: "opencode-zen"
        default: provider.rawValue
        }
    }

    static let all: [ProviderDescriptor] = {
        // The pinned core offers only this process-local namespace for legacy session files.
        // Keep those caches out of another application's Application Support directory.
        setenv("CODEXBAR_TEST_SESSION_FILE_ISOLATION", "1", 1)
        if CodexBarCoreResourceSmoke.isRequested() { exit(CodexBarCoreResourceSmoke.run()) }
        return ProviderDescriptorRegistry.all
    }()
    static let additions = all.filter { !nativeIDs.contains(localID($0.id)) }

    static func descriptor(for localID: String) -> ProviderDescriptor? {
        all.first { self.localID($0.id) == localID }
    }

    static func isExtended(_ localID: String) -> Bool {
        !nativeIDs.contains(localID) && descriptor(for: localID) != nil
    }

    static func guide(for localID: String) -> ExtendedProviderGuide? {
        guard let descriptor = descriptor(for: localID) else { return nil }
        return ExtendedProviderGuides.all[descriptor.id.rawValue]
    }

    @MainActor static func makeProviders() -> [any NotchProvider] {
        additions.map { ExtendedNotchProvider(descriptor: $0) }
    }
}

extension ExtendedProviderCatalog {
    /// How adding this provider gets the user signed in, from what the provider reads:
    /// a website session opens its login page, an API key opens its settings, and a
    /// tool with a login command runs it. Anything else keeps its written guidance.
    static func signInRoute(for descriptor: ProviderDescriptor, displayName: String) -> SignInRoute {
        if descriptor.id == .gemini { return geminiSignInRoute(displayName: displayName) }
        let modes = descriptor.fetchPlan.sourceModes
        let summary = guide(for: localID(descriptor.id))?.summary ?? ""
        let page = [descriptor.metadata.dashboardURL, descriptor.metadata.subscriptionDashboardURL]
            .compactMap { $0 }.compactMap(URL.init(string:)).first { $0.scheme == "https" }
        if let page, modes.contains(.web) || descriptor.metadata.browserCookieOrder != nil {
            return .guided(.init(name: displayName, action: .browser(page),
                note: "Sign in on the \(displayName) website in your browser. CodeRim reads the signed-in session and connects on its own."))
        }
        if modes.contains(.api), !modes.contains(.web), !modes.contains(.oauth), !modes.contains(.cli) {
            return .guided(.init(name: displayName, action: .settings,
                note: "Paste your \(displayName) API key in its settings. " + summary))
        }
        if modes.contains(.api), let page {
            return .guided(.init(name: displayName, action: .browser(page),
                note: "Sign in to \(displayName), create an API key, and paste it in the provider's settings."))
        }
        return .guidance("Configure \(displayName) in its provider settings. " + summary)
    }
}

extension ExtendedProviderCatalog {
    /// Gemini reads the Google sign-in the Gemini CLI keeps, so there is no key to paste:
    /// the CLI's first run offers "Login with Google" and writes that sign-in itself.
    static func geminiSignInRoute(displayName: String, home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                  searchPath: [String] = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)) -> SignInRoute {
        let candidates = searchPath + ["/usr/local/bin", "/opt/homebrew/bin", home.appendingPathComponent(".npm-global/bin").path,
                                        home.appendingPathComponent(".local/bin").path]
        let cli = candidates.map { $0 + "/gemini" }.first { FileManager.default.isExecutableFile(atPath: $0) }
        if let cli {
            // A CLI set to an API key never offers Google sign-in by itself, and CodeRim cannot read
            // usage from a key: say so up front instead of leaving the sign-in waiting forever.
            if usesKeyAuthentication(home: home) {
                return .guided(.init(name: displayName, action: .terminal(command: cli),
                    note: "Your Gemini CLI is set to an API key, which CodeRim cannot read usage from. In Terminal: type /auth, choose Sign in with Google, finish in your browser, then type /quit. Personal Google accounts can no longer sign in through the Gemini CLI; if Google refuses, add Antigravity instead.",
                    hint: "Gemini is set to an API key, but CodeRim reads the Google sign-in.\n1. Type /auth and press Enter.\n2. Choose Sign in with Google and finish in your browser.\n3. When it says Authentication succeeded, type /quit.\nIf Google says this client is no longer supported for individuals, personal accounts moved to Antigravity: close this window and add Antigravity in CodeRim instead."))
            }
            return .guided(.init(name: displayName, action: .terminal(command: cli),
                note: "A Terminal window opens Gemini. Choose “Login with Google”, finish in your browser, then type /quit. CodeRim connects on its own.",
                hint: "1. Choose Sign in with Google and finish in your browser.\n2. When it says Authentication succeeded, type /quit.\nIf Google says this client is no longer supported for individuals, personal accounts moved to Antigravity: close this window and add Antigravity in CodeRim instead."))
        }
        return .guided(.init(name: displayName,
            action: .browser(URL(string: "https://github.com/google-gemini/gemini-cli#quickstart")!),
            note: "Install the Gemini CLI from this page, then choose Sign in again: it signs in with your Google account."))
    }
}

extension ExtendedProviderCatalog {
    static func usesKeyAuthentication(home: URL) -> Bool {
        let path = home.appendingPathComponent(".gemini/settings.json")
        guard let data = try? Data(contentsOf: path),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = ((json["security"] as? [String: Any])?["auth"] as? [String: Any])?["selectedType"] as? String
        else { return false }
        return type != "oauth-personal"
    }
}

struct ExtendedProviderGuide: Sendable {
    let document: String
    let summary: String
    let environmentKeys: [String]
    var url: URL {
        URL(string: "https://github.com/steipete/CodexBar/blob/\(ExtendedProviderCatalog.revision)/docs/\(document)")!
    }
}
