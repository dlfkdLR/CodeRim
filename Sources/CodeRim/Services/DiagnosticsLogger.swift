import Foundation

enum DiagnosticEvent: Sendable {
    case refreshStarted
    case refreshCompleted(quality: DataQuality, sourceCount: Int, processedBytes: Int64)
    case refreshFailed(hasCachedSnapshot: Bool)
    case rebuildCompleted(quality: DataQuality)
    case rebuildFailed
    case clearCompleted
    case clearFailed

    fileprivate var message: String {
        switch self {
        case .refreshStarted:
            "refresh_started"
        case let .refreshCompleted(quality, sourceCount, processedBytes):
            "refresh_completed quality=\(quality.rawValue) sources=\(sourceCount) processed_bytes=\(processedBytes)"
        case let .refreshFailed(hasCachedSnapshot):
            "refresh_failed cached_snapshot=\(hasCachedSnapshot)"
        case let .rebuildCompleted(quality):
            "rebuild_completed quality=\(quality.rawValue)"
        case .rebuildFailed:
            "rebuild_failed"
        case .clearCompleted:
            "clear_completed"
        case .clearFailed:
            "clear_failed"
        }
    }
}

actor DiagnosticsLogger {
    static let shared = DiagnosticsLogger()

    private let maximumLogBytes: Int64 = 1_048_576
    private let fileManager = FileManager.default

    func record(_ event: DiagnosticEvent) {
        guard UserDefaults.standard.bool(forKey: "debugLogging") else { return }

        do {
            try prepareDirectory()
            let logURL = AppPaths.logDirectory.appendingPathComponent("CodeRim.log")
            try rotateIfNeeded(logURL)
            if !fileManager.fileExists(atPath: logURL.path) {
                fileManager.createFile(atPath: logURL.path, contents: nil)
            }
            try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: logURL.path)
            let line = "\(Date().ISO8601Format()) \(event.message)\n"
            let handle = try FileHandle(forWritingTo: logURL)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(line.utf8))
        } catch {
            // Diagnostics must never affect metering or surface sensitive error text.
        }
    }

    private func prepareDirectory() throws {
        try fileManager.createDirectory(at: AppPaths.logDirectory, withIntermediateDirectories: true)
        try fileManager.setAttributes(
            [.posixPermissions: 0o700],
            ofItemAtPath: AppPaths.logDirectory.path
        )
    }

    private func rotateIfNeeded(_ logURL: URL) throws {
        let size = try? logURL.resourceValues(forKeys: [.fileSizeKey]).fileSize
        guard Int64(size ?? 0) >= maximumLogBytes else { return }
        let archived = logURL.appendingPathExtension("1")
        if fileManager.fileExists(atPath: archived.path) {
            try fileManager.removeItem(at: archived)
        }
        try fileManager.moveItem(at: logURL, to: archived)
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: archived.path)
    }
}
