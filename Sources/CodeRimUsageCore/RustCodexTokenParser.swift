#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
import Foundation

enum RustCodexTokenParser {
    /// Unrecognized input delegates the entire line to the shipping parser.
    static func parse(_ line: Data, parseTimestamp: (String) -> Date?) -> CodexParsedLine? {
        line.withUnsafeBytes { buffer in
            let bytes = buffer.bindMemory(to: UInt8.self)
            let event = cr_parse_codex_event(bytes.baseAddress, bytes.count)
            func string(_ span: cr_json_span) -> String? {
                guard span.offset >= 0, span.length > 0, span.offset <= bytes.count,
                      span.length <= bytes.count - span.offset else { return nil }
                return String(decoding: bytes[span.offset..<(span.offset + span.length)], as: UTF8.self)
            }
            let metadata = event.metadata
            switch Int(event.kind) {
            case CR_EVENT_TOKEN:
                let token = event.token
                guard token.timestamp_length > 0, token.timestamp_length <= 64,
                      let timestamp = string(cr_json_span(offset: token.timestamp_offset, length: token.timestamp_length))
                else { return nil }
                guard let date = parseTimestamp(timestamp) else {
                    return .malformed("token event is missing a valid timestamp")
                }
                return .token(CodexTokenObservation(
                    occurredAt: date, ordinal: token.has_ordinal == 0 ? nil : token.ordinal,
                    lastUsage: token.has_last == 0 ? nil : unpack(token.last),
                    cumulativeUsage: token.has_cumulative == 0 ? nil : unpack(token.cumulative)
                ))
            case CR_EVENT_SESSION:
                return .sessionMetadata(SessionMetadata(
                    id: string(metadata.id), model: string(metadata.model), workingDirectory: string(metadata.cwd),
                    forkedFromID: string(metadata.fork_id), parentThreadID: string(metadata.parent),
                    subagentHistoryStartOrdinal: metadata.has_ordinal == 0 ? nil : metadata.ordinal,
                    occurredAt: string(metadata.timestamp).flatMap(parseTimestamp)
                ))
            case CR_EVENT_CONTEXT:
                return .turnContext(TurnContextMetadata(model: string(metadata.model), workingDirectory: string(metadata.cwd)))
            case CR_EVENT_TASK:
                guard let date = string(metadata.timestamp).flatMap(parseTimestamp) else {
                    return .malformed("task start is missing a valid timestamp")
                }
                return .taskStarted(TaskStartedMetadata(
                    occurredAt: date,
                    startedAt: metadata.has_started_at == 0 ? nil : Date(timeIntervalSince1970: Double(metadata.started_at)),
                    ordinal: metadata.has_ordinal == 0 ? nil : metadata.ordinal
                ))
            case CR_EVENT_IGNORED:
                return .ignored
            default:
                return nil
            }
        }
    }

    private static func unpack(_ raw: cr_usage) -> TokenUsage {
        TokenUsage(inputTokens: raw.input, cachedInputTokens: raw.cached,
                   cacheWriteInputTokens: raw.has_written == 0 ? nil : raw.written,
                   outputTokens: raw.output)
    }
}
#endif
