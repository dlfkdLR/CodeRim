import Foundation

/// Reads GLM Coding Plan usage from Z.ai's own monitor endpoint, with a key one
/// of the coding tools already holds — see `GLMCredentials`. No such key, no
/// ring.
///
/// The endpoint is not a published API and is known to throttle, so every
/// failure degrades to a status the UI can render honestly, and a 429 backs off
/// on a schedule that outlives the process.
///
/// Ported from the MIT-licensed Codenotch (`GLMProvider`).
@MainActor
final class GLMNotchProvider: NotchProvider {
    let id = "glm"
    let displayName = "GLM"
    let glyph: ProviderGlyph = .glm
    var isVisibleWhenAbsent: Bool { false }

    private let session: URLSession
    private let archive: UsageArchive
    private var retryNoEarlierThan: Date?
    private var consecutiveRateLimits = 0
    private var lastKnownPlan: String?

    init(session: URLSession = .shared, archive: UsageArchive = UsageArchive()) {
        self.session = session
        self.archive = archive
        self.retryNoEarlierThan = archive.loadBackoffUntil(providerID: "glm")
    }

    var signInRoute: SignInRoute {
        .guidance("Usage rides on a Z.ai GLM Coding Plan key held by a coding tool — Claude Code's settings.json, ZCode or OpenCode. Set one up there and the notch reads it.")
    }

    func account() -> ProviderAccount? {
        guard let credentials = GLMCredentials.load() else { return nil }
        return ProviderAccount(
            label: nil,
            plan: lastKnownPlan,
            source: credentials.source,
            manageURL: credentials.baseURL.host == "open.bigmodel.cn"
                ? URL(string: "https://open.bigmodel.cn/usage")
                : URL(string: "https://z.ai/manage-apikey/apikey-list")
        )
    }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        if let retryNoEarlierThan, retryNoEarlierThan > Date() {
            throw NotchProviderError.rateLimited(retryAfter: retryNoEarlierThan.timeIntervalSinceNow)
        }

        // Ordinary files, not keychain items — reading them prompts nobody.
        guard let credentials = await Task.detached(operation: { GLMCredentials.load() }).value else {
            throw NotchProviderError.needsAuth
        }

        do {
            let data = try await fetch(credentials: credentials)
            let payload = try GLMUsage.parse(data)

            consecutiveRateLimits = 0
            retryNoEarlierThan = nil
            archive.saveBackoffUntil(nil, providerID: id)
            lastKnownPlan = payload.level

            return ProviderSnapshot(
                id: id, displayName: displayName, glyph: glyph,
                fidelity: .official, status: .ok,
                windows: payload.windows, headlineID: "session"
            )
        } catch NotchProviderError.rateLimited(let retryAfter) {
            consecutiveRateLimits += 1
            retryNoEarlierThan = Date().addingTimeInterval(retryAfter)
            archive.saveBackoffUntil(retryNoEarlierThan, providerID: id)
            NotchLog.usage.notice("glm: rate limited (\(self.consecutiveRateLimits, privacy: .public)x)")
            throw NotchProviderError.rateLimited(retryAfter: retryAfter)
        }
    }

    private func fetch(credentials: GLMCredentials.Credential) async throws -> Data {
        let url = credentials.baseURL.appendingPathComponent("api/monitor/usage/quota/limit")
        var request = URLRequest(url: url)
        // The monitor takes the key raw — no "Bearer" scheme.
        request.setValue(credentials.token, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 15

        let (data, response) = try await BoundedHTTP.data(for: request, on: session)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0

        if status == 401 || status == 403 { throw NotchProviderError.needsAuth }
        if status == 429 {
            throw NotchProviderError.rateLimited(
                retryAfter: Self.backoff(
                    forAttempt: consecutiveRateLimits,
                    retryAfter: Self.retryAfter(from: response)
                )
            )
        }
        guard (200..<300).contains(status) else {
            throw NotchProviderError.badResponse(status: status)
        }
        return data
    }

    /// A minute, doubling per consecutive limit, capped so it always recovers.
    /// The server's own hint only raises the floor.
    nonisolated static func backoff(forAttempt attempt: Int, retryAfter: TimeInterval?) -> TimeInterval {
        let floor: TimeInterval = 60
        let ceiling: TimeInterval = 15 * 60
        let doubled = floor * pow(2, Double(min(attempt, 4)))
        return min(ceiling, max(doubled, retryAfter ?? 0))
    }

    /// `Retry-After` is either a number of seconds or an HTTP date.
    nonisolated static func retryAfter(from response: URLResponse?) -> TimeInterval? {
        guard let header = (response as? HTTPURLResponse)?
            .value(forHTTPHeaderField: "Retry-After")?
            .trimmingCharacters(in: .whitespaces)
        else { return nil }

        if let seconds = TimeInterval(header) { return max(0, seconds) }

        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "GMT")
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        guard let date = formatter.date(from: header) else { return nil }
        return max(0, date.timeIntervalSinceNow)
    }
}
