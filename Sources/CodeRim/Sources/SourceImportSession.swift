import CodeRimUsageCore
import Foundation

/// One source's import state. Persistence is supplied only as a counter-state reader.
struct SourceImportSession: Sendable {
    var checkpoint: SourceCheckpoint
    var metadata: SessionMetadata
    var normalizationState: UsageNormalizationState
    var persistedNormalizationState: UsageNormalizationState
    var loadedStateSessionID: String?
    var pendingEvents: [UsageEvent] = []
    var pendingEvidence: [PartialEvidence] = []
    var excludedEventKeys: Set<String> = []
    private let provider: UsageProvider
    private let sourceURL: URL
    private let importCutoff: Date?
    private let projectKey: Data
    private let loadState: @Sendable (String) async throws -> UsageNormalizationState
    private let parser = CodexJSONLParser()
    private let normalizer = UsageNormalizer()

    init(checkpoint: SourceCheckpoint, state: UsageNormalizationState, provider: UsageProvider,
         sourceURL: URL, importCutoff: Date?, projectKey: Data,
         loadState: @escaping @Sendable (String) async throws -> UsageNormalizationState) {
        self.checkpoint = checkpoint
        metadata = SessionMetadata(id: checkpoint.sessionID, model: checkpoint.model, workingDirectory: nil,
            forkedFromID: checkpoint.inheritsHistory ? "inherited" : nil, parentThreadID: checkpoint.parentSessionID,
            subagentHistoryStartOrdinal: checkpoint.inheritedHistoryEndOrdinal, occurredAt: checkpoint.sessionStartedAt)
        normalizationState = state; persistedNormalizationState = state; loadedStateSessionID = checkpoint.sessionID
        self.provider = provider; self.sourceURL = sourceURL; self.importCutoff = importCutoff
        self.projectKey = projectKey; self.loadState = loadState
        pendingEvents.reserveCapacity(256)
    }

