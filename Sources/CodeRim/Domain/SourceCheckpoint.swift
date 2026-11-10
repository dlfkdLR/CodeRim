import Foundation

struct SourceCheckpoint: Equatable, Sendable {
    let sourcePath: String
    var fileIdentity: String
    var generation: Int64
    var committedOffset: Int64
    var isSkippingOversizedLine = false
    var observedSize: Int64 = 0
    var modificationTimeNanoseconds: Int64 = 0
    var contentFingerprint = ""
    var hasPendingImport = false
    var sessionID: String?
    var inheritsHistory: Bool
    var sessionStartedAt: Date?
    var inheritedHistoryEndOrdinal: Int64?
    var historyReplayComplete: Bool = true
    var model: String?
    var projectPath: String?
    var parentSessionID: String?
    var projectName: String?
    var imageAttachmentCount: Int64 = 0

    static func fresh(sourcePath: String, fileIdentity: String, generation: Int64 = 0) -> SourceCheckpoint {
        SourceCheckpoint(
            sourcePath: sourcePath,
            fileIdentity: fileIdentity,
            generation: generation,
            committedOffset: 0,
            sessionID: nil,
            inheritsHistory: false,
            sessionStartedAt: nil,
            inheritedHistoryEndOrdinal: nil,
            model: nil,
            projectPath: nil,
            parentSessionID: nil,
            projectName: nil
        )
    }
}
