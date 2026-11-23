import Foundation

/// Resolve display names in memory. The database keeps its existing opaque
/// project/session identities, token events and privacy boundary.
enum CodexAnalyticsNames {
    static func applying(_ threads: [CodexStore.Thread], to snapshot: AnalyticsSnapshot,
                         projectSessions: [String: Set<String>]) -> AnalyticsSnapshot {
        let bySession = Dictionary(threads.map { thread in
            (UsageStorageIdentity.digest(thread.id), thread)
        }, uniquingKeysWith: { first, _ in first })
        var projectNames: [String: Set<String>] = [:]
        for (projectID, sessionIDs) in projectSessions {
            for sessionID in sessionIDs {
                if let thread = bySession[sessionID] {
                    projectNames[projectID, default: []].insert(thread.projectName)
                }
            }
        }
        let sessions = snapshot.sessions.map { session in
            guard let thread = bySession[session.id] else { return session }
            return SessionUsageSummary(
                id: session.id, projectID: session.projectID,
                projectName: thread.title ?? session.projectName,
                startedAt: session.startedAt, lastActivityAt: session.lastActivityAt,
                usage: session.usage, models: session.models,
                directSubagentCount: session.directSubagentCount,
                imageAttachmentCount: session.imageAttachmentCount, parentSessionID: session.parentSessionID)
        }
        let projects = snapshot.projects.map { project in
            guard CodexStore.Thread.isGeneric(project.name),
                  let names = projectNames[project.id], !names.isEmpty else { return project }
            let sorted = names.sorted()
            let label = sorted.prefix(2).joined(separator: " · ")
                + (sorted.count > 2 ? " (+\(sorted.count - 2))" : "")
            return ProjectUsageSummary(id: project.id, name: label, usage: project.usage,
                                       models: project.models, sessionCount: project.sessionCount)
        }
        return AnalyticsSnapshot(range: snapshot.range, interval: snapshot.interval,
                                 through: snapshot.through, usage: snapshot.usage, quality: snapshot.quality,
                                 buckets: snapshot.buckets, models: snapshot.models,
                                 projects: projects, sessions: sessions)
    }

    static func indexedChats(_ threads: [CodexStore.Thread], excluding sessions: [SessionUsageSummary],
                             cutoff: Date?, through end: Date) -> [IndexedChatUsage] {
        let detailedIDs = Set(sessions.map(\.id))
        return threads.compactMap { thread -> IndexedChatUsage? in
            guard let tokens = thread.indexedTokens, tokens > 0,
                  !FileManager.default.fileExists(atPath: thread.rollout.path) else { return nil }
            let id = UsageStorageIdentity.digest(thread.id)
            guard !detailedIDs.contains(id) else { return nil }
            // An index counter covers the entire chat. After Clear Local History,
            // it cannot be sliced at the cutoff, so omit any chat that began earlier.
            if let cutoff {
                guard let createdAt = thread.createdAt, createdAt > cutoff else { return nil }
            }
            if let updatedAt = thread.updatedAt, updatedAt > end { return nil }
            return IndexedChatUsage(id: id, title: thread.displayTitle,
                                    totalTokens: tokens, updatedAt: thread.updatedAt)
        }.sorted {
            if $0.updatedAt != $1.updatedAt {
                return ($0.updatedAt ?? .distantPast) > ($1.updatedAt ?? .distantPast)
            }
            return $0.id < $1.id
        }
    }
}
