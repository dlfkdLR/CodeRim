import AppKit
import Darwin
import Foundation

/// Opens a Codex conversation or brings the terminal hosting an agent forward.
///
/// For process-based sessions, Claude Code publishes no window or tab. What it
/// does have is a parent chain — the shell that launched it, whose parent is
/// the terminal application — and a controlling tty and working directory.
/// The application is found by walking up the process tree; terminals with a
/// scripting interface are then asked for the exact tab:
///
/// - Terminal.app and iTerm2 match a tab by its tty.
/// - Ghostty (1.3+) matches a terminal surface by working directory, using the
///   session's title to choose between two sessions in the same folder.
///
/// Anything else, or a script the user declined, still gets the application
/// raised, so a click is never a silent no-op.
enum SessionFocus {
    enum Target: Equatable {
        case codexThread(URL)
        case application(pid_t)
    }

    static func target(for session: AgentSession) -> Target? {
        // The desktop app accepts UUID thread IDs at codex://threads/<id>.
        // Validate the raw ID so malformed metadata cannot change the route.
        if let id = session.codexThreadID, UUID(uuidString: id) != nil,
           let url = URL(string: "codex://threads/\(id)") {
            return .codexThread(url)
        }
        if let pid = session.processID, pid > 1 {
            return .application(pid)
        }
        return nil
    }

    @MainActor
    @discardableResult
    static func activate(_ session: AgentSession) -> Bool {
        switch target(for: session) {
        case .codexThread(let url): return NSWorkspace.shared.open(url)
        case .application(let pid):
            return activateApp(owning: pid, workingDirectory: session.workingDirectory, title: session.name)
        case nil: return false
        }
    }

    /// Raise whichever application owns this process, at the session's tab
    /// when the terminal can say which one that is.
    ///
    /// Returns false when the chain runs out before an application appears,
    /// which is the honest answer for an agent started by launchd, over ssh, or
    /// from a process that has since been reparented to init.
    @MainActor
    @discardableResult
    static func activateApp(owning pid: pid_t, workingDirectory: String? = nil, title: String? = nil) -> Bool {
        guard let app = owningApp(of: pid) else {
            NotchLog.usage.debug("no owning app for pid \(pid, privacy: .public)")
            return false
        }
        if let source = focusScript(bundleID: app.bundleIdentifier, tty: controllingTTY(of: pid),
                                    workingDirectory: workingDirectory, title: title) {
            var error: NSDictionary?
            let result = NSAppleScript(source: source)?.executeAndReturnError(&error)
            if error == nil, result?.booleanValue == true { return true }
            NotchLog.usage.debug("terminal focus script failed: \(String(describing: error), privacy: .public)")
        }
        // The notch panel never makes CodeRim the active app, and on macOS 14+
        // `NSRunningApplication.activate()` is cooperative — ignored when the
        // caller is not active. Launch Services activates from the background.
        if let url = app.bundleURL {
            let configuration = NSWorkspace.OpenConfiguration()
            configuration.activates = true
            NSWorkspace.shared.openApplication(at: url, configuration: configuration)
            return true
        }
        return app.activate()
    }

    /// The AppleScript that selects the session's tab, or nil for a terminal
    /// without a usable scripting interface. Every interpolated value is
    /// escaped; the script answers true only when it found the tab.
    static func focusScript(bundleID: String?, tty: String?, workingDirectory: String?, title: String?) -> String? {
        switch bundleID {
        case "com.apple.Terminal":
            guard let tty else { return nil }
            return """
            tell application id "com.apple.Terminal"
                repeat with w in windows
                    repeat with t in tabs of w
                        if tty of t is \(quoted(tty)) then
                            set selected of t to true
                            set index of w to 1
                            activate
                            return true
                        end if
                    end repeat
                end repeat
            end tell
            return false
            """
        case "com.googlecode.iterm2":
            guard let tty else { return nil }
            return """
            tell application id "com.googlecode.iterm2"
                repeat with w in windows
                    repeat with t in tabs of w
                        repeat with s in sessions of t
                            if tty of s is \(quoted(tty)) then
                                select w
                                tell t to select
                                tell s to select
                                activate
                                return true
                            end if
                        end repeat
                    end repeat
                end repeat
            end tell
            return false
            """
        case "com.mitchellh.ghostty":
            guard let workingDirectory else { return nil }
            return """
            tell application id "com.mitchellh.ghostty"
                set matches to every terminal whose working directory is \(quoted(workingDirectory))
                if (count of matches) is 0 then return false
                set chosen to item 1 of matches
                repeat with candidate in matches
                    if name of candidate contains \(quoted(title ?? "")) then
                        set chosen to contents of candidate
                        exit repeat
                    end if
                end repeat
                focus chosen
                activate
                return true
            end tell
            """
        default:
            return nil
        }
    }

    /// An AppleScript string literal for `text`.
    static func quoted(_ text: String) -> String {
        "\"" + text.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }

    /// `/dev/ttysNNN` for the process's controlling terminal, if it has one.
    static func controllingTTY(of pid: pid_t) -> String? {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        guard sysctl(&mib, u_int(mib.count), &info, &size, nil, 0) == 0, size > 0 else { return nil }
        let device = info.kp_eproc.e_tdev
        guard device != -1, let name = devname(device, S_IFCHR) else { return nil }
        return "/dev/" + String(cString: name)
    }

    /// The nearest ancestor process that macOS knows as a running application.
    static func owningApp(of pid: pid_t) -> NSRunningApplication? {
        for candidate in ancestry(of: pid) {
            if let app = NSRunningApplication(processIdentifier: candidate),
               app.bundleIdentifier != nil {
                return app
            }
        }
        return nil
    }

    /// The process and its parents, nearest first.
    ///
    /// Bounded rather than looped until pid 1: a corrupted `kinfo_proc` that
    /// reports itself as its own parent would otherwise spin forever, and no
    /// real chain from an agent to its terminal is more than a handful deep.
    static func ancestry(of pid: pid_t, limit: Int = 8) -> [pid_t] {
        var chain: [pid_t] = []
        var current = pid
        while chain.count < limit, current > 1 {
            chain.append(current)
            guard let parent = parent(of: current), parent != current else { break }
            current = parent
        }
        return chain
    }

    static func parent(of pid: pid_t) -> pid_t? {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        guard sysctl(&mib, u_int(mib.count), &info, &size, nil, 0) == 0, size > 0
        else { return nil }
        return info.kp_eproc.e_ppid
    }
}
