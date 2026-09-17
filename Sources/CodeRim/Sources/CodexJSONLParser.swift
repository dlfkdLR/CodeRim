import CoreFoundation
import Foundation

enum CodexParsedLine: Equatable, Sendable {
    case sessionMetadata(SessionMetadata)
    case taskStarted(TaskStartedMetadata)
    case turnContext(TurnContextMetadata)
    case imageAttachments(ImageAttachmentMetadata)
    case token(CodexTokenObservation)
    case ignored
    case malformed(String)
}

struct CodexTokenObservation: Equatable, Sendable {
    let occurredAt: Date
    let ordinal: Int64?
    let lastUsage: TokenUsage?
    let cumulativeUsage: TokenUsage?
}

struct TurnContextMetadata: Equatable, Sendable {
    let model: String?
    let workingDirectory: String?
}

struct TaskStartedMetadata: Equatable, Sendable {
    let occurredAt: Date
    let startedAt: Date?
    let ordinal: Int64?
}

struct ImageAttachmentMetadata: Equatable, Sendable {
    let occurredAt: Date
    let count: Int
}

struct CodexJSONLParser: Sendable {
    static let maximumLineBytes = 1_048_576
    static let maximumTokenComponent: Int64 = 1_000_000_000_000
    private static let maximumIdentifierLength = 256
    private static let maximumModelLength = 256
    private static let maximumPathLength = 4_096

    func parse(_ line: Data) -> CodexParsedLine {
        guard !line.isEmpty else { return .ignored }
        guard line.count <= Self.maximumLineBytes else {
            return .malformed("line exceeds the 1 MiB safety limit")
        }

        let object: Any
        do {
            object = try JSONSerialization.jsonObject(with: line)
        } catch {
            return .malformed("invalid JSON")
        }

        guard let root = object as? [String: Any], let type = root["type"] as? String else {
            return .malformed("missing event type")
        }

        switch type {
        case "session_meta":
            return parseSessionMetadata(root)
        case "turn_context":
            return parseTurnContext(root)
        case "event_msg":
            return parseEventMessage(root)
        case "response_item":
            return parseResponseItem(root)
        default:
            return .ignored
        }
    }

    private func parseResponseItem(_ root: [String: Any]) -> CodexParsedLine {
        guard let payload = root["payload"] as? [String: Any],
              payload["type"] as? String == "message",
              payload["role"] as? String == "user",
              let content = payload["content"] as? [[String: Any]]
        else { return .ignored }

        let imageCount = content.reduce(into: 0) { count, item in
            if item["type"] as? String == "input_image" {
                count += 1
            }
        }
        guard imageCount > 0 else { return .ignored }
        guard let timestamp = root["timestamp"] as? String,
              let occurredAt = parseTimestamp(timestamp) else {
            return .malformed("image metadata is missing a valid timestamp")
        }
        return .imageAttachments(ImageAttachmentMetadata(occurredAt: occurredAt, count: imageCount))
    }

    private func parseTurnContext(_ root: [String: Any]) -> CodexParsedLine {
        guard let payload = root["payload"] as? [String: Any] else {
            return .malformed("turn context is missing payload")
        }
        return .turnContext(
            TurnContextMetadata(
                model: sanitized(payload["model"], maximumLength: Self.maximumModelLength),
                workingDirectory: sanitized(payload["cwd"], maximumLength: Self.maximumPathLength)
            )
        )
    }

    private func parseSessionMetadata(_ root: [String: Any]) -> CodexParsedLine {
        guard let payload = root["payload"] as? [String: Any] else {
            return .malformed("session metadata is missing payload")
        }

        return .sessionMetadata(
            SessionMetadata(
                id: sanitized(payload["id"], maximumLength: Self.maximumIdentifierLength),
                model: sanitized(payload["model"], maximumLength: Self.maximumModelLength),
                workingDirectory: sanitized(payload["cwd"], maximumLength: Self.maximumPathLength),
                forkedFromID: sanitized(payload["forked_from_id"], maximumLength: Self.maximumIdentifierLength),
                parentThreadID: sanitized(payload["parent_thread_id"], maximumLength: Self.maximumIdentifierLength),
                subagentHistoryStartOrdinal: integer(payload["subagent_history_start_ordinal"]),
                occurredAt: (root["timestamp"] as? String).flatMap(parseTimestamp)
            )
        )
    }

