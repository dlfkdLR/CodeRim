import Foundation

struct ClaudeUsageObservation: Sendable {
    let messageID: String
    let sessionID: String
    let agentID: String?
    let occurredAt: Date
    let model: String
    let workingDirectory: String?
    let usage: TokenUsage
}

/// Projects only accounting fields. Message content and credentials are never decoded.
struct ClaudeJSONLParser: Sendable {
    private struct Row: Decodable {
        let type: String
        let timestamp: String
        let sessionId: String
        let agentId: String?
        let cwd: String?
        let isApiErrorMessage: Bool?
        let message: Message
    }

    private struct Message: Decodable {
        let id: String
        let model: String
        let role: String
        let usage: Usage
    }

    private struct Usage: Decodable {
        let input_tokens: Int64
        let cache_read_input_tokens: Int64?
        let cache_creation_input_tokens: Int64?
        let output_tokens: Int64
    }

    func parse(_ line: Data) -> ClaudeUsageObservation? {
        guard line.count <= CodexJSONLParser.maximumLineBytes,
              let row = try? JSONDecoder().decode(Row.self, from: line),
              row.type == "assistant", row.message.role == "assistant",
              row.isApiErrorMessage != true,
              Self.validIdentifier(row.message.id), Self.validIdentifier(row.sessionId),
              Self.validIdentifier(row.message.model), row.message.model.hasPrefix("claude-"),
              let timestamp = Self.date(row.timestamp)
        else { return nil }

        let raw = row.message.usage
        let read = raw.cache_read_input_tokens ?? 0
        let write = raw.cache_creation_input_tokens ?? 0
        guard [raw.input_tokens, read, write, raw.output_tokens].allSatisfy({
            (0...1_000_000_000_000).contains($0)
        }) else { return nil }

        // Unlike Codex, Claude's input_tokens excludes both cache reads and writes.
        // Nested TTL, iteration and thinking fields are breakdowns, not extra tokens.
        let usage = TokenUsage(
            inputTokens: raw.input_tokens + read + write,
            cachedInputTokens: read,
            cacheWriteInputTokens: write,
            outputTokens: raw.output_tokens
        )
        return ClaudeUsageObservation(
            messageID: row.message.id,
            sessionID: row.sessionId,
            agentID: row.agentId.flatMap { Self.validIdentifier($0) ? $0 : nil },
            occurredAt: timestamp,
            model: row.message.model.lowercased(),
            workingDirectory: row.cwd,
            usage: usage
        )
    }

    private static func validIdentifier(_ value: String) -> Bool {
        !value.isEmpty && value.utf8.count <= 128 && value.utf8.allSatisfy {
            (48...57).contains($0) || (65...90).contains($0)
                || (97...122).contains($0) || [45, 46, 95].contains($0)
        }
    }

    private static func date(_ value: String) -> Date? {
        guard value.utf8.count <= 64 else { return nil }
        return (try? Date(value, strategy: .iso8601.time(includingFractionalSeconds: true)))
            ?? (try? Date(value, strategy: .iso8601))
    }
}
