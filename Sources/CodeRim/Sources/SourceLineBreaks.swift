import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

/// Owns numeric LF positions between async importer callbacks. The source
/// buffer stays with the collector and can be released at each read boundary.
struct SourceLineBreaks {
    private var cursor = 0
    #if CODERIM_RUST_ACCOUNTING
    private var offsets = [Int](repeating: 0, count: 128)
    private var batchStart = 0
    private var count = 0
    private var slot = 0
    #endif

    mutating func start(at index: Data.Index) {
        cursor = index
        #if CODERIM_RUST_ACCOUNTING
        slot = 0
        count = 0
        #endif
    }

    /// End only at a complete line. The last one may need the full parser.
    mutating func nextImportRun(in bytes: Data, from start: Data.Index, codex: Bool, maxLines: Int)
        -> (end: Data.Index, lines: Int, selected: Range<Data.Index>?, preselected: Bool)? {
        #if CODERIM_RUST_ACCOUNTING
        if codex {
            let offset = start - bytes.startIndex
            let run = bytes.withUnsafeBytes { (buffer: UnsafeRawBufferPointer) in
                cr_codex_import_run(buffer.bindMemory(to: UInt8.self).baseAddress?.advanced(by: offset),
                                    buffer.count - offset, cursor - start, maxLines)
            }
            guard run.lines > 0 else { return nil }
            cursor = start + run.consumed
            let newline = cursor - 1
            let selected = run.candidate_length == 0 ? nil : (newline - run.candidate_length)..<newline
            return (cursor, run.lines, selected, true)
        }
        #endif
        guard let newline = next(in: bytes) else { return nil }
        return (newline + 1, 1, start..<newline, false)
    }

    mutating func next(in bytes: Data) -> Data.Index? {
        #if CODERIM_RUST_ACCOUNTING
        if slot == count {
            guard cursor < bytes.endIndex else { return nil }
            batchStart = cursor
            let skip = cursor - bytes.startIndex
            count = bytes.withUnsafeBytes { (source: UnsafeRawBufferPointer) in
                offsets.withUnsafeMutableBufferPointer { destination in
                    let first = source.bindMemory(to: UInt8.self).baseAddress!.advanced(by: skip)
                    return cr_jsonl_line_breaks(first, source.count - skip, destination.baseAddress, destination.count)
                }
            }
            slot = 0
            cursor = count == offsets.count ? batchStart + offsets[count - 1] + 1 : bytes.endIndex
            guard count > 0 else { return nil }
        }
        defer { slot += 1 }
        return batchStart + offsets[slot]
        #else
        guard let at = bytes[cursor...].firstIndex(of: 0x0a) else {
            cursor = bytes.endIndex
            return nil
        }
        cursor = at + 1
        return at
        #endif
    }
}
