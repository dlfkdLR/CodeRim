import CryptoKit
import Darwin
import Foundation

enum SourceReadError: Error {
    case sourceUnavailable
    case sourceChangedDuringRead
}

struct FingerprintAdvanceResult {
    let bytesRead: Int64
    let reachedTarget: Bool
}

struct SourceFingerprintAccumulator {
    static let versionPrefix = "v2:"

    private var authenticator: HMAC<SHA256>
    private var legacyAuthenticator: HMAC<SHA256>?
    private(set) var authenticatedOffset: Int64 = 0

    init(key: Data, legacyOffset: Int64? = nil) {
        let symmetricKey = SymmetricKey(data: key)
        var authenticator = HMAC<SHA256>(key: symmetricKey)
        authenticator.update(data: Data("CodeRim.source-fingerprint.v2|".utf8))
        self.authenticator = authenticator

        if let legacyOffset {
            var legacy = HMAC<SHA256>(key: symmetricKey)
            legacy.update(data: Data("size:\(legacyOffset)|".utf8))
            legacyAuthenticator = legacy
        }
    }

    mutating func advance(
        fileDescriptor: Int32,
        to offset: Int64,
        maximumBytes: Int64
    ) throws -> FingerprintAdvanceResult {
        guard offset >= authenticatedOffset else {
            throw SourceReadError.sourceChangedDuringRead
        }
        let startingOffset = authenticatedOffset
        let byteLimit = authenticatedOffset + max(0, maximumBytes)
        while authenticatedOffset < offset, authenticatedOffset < byteLimit {
            try Task.checkCancellation()
            let byteCount = Int(min(
                Int64(256 * 1_024),
                offset - authenticatedOffset,
                byteLimit - authenticatedOffset
            ))
            let sample = try readFingerprintSample(
                fileDescriptor: fileDescriptor,
                startingAt: authenticatedOffset,
                byteCount: byteCount
            )
            append(sample)
        }
        return FingerprintAdvanceResult(
            bytesRead: authenticatedOffset - startingOffset,
            reachedTarget: authenticatedOffset == offset
        )
    }

    mutating func append(_ data: Data) {
        authenticator.update(data: data)
        legacyAuthenticator?.update(data: data)
        authenticatedOffset += Int64(data.count)
    }

    var fingerprint: String {
        let copy = authenticator
        return Self.versionPrefix + Self.hexDigest(copy.finalize())
    }

    var legacyFingerprint: String? {
        guard let copy = legacyAuthenticator else { return nil }
        return Self.hexDigest(copy.finalize())
    }

    mutating func discardLegacyFingerprint() {
        legacyAuthenticator = nil
    }

    private static func hexDigest(_ authenticationCode: HMAC<SHA256>.MAC) -> String {
        Data(authenticationCode).map { String(format: "%02x", $0) }.joined()
    }
}

private struct FingerprintVerificationState {
    let fileIdentity: String
    let generation: Int64
    let committedOffset: Int64
    let contentFingerprint: String
    let observedSize: Int64
    let modificationTimeNanoseconds: Int64
    let statusChangeTimeNanoseconds: Int64
    var accumulator: SourceFingerprintAccumulator

    func matches(
        _ checkpoint: SourceCheckpoint,
        openedSize: Int64,
        openedModificationTime: Int64,
        openedStatusChangeTime: Int64
    ) -> Bool {
        fileIdentity == checkpoint.fileIdentity
            && generation == checkpoint.generation
            && committedOffset == checkpoint.committedOffset
            && contentFingerprint == checkpoint.contentFingerprint
            && observedSize == openedSize
            && modificationTimeNanoseconds == openedModificationTime
            && statusChangeTimeNanoseconds == openedStatusChangeTime
            && accumulator.authenticatedOffset <= checkpoint.committedOffset
    }
}

private func readFingerprintSample(
    fileDescriptor: Int32,
    startingAt start: Int64,
    byteCount: Int
) throws -> Data {
    var bytes = [UInt8](repeating: 0, count: byteCount)
    let readCount = bytes.withUnsafeMutableBytes { buffer -> Int in
        guard let baseAddress = buffer.baseAddress else { return 0 }
        var total = 0
        while total < byteCount {
            let result = Darwin.pread(
                fileDescriptor,
                baseAddress.advanced(by: total),
                byteCount - total,
                off_t(start + Int64(total))
            )
            if result < 0, errno == EINTR { continue }
            guard result > 0 else { return total }
            total += result
        }
        return total
    }
    guard readCount == byteCount else {
        throw SourceReadError.sourceChangedDuringRead
    }
    return Data(bytes)
}

