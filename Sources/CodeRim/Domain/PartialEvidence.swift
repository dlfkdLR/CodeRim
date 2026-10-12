import Foundation

/// A place in a session file whose tokens could not be counted: a malformed accounting row, one that failed
/// validation, or one too long to read. Stored with the time it belongs to, so the periods holding it read
/// as partial for as long as the history is kept, whether or not the file still exists.
struct PartialEvidence: Equatable, Sendable {
    /// `storageIdentifier` of the source path, as usage events record it.
    let source: String
    /// Byte offset of the row in its file; with `source`, it makes re-reading the same row idempotent.
    let position: Int64
    /// The row's own time when it can be read; otherwise when it was read, which is never earlier.
    let observedAt: Date
}
