import Foundation
import Darwin
#if CODERIM_RUST_WIDGET_HISTORY
import CCodeRimWidget
#endif
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

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
                  && (provider.history.map(validHistory) ?? true)
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

    private static func validHistory(_ history: CompanionHistory) -> Bool {
        guard history.scope == "this-mac", history.days.count <= 366 else { return false }
        #if CODERIM_RUST_WIDGET_HISTORY
        return withUnsafeTemporaryAllocation(of: CRWidgetHistoryDay.self, capacity: history.days.count) { buffer in
            for (index, day) in history.days.enumerated() {
                buffer.initializeElement(at: index, to: CRWidgetHistoryDay(
                    reference_seconds: day.date.timeIntervalSinceReferenceDate, tokens: day.tokens,
                    cost: day.estimatedCostUSD ?? 0))
            }
            return cr_widget_valid_history(buffer.baseAddress, buffer.count, history.totalTokens, history.estimatedCostUSD ?? 0)
        }
        #else
        return history.totalTokens >= 0
            && Set(history.days.map(\.date)).count == history.days.count
            && history.days.map(\.date) == history.days.map(\.date).sorted()
            && (history.estimatedCostUSD.map { $0.isFinite && $0 >= 0 } ?? true)
            && history.days.allSatisfy {
                $0.tokens >= 0 && ($0.estimatedCostUSD.map { $0.isFinite && $0 >= 0 } ?? true)
            }
        #endif
    }

    public static func write(_ snapshot: CompanionSnapshot, to url: URL) throws {
        #if CODERIM_RUST_ACCOUNTING
        if url.isFileURL {
            let data: Data
            do {
                data = try encode(snapshot)
            } catch {
                // Preserve directory creation and failure precedence for an unencodable snapshot.
                try prepareDestination(url)
                throw error
            }
            func publish() -> Int32 {
                url.withUnsafeFileSystemRepresentation { path in
                    data.withUnsafeBytes { bytes in
                        cr_publish_companion_file(path, bytes.bindMemory(to: UInt8.self).baseAddress, bytes.count)
                    }
                }
            }
            var status = publish()
            if status != 0 && status != ELOOP {
                // An existing directory was already checked through its open descriptor.
                // Use Foundation's original directory creation and errors on failure.
                try prepareDestination(url)
                if status == ENOENT { status = publish() }
            }
            if status == ELOOP { throw SnapshotError.invalidFile }
            if status != 0 { throw writeError(status, url: url) }
            return
        }
        #endif
        try prepareDestination(url)
        let data = try encode(snapshot)
        try data.write(to: url, options: [.atomic])
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    private static func prepareDestination(_ url: URL) throws {
        let directory = url.deletingLastPathComponent()
        for candidate in [directory, url] {
            if (try? candidate.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                throw SnapshotError.invalidFile
            }
        }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
    }

    #if CODERIM_RUST_ACCOUNTING
    private static func writeError(_ status: Int32, url: URL) -> NSError {
        let code: CocoaError.Code = switch status {
        case ENOENT: .fileNoSuchFile
        case EACCES, EPERM: .fileWriteNoPermission
        case ENAMETOOLONG: .fileWriteInvalidFileName
        case ENOSPC, EDQUOT: .fileWriteOutOfSpace
        case EROFS: .fileWriteVolumeReadOnly
        case EEXIST: .fileWriteFileExists
        default: .fileWriteUnknown
        }
        return NSError(domain: NSCocoaErrorDomain, code: code.rawValue,
                       userInfo: [NSFilePathErrorKey: url.path, NSURLErrorKey: url,
                                  NSUnderlyingErrorKey: NSError(domain: NSPOSIXErrorDomain, code: Int(status))])
    }
    #endif

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
