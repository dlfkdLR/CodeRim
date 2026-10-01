import AppKit
import Foundation

/// Which account Claude Desktop is signed in to, as far as CodeRim can tell.
///
/// Desktop keeps its own sign-in, separate from the Claude Code CLI's: an
/// encrypted token cache inside its `config.json`, which it rewrites while it
/// runs. CodeRim therefore never edits it. It only reads the account UUID
/// Desktop records beside it, so that after a CLI switch it can say when
/// Desktop is still on the other account and open Desktop to change it there.
enum ClaudeDesktopAccount {
    static let bundleIdentifier = "com.anthropic.claudefordesktop"

    static var configurationURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Claude/config.json")
    }

    /// Nil when Desktop is not installed, never signed in, or unreadable.
    static func accountID(configuration url: URL = configurationURL) -> String? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? NSNumber, size.intValue <= 4_194_304,
              let data = try? Data(contentsOf: url),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let id = object["lastKnownAccountUuid"] as? String, !id.isEmpty
        else { return nil }
        return id
    }

    /// True when Desktop is known to be signed in to a different account.
    static func differs(from accountID: String, configuration url: URL = configurationURL) -> Bool {
        guard let desktop = Self.accountID(configuration: url) else { return false }
        return desktop.caseInsensitiveCompare(accountID) != .orderedSame
    }

    @MainActor
    static func open() {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleIdentifier) else { return }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        NSWorkspace.shared.openApplication(at: url, configuration: configuration)
    }
}