    private func parseEventMessage(_ root: [String: Any]) -> CodexParsedLine {
        guard let payload = root["payload"] as? [String: Any],
              let eventType = payload["type"] as? String else {
            return .ignored
        }

        if eventType == "task_started" {
            guard let timestamp = root["timestamp"] as? String,
                  let occurredAt = parseTimestamp(timestamp) else {
                return .malformed("task start is missing a valid timestamp")
            }
            return .taskStarted(
                TaskStartedMetadata(
                    occurredAt: occurredAt,
                    startedAt: timeInterval(payload["started_at"]).map(Date.init(timeIntervalSince1970:)),
                    ordinal: integer(root["ordinal"])
                )
            )
        }

        guard eventType == "token_count" else { return .ignored }

        guard
            let timestamp = root["timestamp"] as? String,
            let occurredAt = parseTimestamp(timestamp)
        else {
            return .malformed("token event is missing a valid timestamp")
        }

        guard let info = payload["info"] as? [String: Any] else {
            return .malformed("token event is missing usage info")
        }

        let lastUsage = parseUsage(info["last_token_usage"])
        let cumulativeUsage = parseUsage(info["total_token_usage"])
        guard lastUsage != nil || cumulativeUsage != nil else {
            return .malformed("token event has no supported usage object")
        }

        return .token(
            CodexTokenObservation(
                occurredAt: occurredAt,
                ordinal: integer(root["ordinal"]),
                lastUsage: lastUsage,
                cumulativeUsage: cumulativeUsage
            )
        )
    }

    private func parseUsage(_ value: Any?) -> TokenUsage? {
        guard let dictionary = value as? [String: Any] else { return nil }
        guard let input = integer(dictionary["input_tokens"]),
              let cached = integer(dictionary["cached_input_tokens"]),
              let output = integer(dictionary["output_tokens"])
        else { return nil }

        let cacheWrite: Int64?
        if dictionary.keys.contains("cache_write_input_tokens") {
            guard let parsed = integer(dictionary["cache_write_input_tokens"]) else { return nil }
            cacheWrite = parsed
        } else {
            cacheWrite = nil
        }

        let usage = TokenUsage(
            inputTokens: input,
            cachedInputTokens: cached,
            cacheWriteInputTokens: cacheWrite,
            outputTokens: output
        )
        return usage.isValid ? usage : nil
    }

    private func integer(_ value: Any?) -> Int64? {
        switch value {
        case let number as NSNumber:
            guard CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
            guard !CFNumberIsFloatType(number) else { return nil }
            let result = number.int64Value
            return (0...Self.maximumTokenComponent).contains(result) ? result : nil
        case let string as String:
            guard string.count <= 16 else { return nil }
            return Int64(string).flatMap {
                (0...Self.maximumTokenComponent).contains($0) ? $0 : nil
            }
        default:
            return nil
        }
    }

    private func timeInterval(_ value: Any?) -> TimeInterval? {
        guard let number = value as? NSNumber,
              CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let seconds = number.doubleValue
        guard seconds.isFinite, seconds >= 0 else { return nil }
        return seconds
    }

    private func sanitized(_ value: Any?, maximumLength: Int) -> String? {
        guard let value = value as? String,
              !value.isEmpty,
              value.count <= maximumLength,
              !value.contains("\0")
        else { return nil }
        return value
    }

    private func parseTimestamp(_ value: String) -> Date? {
        let fractional = Date.ISO8601FormatStyle(includingFractionalSeconds: true)
        if let date = try? fractional.parse(value) {
            return date
        }
        return try? Date.ISO8601FormatStyle().parse(value)
    }
}
