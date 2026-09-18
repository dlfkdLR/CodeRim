import AppKit
import CoreServices
import Darwin
import Foundation
import os

/// Sparkle's prebuilt framework preserves the installed filename. Finish the
/// CodexMeter -> CodeRim rename before any stores, updater or resources start.
enum AppIdentityMigration {
    private static let log = Logger(subsystem: "dev.coderim.CodeRim", category: "identity")

    @MainActor
    static func prepareForLaunch() {
        let bundle = Bundle.main
        let roots = [URL(fileURLWithPath: "/Applications", isDirectory: true),
                     FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications")]
        if let destination = migrationDestination(for: bundle.bundleURL,
                                                   info: bundle.infoDictionary ?? [:], applicationDirectories: roots, caskrooms: homebrewCaskrooms) {
            // Start the relaunch helper before moving anything. EOF cancels it
            // on failure; only a successful atomic rename sends the go-ahead.
            let gate = Pipe()
            let helper = Process()
            helper.executableURL = URL(fileURLWithPath: "/bin/sh")
            helper.arguments = ["-c", relaunchScript, "coderim-rename",
                                String(ProcessInfo.processInfo.processIdentifier), destination.path, "/usr/bin/open"]
            helper.standardInput = gate
            helper.standardOutput = FileHandle.nullDevice
            helper.standardError = FileHandle.nullDevice
            do {
                try helper.run()
                try moveWithoutReplacing(from: bundle.bundleURL, to: destination)
            } catch {
                try? gate.fileHandleForWriting.close()
                log.error("Could not migrate application filename: \(error.localizedDescription, privacy: .public)")
                refreshRegistration(bundle: bundle)
                return
            }
            gate.fileHandleForWriting.write(Data("relaunch\n".utf8))
            try? gate.fileHandleForWriting.close()
            // Bundle.main caches its original path; a fresh process is required.
            // Nothing that owns user data has been initialized at this point.
            exit(EXIT_SUCCESS)
        }
        refreshRegistration(bundle: bundle)
    }

    /// Arguments carry paths literally, including spaces, quotes and shell syntax.
    /// A vetoed/delayed exit never starts a second instance after the deadline.
    static let relaunchScript = """
    IFS= read -r action || exit 0
    [ "$action" = relaunch ] || exit 0
    attempt=0
    while /bin/kill -0 "$1" 2>/dev/null; do
        attempt=$((attempt + 1))
        [ "$attempt" -lt 300 ] || exit 1
        /bin/sleep 0.2
    done
    exec "$3" -n "$2"
    """

    static func migrationDestination(for source: URL, info: [String: Any],
                                     applicationDirectories: [URL], caskrooms: [URL] = []) -> URL? {
        let source = source.standardizedFileURL
        guard source.lastPathComponent == "CodexMeter.app",
              info["CFBundleIdentifier"] as? String == "dev.codexmeter.CodexMeter",
              info["CFBundleExecutable"] as? String == "CodeRim",
              info["CFBundleName"] as? String == "CodeRim",
              let attributes = try? FileManager.default.attributesOfItem(atPath: source.path),
              attributes[.type] as? FileAttributeType == .typeDirectory else { return nil }
        let parent = source.deletingLastPathComponent().resolvingSymlinksInPath()
        guard applicationDirectories.contains(where: { $0.standardizedFileURL.resolvingSymlinksInPath() == parent })
        else { return nil }
        // Homebrew owns the installed path in its receipt. Let brew perform
        // its cask rename/uninstall/install migration instead of breaking it.
        guard !isHomebrewManaged(source, caskrooms: caskrooms) else { return nil }
        let destination = source.deletingLastPathComponent().appendingPathComponent("CodeRim.app", isDirectory: true)
        // Include dangling symlinks. Never replace a pre-existing destination.
        guard (try? FileManager.default.attributesOfItem(atPath: destination.path)) == nil else { return nil }
        return destination
    }

    private static var homebrewCaskrooms: [URL] {
        var paths = ["/opt/homebrew/Caskroom", "/usr/local/Caskroom"]
        if let custom = ProcessInfo.processInfo.environment["HOMEBREW_CASKROOM"], custom.hasPrefix("/") {
            paths.append(custom)
        }
        return paths.map { URL(fileURLWithPath: $0, isDirectory: true) }
    }

    static func isHomebrewManaged(_ source: URL, caskrooms: [URL]) -> Bool {
        let manager = FileManager.default
        let target = source.resolvingSymlinksInPath().standardizedFileURL
        for caskroom in caskrooms {
            for token in ["codexmeter", "coderim"] {
                let cask = caskroom.appendingPathComponent(token)
                let versions = (try? manager.contentsOfDirectory(at: cask, includingPropertiesForKeys: nil,
                                                                 options: [.skipsHiddenFiles])) ?? []
                for version in versions {
                    for name in ["CodexMeter.app", "CodeRim.app"] {
                        let artifact = version.appendingPathComponent(name)
                        guard (try? manager.destinationOfSymbolicLink(atPath: artifact.path)) != nil else { continue }
                        if artifact.resolvingSymlinksInPath().standardizedFileURL == target { return true }
                    }
                }
            }
        }
        return false
    }

    static func moveWithoutReplacing(from source: URL, to destination: URL) throws {
        // RENAME_EXCL closes the exists-check/rename race and keeps the app inode
        // intact so Finder aliases and existing login-item bookmarks can follow it.
        guard renamex_np(source.path, destination.path, UInt32(RENAME_EXCL)) == 0 else {
            throw POSIXError(.init(rawValue: errno) ?? .EIO)
        }
    }

    @MainActor
    private static func refreshRegistration(bundle: Bundle) {
        guard bundle.bundleURL.pathExtension == "app",
              bundle.bundleIdentifier == "dev.codexmeter.CodexMeter",
              let icon = bundle.object(forInfoDictionaryKey: "CFBundleIconFile") as? String,
              let version = bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String else { return }
        let registration = [bundle.bundlePath, version, icon].joined(separator: "|")
        let key = "registeredApplicationIdentity"
        guard UserDefaults.standard.string(forKey: key) != registration else { return }
        // Refresh only this app's public Launch Services entry. Notification
        // permissions, bundle identifier and system-wide caches remain intact.
        if LSRegisterURL(bundle.bundleURL as CFURL, true) == noErr {
            UserDefaults.standard.set(registration, forKey: key)
        }
    }
}
