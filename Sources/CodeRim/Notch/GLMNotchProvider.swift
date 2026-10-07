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
    private var backoff: AccountBackoff
    private var lastKnownPlan: String?

    init(session: URLSession = ProviderSession.shared, archive: UsageArchive = UsageArchive()) {
        self.session = session
        self.archive = archive
        self.backoff = AccountBackoff(providerID: "glm", archive: archive)
    }

    var signInRoute: SignInRoute {
        .guided(.init(name: "Z.ai", action: .browser(URL(string: "https://z.ai/manage-apikey/apikey-list")!),
            note: "Create a GLM Coding Plan key on Z.ai and add it to Claude Code's settings.json, ZCode or OpenCode. CodeRim detects it there."))
    }

    func accountIdentity() -> String? { GLMCredentials.load()?.identity }

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
        // Ordinary files, not keychain items — reading them prompts nobody.
        guard let credentials = await Task.detached(operation: { GLMCredentials.load() }).value else {
            throw NotchProviderError.needsAuth
        }
        // The wait belongs to the account that earned it; a different key is read straight away.
        let account = credentials.identity
        if let wait = backoff.remainingWait(for: account), wait > 0 {
            throw NotchProviderError.rateLimited(retryAfter: wait)
        }

        do {
            let data = try await fetch(credentials: credentials)
            let payload = try GLMUsage.parse(data)

            backoff.recordSuccess(account: account)
            lastKnownPlan = payload.level

            return ProviderSnapshot(
                id: id, displayName: displayName, glyph: glyph,
                fidelity: .official, status: .ok,
                windows: payload.windows, headlineID: "session"
            )
        } catch NotchProviderError.rateLimited(let retryAfter) {
            backoff.recordLimit(retryAfter: retryAfter, account: account)
            NotchLog.usage.notice("glm: rate limited (\(self.backoff.consecutiveLimits, privacy: .public)x)")
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
                    forAttempt: backoff.consecutiveLimits,
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