    mutating func consume(_ line: Data, position: Int64,
                          isolation: isolated (any Actor)? = #isolation) async throws {
        if provider == .claude {
            processClaudeLine(line, position: position)
            return
        }
        guard line.containsASCII("\"token_count\"")
                || line.containsASCII("\"session_meta\"")
                || line.containsASCII("\"task_started\"")
                || line.containsASCII("\"turn_context\"")
                || (line.containsASCII("\"response_item\"") && line.containsASCII("\"input_image\""))
        else { return }

        switch parser.parse(line) {
        case let .sessionMetadata(parsed):
            guard checkpoint.committedOffset == 0 else {
                if checkpoint.inheritsHistory {
                    checkpoint.historyReplayComplete = false
                }
                return
            }
            let resolvedID = parsed.id.map(UsageStorageIdentity.digest) ?? checkpoint.sessionID
            let resolvedModel = canonicalModelID(parsed.model)
            let project = UsageStorageIdentity.project(parsed.workingDirectory, keyData: projectKey)
            let parentSessionID = parsed.parentThreadID.map(UsageStorageIdentity.digest)
            metadata = SessionMetadata(
                id: resolvedID,
                model: resolvedModel,
                workingDirectory: nil,
                forkedFromID: parsed.forkedFromID,
                parentThreadID: parentSessionID,
                subagentHistoryStartOrdinal: parsed.subagentHistoryStartOrdinal,
                occurredAt: parsed.occurredAt
            )
            checkpoint.sessionID = resolvedID
            checkpoint.inheritsHistory = parsed.inheritsHistory
            checkpoint.sessionStartedAt = parsed.occurredAt
            checkpoint.inheritedHistoryEndOrdinal = parsed.subagentHistoryStartOrdinal
            checkpoint.historyReplayComplete = !parsed.inheritsHistory
            checkpoint.model = resolvedModel
            checkpoint.projectPath = project?.id
            checkpoint.projectName = project?.name
            checkpoint.parentSessionID = parentSessionID
            if resolvedID != loadedStateSessionID, let resolvedID {
                normalizationState = try await loadState(resolvedID)
                persistedNormalizationState = normalizationState
                loadedStateSessionID = resolvedID
            }

        case let .taskStarted(task):
            guard checkpoint.inheritsHistory, !checkpoint.historyReplayComplete else { return }
            if let inheritedHistoryEndOrdinal = checkpoint.inheritedHistoryEndOrdinal,
               let ordinal = task.ordinal,
               ordinal > inheritedHistoryEndOrdinal {
                checkpoint.historyReplayComplete = true
                return
            }
            if checkpoint.inheritedHistoryEndOrdinal == nil,
               let sessionStartedAt = checkpoint.sessionStartedAt,
               let taskStartedAt = task.startedAt,
               taskStartedAt.timeIntervalSince1970.rounded(.down)
                   >= sessionStartedAt.timeIntervalSince1970.rounded(.down) {
                checkpoint.historyReplayComplete = true
            }

        case let .turnContext(context):
            if let model = canonicalModelID(context.model) {
                checkpoint.model = model
                metadata = SessionMetadata(
                    id: metadata.id,
                    model: model,
                    workingDirectory: nil,
                    forkedFromID: metadata.forkedFromID,
                    parentThreadID: metadata.parentThreadID,
                    subagentHistoryStartOrdinal: metadata.subagentHistoryStartOrdinal,
                    occurredAt: metadata.occurredAt
                )
            }
            if let project = UsageStorageIdentity.project(context.workingDirectory, keyData: projectKey) {
                checkpoint.projectPath = project.id
                checkpoint.projectName = project.name
            }

        case let .imageAttachments(attachment):
            if checkpoint.inheritsHistory, !checkpoint.historyReplayComplete,
               let boundary = checkpoint.inheritedHistoryEndOrdinal,
               let ordinal = attachment.ordinal, ordinal > boundary {
                checkpoint.historyReplayComplete = true
            }
            guard !checkpoint.inheritsHistory || checkpoint.historyReplayComplete else { return }
            if let importCutoff, attachment.occurredAt <= importCutoff { return }
            let result = checkpoint.imageAttachmentCount.addingReportingOverflow(Int64(attachment.count))
            checkpoint.imageAttachmentCount = result.overflow ? Int64.max : result.partialValue

        case let .token(observation):
            let isInheritedReplay: Bool
            if checkpoint.inheritsHistory, !checkpoint.historyReplayComplete {
                if let inheritedHistoryEndOrdinal = checkpoint.inheritedHistoryEndOrdinal,
                   let ordinal = observation.ordinal,
                   ordinal > inheritedHistoryEndOrdinal {
                    checkpoint.historyReplayComplete = true
                    isInheritedReplay = false
                } else {
                    isInheritedReplay = true
                }
            } else {
                isInheritedReplay = false
            }
            let result = normalizer.normalize(observation, state: normalizationState)
            normalizationState = result.state
            guard !isInheritedReplay else { return }
            guard let delta = result.delta, !delta.isZero else { return }
            if let importCutoff, observation.occurredAt <= importCutoff { return }
            let sessionID = checkpoint.sessionID
            pendingEvents.append(
                UsageEvent(
                    eventKey: eventKey(
                        sessionID: sessionID,
                        observation: observation
                    ),
                    occurredAt: observation.occurredAt,
                    sessionID: sessionID,
                    model: checkpoint.model,
                    projectPath: checkpoint.projectPath,
                    usage: delta,
                    sourcePath: UsageStorageIdentity.digest(sourceURL.standardizedFileURL.path),
                    sourcePosition: position,
                    pricingContext: pricingContext(observation: observation, delta: delta)
                )
            )

        case .malformed:
            normalizationState.quality = .partial
            // The session's quality alone dates the gap at its last valid row, which a recent period does not
            // reach; the row's own time (or when it was read) does.
            recordEvidence(position: position, occurredAt: ClaudeJSONLParser.timestamp(of: line))

        case .ignored:
            break
        }
    }

    mutating func recordEvidence(position: Int64, occurredAt: Date?) {
        let observedAt = occurredAt ?? Date()
        if let importCutoff, observedAt <= importCutoff { return }
        let generation = "\(checkpoint.sourcePath)|\(checkpoint.fileIdentity)|\(checkpoint.generation)"
        pendingEvidence.append(PartialEvidence(source: UsageStorageIdentity.digest(generation), position: position, observedAt: observedAt))
    }

