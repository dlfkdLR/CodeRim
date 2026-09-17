import Darwin
import Foundation

enum CLIInstaller {
    static func install(appURL: URL = Bundle.main.bundleURL,
                        binDirectory: URL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".local/bin")) throws -> URL {
        let manager = FileManager.default
        let app = appURL.standardizedFileURL.resolvingSymlinksInPath()
        let helper = app.appendingPathComponent("Contents/Helpers/CodeRimCLI")
        guard manager.isExecutableFile(atPath: helper.path) else { throw InstallError.missingHelper }
        let directory = binDirectory.standardizedFileURL.resolvingSymlinksInPath()
        try manager.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent("coderim")
        func isManaged(_ target: String) -> Bool {
            managedTargets(app: app, helper: helper).contains(target)
        }
        func installLink(at link: URL) throws {
            if let target = try? manager.destinationOfSymbolicLink(atPath: link.path) {
                guard isManaged(target) else { throw InstallError.destinationExists }
                if target == helper.path { return }
                let temporary = directory.appendingPathComponent(".coderim-cli-\(UUID().uuidString)")
                defer { try? manager.removeItem(at: temporary) }
                try manager.createSymbolicLink(at: temporary, withDestinationURL: helper)
                guard rename(temporary.path, link.path) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
            } else {
                guard !manager.fileExists(atPath: link.path) else { throw InstallError.destinationExists }
                try manager.createSymbolicLink(at: link, withDestinationURL: helper)
            }
        }
        try installLink(at: destination)
        // Keep an existing app-managed legacy command working after the app moves.
        // A different tool named codexmeter is outside this installer's ownership.
        let legacy = directory.appendingPathComponent("codexmeter")
        if let target = try? manager.destinationOfSymbolicLink(atPath: legacy.path), isManaged(target) {
            try installLink(at: legacy)
        }
        return destination
    }

    /// App updates repair only existing links previously installed by this app.
    /// No CLI is installed for users who have never opted in.
    @discardableResult
    static func repairExistingInstallation(appURL: URL = Bundle.main.bundleURL,
        binDirectory: URL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".local/bin")) throws -> Int {
        let manager = FileManager.default
        let app = appURL.standardizedFileURL.resolvingSymlinksInPath()
        let helper = app.appendingPathComponent("Contents/Helpers/CodeRimCLI")
        guard manager.isExecutableFile(atPath: helper.path) else { return 0 }
        let directory = binDirectory.standardizedFileURL.resolvingSymlinksInPath()
        let managed = managedTargets(app: app, helper: helper)
        var repaired = 0
        for name in ["coderim", "codexmeter"] {
            let link = directory.appendingPathComponent(name)
            guard let target = try? manager.destinationOfSymbolicLink(atPath: link.path),
                  target != helper.path, managed.contains(target) else { continue }
            let temporary = directory.appendingPathComponent(".coderim-cli-\(UUID().uuidString)")
            defer { try? manager.removeItem(at: temporary) }
            try manager.createSymbolicLink(at: temporary, withDestinationURL: helper)
            guard rename(temporary.path, link.path) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
            repaired += 1
        }
        return repaired
    }

    private static func managedTargets(app: URL, helper: URL) -> Set<String> {
        [helper.path,
         app.appendingPathComponent("Contents/Helpers/CodexMeterCLI").path,
         app.deletingLastPathComponent().appendingPathComponent("CodexMeter.app/Contents/Helpers/CodexMeterCLI").path,
         app.deletingLastPathComponent().appendingPathComponent("CodexMeter.app/Contents/Helpers/CodeRimCLI").path]
    }

    enum InstallError: Error, LocalizedError {
        case missingHelper, destinationExists
        var errorDescription: String? {
            switch self {
            case .missingHelper: "The CLI helper is missing. Install the complete CodeRim app."
            case .destinationExists: "A different coderim command already exists. It has been left unchanged."
            }
        }
    }
}
