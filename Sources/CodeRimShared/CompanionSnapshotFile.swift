import Foundation
import Darwin

public enum CompanionSnapshotFile {
    public static var appGroup: String {
        Bundle.main.object(forInfoDictionaryKey: "CodexMeterAppGroup") as? String
            ?? "group.dev.codexmeter.CodexMeter"
    }
    public static var usesLocalFile: Bool {
        Bundle.main.object(forInfoDictionaryKey: "CodexMeterSnapshotTransport") as? String == "local-file"
    }
    public static let fileName = "snapshot.json"

    /// The CLI reads a separate owner-only copy without needing App Group entitlements.
    public static var cliURL: URL {
        // The widget's current home is its sandbox. Resolve the login user's home
        // before appending the one fixed file allowed by its local-build entitlement.
        loginHomeDirectory
            .appendingPathComponent("Library/Application Support/CodexMeter/Companion")
            .appendingPathComponent(fileName)
    }

    private static let loginHomeDirectory: URL = {
        // Foundation redirects even homeDirectory(forUser:) inside an app sandbox.
        // passwd is the OS account record, not an environment variable or user-supplied path.
        var entry = passwd()
        var found: UnsafeMutablePointer<passwd>?
        var buffer = [CChar](repeating: 0, count: max(16_384, Int(sysconf(_SC_GETPW_R_SIZE_MAX))))
        return buffer.withUnsafeMutableBufferPointer { storage in
            guard getpwuid_r(getuid(), &entry, storage.baseAddress, storage.count, &found) == 0,
                  found != nil, let directory = entry.pw_dir else {
                return FileManager.default.homeDirectoryForCurrentUser
            }
            return URL(fileURLWithPath: String(cString: directory), isDirectory: true)
        }
    }()

    public static var widgetURL: URL? {
        if usesLocalFile { return cliURL }
        return FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroup)?
            .appendingPathComponent("Companion", isDirectory: true)
            .appendingPathComponent(fileName)
    }

    public static func encode(_ snapshot: CompanionSnapshot, pretty: Bool = false) throws -> Data {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = pretty ? [.sortedKeys, .prettyPrinted] : [.sortedKeys]
        return try encoder.encode(snapshot)
    }

    public static func read(from url: URL) throws -> CompanionSnapshot {
        let info = try url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey, .isSymbolicLinkKey])
        guard info.isRegularFile == true, info.isSymbolicLink != true,
              let size = info.fileSize, size <= 1_048_576 else { throw SnapshotError.invalidFile }
        let data = try Data(contentsOf: url)
        guard data.count <= 1_048_576 else { throw SnapshotError.invalidFile }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let snapshot = try decoder.decode(CompanionSnapshot.self, from: data)
        guard snapshot.schemaVersion == CompanionSnapshot.currentSchemaVersion else {
            throw SnapshotError.unsupportedVersion
        }
        guard Set(snapshot.providers.map(\.id)).count == snapshot.providers.count,
              snapshot.providers.allSatisfy({ provider in
                  (provider.localUsage.map { usage in
                      usage.scope == "this-mac" && usage.totals.values.allSatisfy {
                          $0.inputTokens >= 0 && $0.outputTokens >= 0 && $0.cachedInputTokens >= 0
                              && $0.cachedInputTokens <= $0.inputTokens
                      }
                  } ?? true)
                  && (provider.history.map { history in
                      history.scope == "this-mac" && history.days.count <= 366 && history.totalTokens >= 0
                          && Set(history.days.map(\.date)).count == history.days.count
                          && history.days.map(\.date) == history.days.map(\.date).sorted()
                          && (history.estimatedCostUSD.map { $0.isFinite && $0 >= 0 } ?? true)
                          && history.days.allSatisfy {
                              $0.tokens >= 0 && ($0.estimatedCostUSD.map { $0.isFinite && $0 >= 0 } ?? true)
                          }
                  } ?? true)
                  && provider.limits.staleAfterSeconds.isFinite
                  && (1...3600).contains(provider.limits.staleAfterSeconds)
                  && provider.limits.windows.allSatisfy { window in
                      (window.usedPercent.map { $0.isFinite && $0 >= 0 } ?? true)
                          && (window.usedCount.map { $0 >= 0 } ?? true)
                          && (window.remainingCount.map { $0 >= 0 } ?? true)
                  }
              }) else { throw SnapshotError.invalidFile }
        return snapshot
    }

    public static func write(_ snapshot: CompanionSnapshot, to url: URL) throws {
        let manager = FileManager.default
        let directory = url.deletingLastPathComponent()
        // Never follow a replaced export directory or file into another location.
        for candidate in [directory, url] {
            if (try? candidate.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                throw SnapshotError.invalidFile
            }
        }
        try manager.createDirectory(at: directory, withIntermediateDirectories: true,
                                    attributes: [.posixPermissions: 0o700])
        let data = try encode(snapshot)
        try data.write(to: url, options: [.atomic])
        try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    public enum SnapshotError: Error, LocalizedError {
        case invalidFile, unsupportedVersion

        public var errorDescription: String? {
            switch self {
            case .invalidFile: "The usage snapshot is invalid."
            case .unsupportedVersion: "Update the CodeRim CLI to read this snapshot version."
            }
        }
    }
}