/// File descriptors, frozen reads and authenticated-prefix caches belong to the
/// collector's executor. This reader never opens the database or interprets rows.
final class SourceIntegrityReader {
    private var fingerprintVerificationStates: [String: FingerprintVerificationState] = [:]
    private var readSnapshots: [String: SourceReadSnapshot] = [:]

    func prune(activeKeys: Set<String>) {
        fingerprintVerificationStates = fingerprintVerificationStates.filter { activeKeys.contains($0.key) }
        readSnapshots = readSnapshots.filter { activeKeys.contains($0.key) }
    }

    func reset() {
        readSnapshots.removeAll()
        fingerprintVerificationStates.removeAll()
    }

    func invalidateFingerprint(_ key: String) {
        fingerprintVerificationStates.removeValue(forKey: key)
    }

    func accumulator(key: String, checkpoint: SourceCheckpoint, keyData: Data, metadata: stat) -> SourceFingerprintAccumulator {
        if let saved = fingerprintVerificationStates[key],
           saved.matches(checkpoint, openedSize: Int64(metadata.st_size),
               openedModificationTime: modificationTimeNanoseconds(metadata),
               openedStatusChangeTime: statusChangeTimeNanoseconds(metadata)) {
            return saved.accumulator
        }
        invalidateFingerprint(key)
        let legacy = !checkpoint.contentFingerprint.isEmpty
            && !checkpoint.contentFingerprint.hasPrefix(SourceFingerprintAccumulator.versionPrefix)
        return SourceFingerprintAccumulator(key: keyData, legacyOffset: legacy ? checkpoint.committedOffset : nil)
    }

