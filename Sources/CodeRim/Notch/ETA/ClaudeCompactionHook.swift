import Foundation

/// Knowing that Claude Code is compacting *while* it compacts.
///
/// The transcript records a compaction only once it is over, so the notch
/// cannot see one in progress. Claude Code can tell us, though: it runs hooks
/// before (`PreCompact`) and after (`PostCompact`) it. Installing two small
/// hooks that drop and remove a marker file per session is the only reliable
/// signal there is.
///
/// This edits the user's own `~/.claude/settings.json`, so it is opt-in, keeps
/// a backup, touches nothing but its own two entries, and can remove them
/// again.
struct ClaudeCompactionHook {
    enum Failure: Error, Equatable {
        /// The settings file exists but is not a JSON object; it is left alone.
        case unreadableSettings
        case cannotWrite(String)
    }

    let settings: URL
    /// Where the hook script lives. No spaces, so the command needs no quoting.
    let script: URL
    let markers: URL

    static let standard: ClaudeCompactionHook = {
        let home = URL(fileURLWithPath: NSHomeDirectory())
        return ClaudeCompactionHook(
            settings: home.appendingPathComponent(".claude/settings.json"),
            script: home.appendingPathComponent(".coderim/hooks/compaction.sh"),
            markers: home.appendingPathComponent(".coderim/compacting"))
    }()

    /// What identifies an entry as ours when removing it.
    private static let signature = "coderim/hooks/compaction.sh"

    // MARK: - Status

    var isInstalled: Bool {
        guard let root = readSettings(),
              let hooks = root["hooks"] as? [String: Any] else { return false }
        return ["PreCompact", "PostCompact"].allSatisfy { event in
            (hooks[event] as? [[String: Any]] ?? []).contains(where: Self.isOurs)
        } && FileManager.default.isExecutableFile(atPath: script.path)
    }

    // MARK: - Install / remove

    func install() throws {
        var root = try loadForEditing()
        var hooks = root["hooks"] as? [String: Any] ?? [:]
        for (event, argument) in [("PreCompact", "pre"), ("PostCompact", "post")] {
            var groups = (hooks[event] as? [[String: Any]] ?? []).filter { !Self.isOurs($0) }
            groups.append(["hooks": [[
                "type": "command",
                "command": "\"$HOME/.coderim/hooks/compaction.sh\" \(argument)",
                "timeout": 5,
            ] as [String: Any]]])
            hooks[event] = groups
        }
        root["hooks"] = hooks
        try writeScript()
        try save(root)
    }

    func uninstall() throws {
        var root = try loadForEditing()
        if var hooks = root["hooks"] as? [String: Any] {
            for event in ["PreCompact", "PostCompact"] {
                let kept = (hooks[event] as? [[String: Any]] ?? []).filter { !Self.isOurs($0) }
                hooks[event] = kept.isEmpty ? nil : kept
            }
            root["hooks"] = hooks.isEmpty ? nil : hooks
            try save(root)
        }
        try? FileManager.default.removeItem(at: script)
        try? FileManager.default.removeItem(at: markers)
    }

    // MARK: - Markers

    /// Sessions that are compacting right now. A marker older than `maxAge`
    /// belongs to a compaction that never reported finishing (the process was
    /// killed), and is ignored.
    func compactingSessionIDs(now: Date = Date(), maxAge: TimeInterval = 15 * 60) -> Set<String> {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: markers.path)) ?? []
        return Set(names.filter { name in
            let attributes = try? FileManager.default.attributesOfItem(
                atPath: markers.appendingPathComponent(name).path)
            guard let modified = attributes?[.modificationDate] as? Date else { return false }
            return now.timeIntervalSince(modified) <= maxAge
        })
    }

    // MARK: - Internals

    private static func isOurs(_ group: [String: Any]) -> Bool {
        (group["hooks"] as? [[String: Any]] ?? []).contains {
            ($0["command"] as? String)?.contains(signature) == true
        }
    }

    private func readSettings() -> [String: Any]? {
        guard let data = try? Data(contentsOf: settings) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    /// The current settings, or an empty object when there is no file yet. A
    /// file that exists but does not parse is never overwritten.
    private func loadForEditing() throws -> [String: Any] {
        guard FileManager.default.fileExists(atPath: settings.path) else { return [:] }
        guard let root = readSettings() else { throw Failure.unreadableSettings }
        return root
    }

    private func save(_ root: [String: Any]) throws {
        do {
            let manager = FileManager.default
            try manager.createDirectory(at: settings.deletingLastPathComponent(),
                                        withIntermediateDirectories: true)
            if manager.fileExists(atPath: settings.path) {
                let backup = settings.appendingPathExtension("coderim-backup")
                if !manager.fileExists(atPath: backup.path) { try manager.copyItem(at: settings, to: backup) }
            }
            let data = try JSONSerialization.data(
                withJSONObject: root, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
            try data.write(to: settings, options: .atomic)
        } catch {
            throw Failure.cannotWrite(error.localizedDescription)
        }
    }

    /// The session id is the only thing taken from the hook's input, and it is
    /// checked against a strict alphabet before it names a file.
    private func writeScript() throws {
        let text = """
        #!/bin/sh
        # Installed by CodeRim so the notch can show "compacting". Safe to delete.
        dir="$HOME/.coderim/compacting"
        mkdir -p "$dir"
        sid=$(sed -n 's/.*"session_id"[[:space:]]*:[[:space:]]*"\\([^"]*\\)".*/\\1/p' | head -n 1)
        case "$sid" in ''|*[!A-Za-z0-9._-]*) exit 0 ;; esac
        case "$1" in
          pre)  : > "$dir/$sid" ;;
          post) rm -f "$dir/$sid" ;;
        esac
        exit 0

        """
        do {
            try FileManager.default.createDirectory(at: script.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            try text.write(to: script, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        } catch {
            throw Failure.cannotWrite(error.localizedDescription)
        }
    }
}