    private func eventKey(
        sessionID: String?,
        observation: CodexTokenObservation
    ) -> String {
        let material = [
            "event-v2",
            "session:\(sessionID ?? "unknown")",
            "time:\(observation.occurredAt.timeIntervalSinceReferenceDate.bitPattern)",
            "ordinal:\(observation.ordinal.map(String.init) ?? "none")",
            "last:\(usageIdentity(observation.lastUsage))",
            "cumulative:\(usageIdentity(observation.cumulativeUsage))"
        ].joined(separator: "|")
        return UsageStorageIdentity.digest(Data(material.utf8))
    }

    private mutating func processClaudeLine(_ line: Data, position: Int64) {
        guard let observation = ClaudeJSONLParser().parse(line) else {
            // Most lines are not accounting rows. One that is but failed validation leaves tokens uncounted.
            let rejected = ClaudeJSONLParser.rejectedAccounting(line)
            if rejected.isAccounting {
                recordEvidence(position: position, occurredAt: rejected.occurredAt)
            }
            return
        }
        let eventKey = UsageStorageIdentity.digest("claude-message|\(observation.messageID)")
        if let importCutoff, observation.occurredAt <= importCutoff {
            excludedEventKeys.insert(eventKey)
            return
        }
        let parentID = UsageStorageIdentity.digest("claude-session|\(observation.sessionID)")
        // Subagent transcripts share the parent sessionId but own their messages.
        let filename = sourceURL.deletingPathExtension().lastPathComponent
        let agentID = observation.agentID.map { $0.hasPrefix("agent-") ? $0 : "agent-\($0)" }
            ?? (filename.hasPrefix("agent-") ? filename : nil)
        let sessionID = agentID.map {
            UsageStorageIdentity.digest("claude-agent|\(observation.sessionID)|\($0)")
        } ?? parentID
        let project = UsageStorageIdentity.project(observation.workingDirectory, keyData: projectKey)
        checkpoint.sessionID = sessionID
        checkpoint.parentSessionID = agentID == nil ? nil : parentID
        checkpoint.sessionStartedAt = min(checkpoint.sessionStartedAt ?? observation.occurredAt,
                                          observation.occurredAt)
        checkpoint.model = observation.model
        checkpoint.projectPath = project?.id ?? checkpoint.projectPath
        checkpoint.projectName = project?.name ?? checkpoint.projectName
        pendingEvents.append(UsageEvent(
            // A response may be repeated for text/tool blocks and in copied history.
            // Its message ID, not a line UUID, file path or timestamp, is the identity.
            eventKey: eventKey,
            occurredAt: observation.occurredAt,
            sessionID: sessionID,
            model: observation.model,
            projectPath: checkpoint.projectPath,
            usage: observation.usage,
            sourcePath: UsageStorageIdentity.digest(sourceURL.standardizedFileURL.path),
            sourcePosition: position,
            importParentSessionID: agentID == nil ? nil : parentID
        ))
    }

    private func usageIdentity(_ usage: TokenUsage?) -> String {
        guard let usage else { return "none" }
        // Keep the v2 identity compatible with Phase 1 databases. Cache-write
        // tokens are enrichment metadata for the same source observation, not a
        // reason to create a second accounting event during the v15 backfill.
        return "\(usage.inputTokens),\(usage.cachedInputTokens),\(usage.outputTokens)"
    }

    private func pricingContext(
        observation: CodexTokenObservation,
        delta: TokenUsage
    ) -> PricingContext? {
        guard delta.inputTokens > PricingContext.highContextInputThreshold else { return .standard }
        guard observation.lastUsage == delta else { return nil }
        return .highContext
    }

    private func canonicalModelID(_ value: String?) -> String? {
        guard let value else { return nil }
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !normalized.isEmpty,
              normalized.utf8.count <= 128,
              normalized.unicodeScalars.allSatisfy({
                  CharacterSet.alphanumerics.contains($0) || ".-_".unicodeScalars.contains($0)
              })
        else { return nil }
        return normalized
    }

}

private extension Data {
    func containsASCII(_ string: String) -> Bool { range(of: Data(string.utf8)) != nil }
}