    func open(_ source: CodexSessionSource) throws -> (handle: FileHandle, metadata: stat) {
        let descriptor = Darwin.open(source.url.path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
        guard descriptor >= 0 else { throw SourceReadError.sourceUnavailable }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        var metadata = stat()
        guard fstat(descriptor, &metadata) == 0, (metadata.st_mode & S_IFMT) == S_IFREG,
              "\(UInt64(metadata.st_dev)):\(UInt64(metadata.st_ino))" == source.identity else {
            try? handle.close()
            throw SourceReadError.sourceChangedDuringRead
        }
        return (handle, metadata)
    }

    enum ReadSelection {
        case live
        case snapshot(SourceReadSnapshot)
        case deferred
    }

    func selectRead(key: String, descriptor: Int32, metadata: stat,
                    checkpoint: SourceCheckpoint, snapshotThreshold: Int64) -> ReadSelection {
        if let snapshot = readSnapshots[key], !snapshot.accepts(metadata) {
            readSnapshots.removeValue(forKey: key)
            invalidateFingerprint(key)
        }
        let unchanged = checkpoint.fileIdentity == "\(UInt64(metadata.st_dev)):\(UInt64(metadata.st_ino))"
            && checkpoint.observedSize == Int64(metadata.st_size)
            && checkpoint.modificationTimeNanoseconds == modificationTimeNanoseconds(metadata)
            && checkpoint.committedOffset >= Int64(metadata.st_size) && !checkpoint.hasPendingImport
        if readSnapshots[key] == nil, !unchanged, Int64(metadata.st_size) > snapshotThreshold {
            // Bound descriptors while small live sources continue to make progress.
            guard readSnapshots.count < 4 else { return .deferred }
            readSnapshots[key] = SourceReadSnapshot.capture(descriptor: descriptor, metadata: metadata)
        }
        if let snapshot = readSnapshots[key] { return .snapshot(snapshot) }
        return .live
    }

    func finishRead(key: String, completed: Bool) {
        if completed { readSnapshots.removeValue(forKey: key) }
    }

    /// Fingerprints and checkpoints describe the original identity at capture
    /// time, while every byte in this pass comes from the same frozen clone.
    private func readSourceStat(_ descriptor: Int32, _ value: inout stat) -> Int32 {
        let result = fstat(descriptor, &value)
        guard result == 0 else { return result }
        if let snapshot = readSnapshots.values.first(where: { $0.handle.fileDescriptor == descriptor }) {
            guard value.st_size == snapshot.metadata.st_size else { return -1 }
            value = snapshot.metadata
        }
        return 0
    }

    func updateCheckpointFingerprint(
        _ checkpoint: inout SourceCheckpoint,
        accumulator: inout SourceFingerprintAccumulator,
        expectedFingerprint: String,
        expectedStatusChangeTime: Int64,
        fileDescriptor: Int32
    ) throws -> Int64 {
        var currentStat = stat()
        guard readSourceStat(fileDescriptor, &currentStat) == 0,
              (currentStat.st_mode & S_IFMT) == S_IFREG,
              Int64(currentStat.st_size) >= checkpoint.committedOffset
        else {
            throw SourceReadError.sourceChangedDuringRead
        }
        // Keep the opened metadata: a later append or rewrite must be checked on the next pass.
        let advance = try accumulator.advance(
            fileDescriptor: fileDescriptor,
            to: checkpoint.committedOffset,
            maximumBytes: checkpoint.committedOffset - accumulator.authenticatedOffset
        )
        guard advance.reachedTarget, accumulator.fingerprint == expectedFingerprint,
              readSourceStat(fileDescriptor, &currentStat) == 0,
              Int64(currentStat.st_size) > checkpoint.observedSize
                || (Int64(currentStat.st_size) == checkpoint.observedSize
                    && modificationTimeNanoseconds(currentStat) == checkpoint.modificationTimeNanoseconds
                    && statusChangeTimeNanoseconds(currentStat) == expectedStatusChangeTime)
        else {
            throw SourceReadError.sourceChangedDuringRead
        }
        checkpoint.contentFingerprint = accumulator.fingerprint
        accumulator.discardLegacyFingerprint()
        return advance.bytesRead
    }

    func cacheFingerprintState(
        checkpointKey: String,
        checkpoint: SourceCheckpoint,
        observedSize: Int64,
        modificationTimeNanoseconds expectedModificationTimeNanoseconds: Int64,
        fileDescriptor: Int32,
        accumulator: SourceFingerprintAccumulator
    ) {
        var currentStat = stat()
        guard accumulator.authenticatedOffset <= checkpoint.committedOffset,
              readSourceStat(fileDescriptor, &currentStat) == 0,
              (currentStat.st_mode & S_IFMT) == S_IFREG,
              "\(UInt64(currentStat.st_dev)):\(UInt64(currentStat.st_ino))" == checkpoint.fileIdentity,
              Int64(currentStat.st_size) == observedSize,
              modificationTimeNanoseconds(currentStat) == expectedModificationTimeNanoseconds
        else {
            fingerprintVerificationStates.removeValue(forKey: checkpointKey)
            return
        }
        fingerprintVerificationStates[checkpointKey] = FingerprintVerificationState(
            fileIdentity: checkpoint.fileIdentity,
            generation: checkpoint.generation,
            committedOffset: checkpoint.committedOffset,
            contentFingerprint: checkpoint.contentFingerprint,
            observedSize: observedSize,
            modificationTimeNanoseconds: expectedModificationTimeNanoseconds,
            statusChangeTimeNanoseconds: statusChangeTimeNanoseconds(currentStat),
            accumulator: accumulator
        )
    }

    func modificationTimeNanoseconds(_ value: stat) -> Int64 {
        Int64(value.st_mtimespec.tv_sec) * 1_000_000_000
            + Int64(value.st_mtimespec.tv_nsec)
    }

    func statusChangeTimeNanoseconds(_ value: stat) -> Int64 {
        Int64(value.st_ctimespec.tv_sec) * 1_000_000_000
            + Int64(value.st_ctimespec.tv_nsec)
    }

    func verifyOpenedSourceState(
        fileDescriptor: Int32,
        expectedIdentity: String,
        expectedSize: Int64,
        expectedModificationTime: Int64,
        expectedStatusChangeTime: Int64
    ) throws {
        var currentStat = stat()
        guard readSourceStat(fileDescriptor, &currentStat) == 0,
              (currentStat.st_mode & S_IFMT) == S_IFREG,
              "\(UInt64(currentStat.st_dev)):\(UInt64(currentStat.st_ino))" == expectedIdentity,
              Int64(currentStat.st_size) == expectedSize,
              modificationTimeNanoseconds(currentStat) == expectedModificationTime,
              statusChangeTimeNanoseconds(currentStat) == expectedStatusChangeTime
        else {
            throw SourceReadError.sourceChangedDuringRead
        }
    }

}
