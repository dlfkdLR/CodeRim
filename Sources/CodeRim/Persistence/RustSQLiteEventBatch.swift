#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
import CodeRimUsageCore
import Foundation

struct RustSQLiteEventBatch {
    static let rowLimit = 256
    private static let textLimit = 8 * 1024 * 1024
    private enum PackingError: Error { case oversized }
    private var text = [UInt8]()
    private var rows = [cr_import_event]()

    /// A nil result means packing exceeded the memory budget before SQLite was touched.
    static func insert(_ events: ArraySlice<UsageEvent>, into statement: OpaquePointer,
                       generation: Int64, mergesMessageUsage: Bool) -> cr_import_result? {
        var batch = Self()
        batch.rows.reserveCapacity(events.count)
        do {
            for event in events {
                try batch.append(event, mergesMessageUsage: mergesMessageUsage)
            }
        } catch {
            return nil
        }
        return batch.rows.withUnsafeBufferPointer { rows in
            batch.text.withUnsafeBufferPointer { text in
                cr_import_usage_events(UnsafeMutableRawPointer(statement), rows.baseAddress, rows.count,
                                       text.baseAddress, text.count, generation, mergesMessageUsage ? 1 : 0)
            }
        }
    }

    private mutating func append(_ value: String?) throws -> cr_import_text {
        guard let value else { return cr_import_text(offset: 0, length: -1) }
        let bytes = value.utf8
        let length = bytes.count
        guard length <= Self.textLimit - text.count else { throw PackingError.oversized }
        let span = cr_import_text(offset: UInt32(text.count), length: Int32(length))
        text.append(contentsOf: bytes)
        return span
    }

    private mutating func append(_ event: UsageEvent, mergesMessageUsage: Bool) throws {
        let usage = event.usage
        let row = try cr_import_event(
            usage: cr_usage(input: usage.inputTokens, cached: usage.cachedInputTokens,
                            output: usage.outputTokens, written: usage.cacheWriteInputTokens ?? 0,
                            has_written: usage.cacheWriteInputTokens == nil ? 0 : 1),
            occurred_at: event.occurredAt.timeIntervalSince1970, position: event.sourcePosition,
            pricing: event.pricingContext.map { Int64($0.rawValue) } ?? -1,
            key: append(event.eventKey), session: append(event.sessionID), model: append(event.model),
            project: append(event.projectPath), source: append(event.sourcePath),
            parent: append(mergesMessageUsage ? event.importParentSessionID : nil)
        )
        rows.append(row)
    }
}
#endif
