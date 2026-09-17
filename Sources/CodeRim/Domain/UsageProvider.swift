import Foundation

/// Local histories are independent data sets, never a combined account total.
enum UsageProvider: String, CaseIterable, Identifiable, Sendable {
    case codex
    case claude

    var id: Self { self }
    var title: String { self == .codex ? "Codex" : "Claude Code" }
    var tabTitle: String { self == .codex ? "Codex" : "Claude" }
    var logoResourceName: String { self == .codex ? "OpenAI" : "Claude" }
    var symbol: String { self == .codex ? "terminal" : "sparkles" }
    var supportsAccountTotals: Bool { self == .codex }
    /// Claude API-equivalent pricing is not published here, so cost is never shown for it.
    var supportsCostEstimates: Bool { self == .codex }

    func analyticsHint(for destinationTitle: String) -> String {
        "Shows detailed local \(title) \(destinationTitle.lowercased())"
    }

    var databaseURL: URL {
        self == .codex ? AppPaths.databaseURL
            : AppPaths.applicationSupportDirectory.appendingPathComponent("Claude.sqlite")
    }

    func sourceRoots(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> [URL] {
        switch self {
        case .codex:
            return ["sessions", "archived_sessions"].map {
                home.appendingPathComponent(".codex", isDirectory: true)
                    .appendingPathComponent($0, isDirectory: true)
            }
        case .claude:
            let configured = environment["CLAUDE_CONFIG_DIR"]
            let root: URL
            if let configured, configured.hasPrefix("/"), !configured.contains("\0") {
                root = URL(fileURLWithPath: configured, isDirectory: true)
            } else {
                root = home.appendingPathComponent(".claude", isDirectory: true)
            }
            return [root.appendingPathComponent("projects", isDirectory: true)]
        }
    }
}
