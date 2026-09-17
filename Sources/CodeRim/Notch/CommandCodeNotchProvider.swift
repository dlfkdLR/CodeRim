import Foundation

/// Reads Command Code GOAT usage from the `/alpha` endpoints the desktop app
/// uses, with the key in `~/.commandcode/auth.json`. No key, no ring.
///
/// Ported from the MIT-licensed Codenotch (`CommandCodeProvider`).
@MainActor
final class CommandCodeNotchProvider: NotchProvider {
    let id = "commandcode"
    let displayName = "Command Code"
    let glyph: ProviderGlyph = .commandcode
    var isVisibleWhenAbsent: Bool { false }

    private let session: URLSession
    private let archive: UsageArchive
    private let authURL: URL
    private var retryNoEarlierThan: Date?
    private var consecutiveRateLimits = 0
    private var lastKnownPlan: String?
    private var lastKnownUser: String?

    init(session: URLSession = .shared,
         archive: UsageArchive = UsageArchive(),
         authURL: URL = CommandCodeCredentials.authURL) {
        self.session = session
        self.archive = archive
        self.authURL = authURL
        self.retryNoEarlierThan = archive.loadBackoffUntil(providerID: "commandcode")
    }

    var signInRoute: SignInRoute {
        .guidance("Sign in with the Command Code app — it writes ~/.commandcode/auth.json and the notch reads it.")
    }

    func account() -> ProviderAccount? {
        guard let base = CommandCodeCredentials.account(from: authURL) else { return nil }
        return ProviderAccount(
            label: lastKnownUser ?? base.label,
            plan: lastKnownPlan ?? base.plan,
            source: base.source,
            manageURL: base.manageURL
        )
    }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        if let retryNoEarlierThan, retryNoEarlierThan > Date() {
            throw NotchProviderError.rateLimited(retryAfter: retryNoEarlierThan.timeIntervalSinceNow)
        }

        let authURL = self.authURL
        guard let credentials = try? await Task.detached(operation: {
            try CommandCodeCredentials.load(from: authURL)
        }).value else {
            throw NotchProviderError.needsAuth
        }

        do {
            lastKnownUser = credentials.userName
            let whoami = try await body(from: CommandCodeUsage.whoami, token: credentials.apiKey)
            let orgId = CommandCodeUsage.orgId(whoamiJSON: whoami)

            async let creditsTask = body(from: CommandCodeUsage.credits.withQuery(["orgId": orgId]),
                                         token: credentials.apiKey)
            async let subsTask = body(from: CommandCodeUsage.subscriptions.withQuery(["orgId": orgId]),
                                      token: credentials.apiKey)
            let credits = try await creditsTask
            let subscription = try await subsTask

            let subObject = (try? JSONSerialization.jsonObject(with: Data(subscription.utf8))) as? [String: Any]
            let subData = (subObject?["data"] as? [String: Any]) ?? subObject ?? [:]
            lastKnownPlan = CommandCodeUsage.planName(subData["planId"] as? String)

            var summaryQuery: [String: String?] = ["orgId": orgId]
            if let start = CommandCodeUsage.date(subData["currentPeriodStart"]) {
                let stamp = ISO8601DateFormatter()
                stamp.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                summaryQuery["since"] = stamp.string(from: start)
            }
            let summary = try await body(from: CommandCodeUsage.summary.withQuery(summaryQuery),
                                         token: credentials.apiKey)

            let windows = try CommandCodeUsage.windows(
                summaryJSON: summary, creditsJSON: credits, subscriptionJSON: subscription
            )

            consecutiveRateLimits = 0
            retryNoEarlierThan = nil
            archive.saveBackoffUntil(nil, providerID: id)

            return ProviderSnapshot(
                id: id, displayName: displayName, glyph: glyph,
                fidelity: .official, status: .ok,
                windows: windows, headlineID: "monthly"
            )
        } catch NotchProviderError.rateLimited(let retryAfter) {
            consecutiveRateLimits += 1
            retryNoEarlierThan = Date().addingTimeInterval(retryAfter)
            archive.saveBackoffUntil(retryNoEarlierThan, providerID: id)
            NotchLog.usage.notice("commandcode: rate limited (\(self.consecutiveRateLimits, privacy: .public)x)")
            throw NotchProviderError.rateLimited(retryAfter: retryAfter)
        }
    }

    private func body(from url: URL, token: String) async throws -> String {
        var request = URLRequest(url: url)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("command-code-desktop", forHTTPHeaderField: "User-Agent")
        request.setValue("desktop", forHTTPHeaderField: "x-command-code-version")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 15

        let (data, response) = try await BoundedHTTP.data(for: request, on: session)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 401 || status == 403 { throw NotchProviderError.needsAuth }
        if status == 429 { throw NotchProviderError.rateLimited(retryAfter: 60) }
        guard (200..<300).contains(status),
              let text = String(data: data, encoding: .utf8)
        else { throw NotchProviderError.badResponse(status: status) }
        return text
    }
}

private extension URL {
    func withQuery(_ items: [String: String?]) -> URL {
        guard var components = URLComponents(url: self, resolvingAgainstBaseURL: false) else {
            return self
        }
        let pairs = items.compactMap { key, value -> URLQueryItem? in
            guard let value, !value.isEmpty else { return nil }
            return URLQueryItem(name: key, value: value)
        }
        if !pairs.isEmpty { components.queryItems = pairs }
        return components.url ?? self
    }
}
