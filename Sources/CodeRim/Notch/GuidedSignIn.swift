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
        /// CodeRim runs the provider's own sign-in itself — a GitHub device code or a Google
        /// account consent in the browser — and stores what it returns. No tool to install.
        case inApp(InAppSignIn)
    }

    let name: String
    let action: Action
    /// What the user will see and what happens next, in one sentence.
    let note: String

    var actionTitle: String {
        switch action {
        case .terminal, .browser, .inApp: return "Sign in to \(name)"
        case .settings:           return "Enter \(name) key"
        }
    }

    /// Extra lines the Terminal window prints before the tool starts.
    var hint: String = ""
    /// Where to get the command a terminal sign-in runs, when it is not installed yet.
    var installURL: URL?
    /// Starting this sign-in is the user's go-ahead to read the provider's session from their
    /// browser. Without it a website sign-in could never be picked up: browser import is off
    /// until the user asks for it.
    var importsBrowserSession = false

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
            case .settings, .inApp: return false
            case .browser(let url): return NSWorkspace.shared.open(url)
            case .terminal(let command): return openTerminal(running: command, title: guided.name, hint: guided.hint)
            }
        }
    }

    /// Folders a login shell would add and a double-clicked `.command` file does not.
    static func toolDirectories(home: String = NSHomeDirectory()) -> [String] {
        var dirs = ["\(home)/.local/bin", "/opt/homebrew/bin", "/usr/local/bin", "\(home)/.bun/bin",
                    "\(home)/.cargo/bin", "\(home)/.npm-global/bin", "\(home)/.opencode/bin"]
        let nvm = "\(home)/.nvm/versions/node"
        for version in (try? FileManager.default.contentsOfDirectory(atPath: nvm)) ?? [] { dirs.append("\(nvm)/\(version)/bin") }
        return dirs
    }

    /// Where the first word of `command` is installed, if it is.
    static func installedTool(_ command: String, searchPath: [String] = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)) -> String? {
        guard let tool = command.split(separator: " ").first.map(String.init) else { return nil }
        if tool.hasPrefix("/") { return FileManager.default.isExecutableFile(atPath: tool) ? tool : nil }
        return (searchPath + toolDirectories()).map { "\($0)/\(tool)" }.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// Why a route cannot start, said plainly, when the command it runs is not on this Mac.
    static func problem(with route: SignInRoute) -> String? {
        guard case .guided(let guided) = route, case .terminal(let command) = guided.action,
              installedTool(command) == nil else { return nil }
        let tool = command.split(separator: " ").first.map(String.init) ?? command
        let page = guided.installURL.map { _ in " Its install page is opening; after installing, choose Try again." } ?? " Install it, then choose Try again."
        return "\(guided.name) needs the `\(tool)` command, which is not installed on this Mac.\(page)"
    }

    @MainActor static func openInstallPage(for route: SignInRoute) {
        if case .guided(let guided) = route, let url = guided.installURL { NSWorkspace.shared.open(url) }
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
        export PATH="\(Self.toolDirectories().joined(separator: ":")):$PATH"
        clear
        echo "Signing in to \(title.filter { $0.isLetter || $0.isNumber || $0 == " " }) for CodeRim."
        echo "When it finishes, return to CodeRim: it connects on its own."
        \(Self.hintBlock(hint))
        echo
        \(command)
        code=$?
        echo
        if [ $code -eq 0 ]; then echo "Done. Return to CodeRim: it connects on its own."; else echo "That did not finish (exit $code). Return to CodeRim and choose Try again."; fi
        """
        guard (try? script.write(to: file, atomically: true, encoding: .utf8)) != nil,
              (try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: file.path)) != nil
        else { return false }
        return NSWorkspace.shared.open(file)
    }
}
