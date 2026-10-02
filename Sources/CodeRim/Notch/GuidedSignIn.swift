import AppKit
import Foundation

/// A sign-in CodeRim can start for the user, so adding a provider needs no second
/// "Set up" step: it opens the right place and then waits for the account to appear.
struct GuidedSignIn: Equatable {
    enum Action: Equatable {
        /// A Terminal window runs the provider's own login command.
        case terminal(command: String)
        /// The provider's login or dashboard page opens in the default browser.
        case browser(URL)
        /// An API key has to be pasted; CodeRim opens the provider's settings.
        case settings
    }

    let name: String
    let action: Action
    /// What the user will see and what happens next, in one sentence.
    let note: String

    var actionTitle: String {
        switch action {
        case .terminal, .browser: return "Sign in to \(name)"
        case .settings:           return "Enter \(name) key"
        }
    }

    /// Extra lines the Terminal window prints before the tool starts.
    var hint: String = ""

    var opensSettings: Bool { action == .settings }
}

/// Carries out a route. Nothing here reads or stores a credential.
enum SignInLauncher {
    /// Returns whether something was opened for the user.
    @MainActor @discardableResult
    static func perform(_ route: SignInRoute) -> Bool {
        switch route {
        case .modal, .guidance:
            return false
        case .openApp(let bundleID, _):
            guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return false }
            NSWorkspace.shared.openApplication(at: url, configuration: .init())
            return true
        case .guided(let guided):
            switch guided.action {
            case .settings: return false
            case .browser(let url): return NSWorkspace.shared.open(url)
            case .terminal(let command): return openTerminal(running: command, title: guided.name, hint: guided.hint)
            }
        }
    }

    /// The steps the user must take inside the tool, printed large and held on screen: the tool's
    /// own interface scrolls past a short line, so this waits for Return before starting it.
    static func hintBlock(_ hint: String) -> String {
        let lines = hint.split(separator: "\n").map { line in
            String(line.filter { $0.isLetter || $0.isNumber || " .,:/-()+".contains($0) })
        }.filter { !$0.isEmpty }
        guard !lines.isEmpty else { return "" }
        var out = ["echo", "printf '\\033[1;33m%s\\033[0m\\n' '=== DO THIS IN THIS WINDOW ==='"]
        out += lines.map { "printf '\\033[1;33m%s\\033[0m\\n' '\($0)'" }
        out += ["echo", "read -r '?Press Return to start. '"]
        return out.joined(separator: "\n        ")
    }

    /// A `.command` file is the one way to run something in Terminal without an
    /// AppleScript permission prompt. It deletes itself, and quotes nothing from the user.
    @MainActor
    static func openTerminal(running command: String, title: String, hint: String = "") -> Bool {
        guard command.allSatisfy({ $0.isLetter || $0.isNumber || " -_./".contains($0) }) else { return false }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("coderim-login", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        let file = directory.appendingPathComponent("\(UUID().uuidString).command")
        let script = """
        #!/bin/zsh
        rm -f "$0"
        clear
        echo "Signing in to \(title.filter { $0.isLetter || $0.isNumber || $0 == " " }) for CodeRim."
        echo "When it finishes, return to CodeRim: it connects on its own."
        \(Self.hintBlock(hint))
        echo
        \(command)
        echo
        echo "Done. You can close this window."
        """
        guard (try? script.write(to: file, atomically: true, encoding: .utf8)) != nil,
              (try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: file.path)) != nil
        else { return false }
        return NSWorkspace.shared.open(file)
    }
}
