#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
import Foundation

enum RustCodexTokenParser {
    /// A nil result delegates the *entire* line to the shipping parser.
    static func parse(_ line: Data, parseTimestamp: (String) -> Date?) -> CodexParsedLine? {
        line.withUnsafeBytes { buffer in
            let bytes = buffer.bindMemory(to: UInt8.self)
            let token = cr_parse_token_json(bytes.baseAddress, bytes.count)
            // Check ranges before slicing. The borrowed pointer never leaves this closure.
            guard token.timestamp_length > 0, token.timestamp_length <= 64,
                  token.timestamp_offset >= 0,
                  token.timestamp_offset <= bytes.count - token.timestamp_length else { return nil }
            let end = token.timestamp_offset + token.timestamp_length
            let timestamp = String(decoding: bytes[token.timestamp_offset..<end], as: UTF8.self)
            guard let date = parseTimestamp(timestamp) else {
                return .malformed("token event is missing a valid timestamp")
            }
            return .token(CodexTokenObservation(
                occurredAt: date, ordinal: token.has_ordinal == 0 ? nil : token.ordinal,
                lastUsage: token.has_last == 0 ? nil : unpack(token.last),
                cumulativeUsage: token.has_cumulative == 0 ? nil : unpack(token.cumulative)
            ))
        }
    }

    private static func unpack(_ raw: cr_usage) -> TokenUsage {
        TokenUsage(inputTokens: raw.input, cachedInputTokens: raw.cached,
                   cacheWriteInputTokens: raw.has_written == 0 ? nil : raw.written,
                   outputTokens: raw.output)
    }
}
#endif
