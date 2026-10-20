import Foundation
import CodexBarCore

/// Keep password bytes intact and retain each authentication stage's failure type.
enum StepFunPasswordRecovery {
    typealias Upstream = @Sendable (ProviderDescriptor, ProviderFetchContext) async throws -> ProviderFetchResult
    @TaskLocal static var cacheCommit: (@Sendable (String) async -> Void)?

    static func fetch(_ descriptor: ProviderDescriptor, context: ProviderFetchContext,
                      upstream: Upstream = { try await $0.fetch(context: $1) },
                      cached: @Sendable () -> String? = { CookieHeaderCache.load(provider: .stepfun)?.cookieHeader },
                      login: @Sendable (String, String) async throws -> String = { try await StepFunUsageFetcher.login(username: $0, password: $1) },
                      refresh: @Sendable (String) async throws -> String = { try await StepFunUsageFetcher.refreshToken(token: $0) },
                      usage: @Sendable (String) async throws -> StepFunUsageSnapshot = { try await StepFunUsageFetcher.fetchUsage(token: $0) },
                      cache: @Sendable (String) async -> Void = { await cacheCommit?($0) }) async throws -> ProviderFetchResult {
        guard (context.settings?.stepfun?.cookieSource ?? .auto) == .auto,
              context.sourceMode == .auto || context.sourceMode == .web else {
            return try await upstream(descriptor, context)
        }
        try Task.checkCancellation()
        let username = StepFunSettingsReader.username(environment: context.env)
        let password = context.env["STEPFUN_PASSWORD"]
        var lastAuthenticationError: Error?
        let cachedToken = cached().flatMap { $0.isEmpty ? nil : $0 }
        let environmentToken = StepFunSettingsReader.token(environment: context.env)
        var candidates: [String] = []
        for value in [cachedToken, environmentToken].compactMap({ $0 }) where !candidates.contains(value) {
            candidates.append(value)
        }
        for selected in candidates {
            do {
                let snapshot = try await usage(selected)
                try Task.checkCancellation()
                if cachedToken != nil, selected != cachedToken { await cache(selected) }
                return result(snapshot)
            } catch {
                try Task.checkCancellation()
                guard authenticationFailure(error) else { throw error }
                lastAuthenticationError = error
            }
            let renewed: String
            do { renewed = try await refresh(selected) }
            catch {
                try Task.checkCancellation()
                guard authenticationFailure(error) else { throw error }
                lastAuthenticationError = error
                continue
            }
            do {
                let snapshot = try await usage(renewed)
                try Task.checkCancellation()
                await cache(renewed)
                return result(snapshot)
            } catch {
                try Task.checkCancellation()
                guard authenticationFailure(error) else { throw error }
                lastAuthenticationError = error
            }
        }
        guard let username, let password, !password.isEmpty else {
            throw lastAuthenticationError ?? StepFunUsageError.missingCredentials
        }
        let token = try await login(username, password)
        try Task.checkCancellation()
        let snapshot = try await usage(token)
        try Task.checkCancellation()
        await cache(token)
        return result(snapshot)
    }
    private static func result(_ snapshot: StepFunUsageSnapshot) -> ProviderFetchResult {
        ProviderFetchResult(usage: snapshot.toUsageSnapshot(), credits: nil, dashboard: nil,
            sourceLabel: "web", strategyID: "stepfun.web", strategyKind: .web)
    }
    static func authenticationFailure(_ error: Error) -> Bool {
        let message: String
        switch error {
        case let StepFunUsageError.apiError(value), let StepFunUsageError.tokenRefreshFailed(value): message = value
        default: return false
        }
        let text = message.lowercased()
        return text.contains("http 401") || text.contains("http 403") || text.contains("unauthorized")
            || text.contains("unauthenticated") || text.contains("invalid credentials") || text.contains("invalid token")
            || text.contains("token expired") || text.contains("expired token")
    }
}
