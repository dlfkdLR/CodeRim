import CryptoKit
import Foundation
import os

/// A short display label from the first user request, kept in memory only.
/// Attachment and environment messages can precede that request as separate records.
enum CodexChatTitle {
    static let maximumScanBytes = 1_024 * 1_024
    private struct CachedTitle {
        let size: UInt64
        let modified: Date
        let title: String?
        let scannedBytes: UInt64
        let prefixDigest: Data
    }
    private static let cache = OSAllocatedUnfairLock(initialState: [String: CachedTitle]())

    static func request(in text: String) -> String? {
        var text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("# Files mentioned by the user:") {
            guard let marker = text.range(of: "## My request:") else { return nil }
            text = String(text[marker.upperBound...]).trimmingCharacters(in: .whitespacesAndNewlines)
        }
        guard !text.isEmpty, !text.hasPrefix("# AGENTS.md instructions"),
              !text.hasPrefix("<"), !text.contains("\0"),
              let first = text.split(whereSeparator: \.isNewline).first else { return nil }
        let line = first.trimmingCharacters(in: .whitespaces)
        guard !line.isEmpty else { return nil }
        return line.count > 160 ? String(line.prefix(159)) + "…" : line
    }

    static func read(_ url: URL) -> String? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              attributes[.type] as? FileAttributeType == .typeRegular,
              let size = attributes[.size] as? NSNumber,
              let modified = attributes[.modificationDate] as? Date,
              let inode = attributes[.systemFileNumber] as? NSNumber,
              let device = attributes[.systemNumber] as? NSNumber else { return nil }
        let key = "\(url.path)|\(device)|\(inode)"
        let saved = cache.withLock { $0[key] }
        if let saved {
            if size.uint64Value == saved.size, modified == saved.modified { return saved.title }
            if size.uint64Value > saved.size, saved.title == nil,
               saved.size >= UInt64(maximumScanBytes) { return nil }
        }
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        if let saved, saved.title != nil, size.uint64Value > saved.size,
           let prefix = try? handle.read(upToCount: Int(saved.scannedBytes)),
           prefix.count == Int(saved.scannedBytes), Data(SHA256.hash(data: prefix)) == saved.prefixDigest {
            // Validate only the small prefix containing the request, without
            // decoding every record again as active transcripts append output.
            let updated = CachedTitle(size: size.uint64Value, modified: modified, title: saved.title,
                                      scannedBytes: saved.scannedBytes, prefixDigest: saved.prefixDigest)
            cache.withLock { $0[key] = updated }
            return saved.title
        }
        var cursor = TranscriptLineCursor()
        var title: String?
        do {
            // Read one chunk at a time so a short request does not read the whole prefix.
            let end = min(size.uint64Value, UInt64(maximumScanBytes))
            while title == nil, cursor.readOffset < end {
                let previous = cursor.readOffset
                try cursor.read(handle, through: min(end, previous + UInt64(TranscriptLineCursor.chunkBytes))) { line in
                    guard title == nil,
                          let json = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                          let payload = json["payload"] as? [String: Any] else { return }
                    if json["type"] as? String == "event_msg", payload["type"] as? String == "user_message",
                       let message = payload["message"] as? String {
                        title = request(in: message)
                    } else if json["type"] as? String == "response_item",
                              payload["type"] as? String == "message", payload["role"] as? String == "user",
                              let content = payload["content"] as? [[String: Any]] {
                        for part in content where part["type"] as? String == "input_text" {
                            if let text = part["text"] as? String, let label = request(in: text) {
                                title = label
                                break
                            }
                        }
                    }
                }
                guard cursor.readOffset > previous else { break }
            }
        } catch { return nil }
        guard (try? handle.seek(toOffset: 0)) != nil,
              let prefix = try? handle.read(upToCount: Int(cursor.readOffset)),
              prefix.count == Int(cursor.readOffset) else { return nil }
        let resolved = CachedTitle(size: size.uint64Value, modified: modified, title: title,
                                   scannedBytes: cursor.readOffset, prefixDigest: Data(SHA256.hash(data: prefix)))
        cache.withLock {
            if $0.count >= 1_024 { $0.removeAll(keepingCapacity: true) }
            $0[key] = resolved
        }
        return title
    }
}
