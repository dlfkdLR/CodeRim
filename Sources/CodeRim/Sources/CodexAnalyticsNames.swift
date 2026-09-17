import CryptoKit
import Foundation

/// Resolve display names in memory. The database keeps its existing opaque
/// project/session identities, token events and privacy boundary.
enum CodexAnalyticsNames {
    static func applying(_ threads: [CodexStore.Thread], to snapshot: AnalyticsSnapshot,
                         projectSessions: [String: Set<String>]) -> AnalyticsSnapshot {
        let bySession = Dictionary(threads.map { thread in
            (SHA256.hash(data: Data(thread.id.utf8)).map { String(format: "%02x", $0) }.joined(), thread)
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
}
