import Foundation

/// Reads OpenCode Go plan usage from the official endpoint, with the key
/// OpenCode itself stores on sign-in. No `opencode-go` key, no ring.
///
/// The endpoint throttles, so a 429 backs off on a schedule that outlives the
/// process (persisted in `UsageArchive`) rather than polling into the limit.
/// Two upstream quirks: a valid key with no Go plan answers 401, the same as a
/// bad key; and Zen pay-as-you-go credit has no API, so this is the Go windows
/// only.
///
/// Ported from the MIT-licensed Codenotch (`OpenCodeProvider`).
@MainActor
final class OpenCodeNotchProvider: NotchProvider {
    let id = "opencode"
    let displayName = "OpenCode"
    let glyph: ProviderGlyph = .opencode
    var isVisibleWhenAbsent: Bool { false }

    private let session: URLSession
    private let archive: UsageArchive
    private var retryNoEarlierThan: Date?
    private var consecutiveRateLimits = 0

    init(session: URLSession = .shared, archive: UsageArchive = UsageArchive()) {
        self.session = session
        self.archive = archive
        self.retryNoEarlierThan = archive.loadBackoffUntil(providerID: "opencode")
    }

    var signInRoute: SignInRoute {
        .guidance("Usage rides on the opencode-go key OpenCode stores on sign-in — connect Go inside OpenCode (`opencode auth login`) and the notch reads it.")
    }

    func account() -> ProviderAccount? {
        guard OpenCodeCredentials.load() != nil else { return nil }
        return ProviderAccount(label: nil, plan: "Go", source: "OpenCode",
                               manageURL: URL(string: "https://opencode.ai"))
    }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        if let retryNoEarlierThan, retryNoEarlierThan > Date() {
            throw NotchProviderError.rateLimited(retryAfter: retryNoEarlierThan.timeIntervalSinceNow)
        }

        // An ordinary file, not a keychain item — reading it prompts nobody.
        guard let credentials = OpenCodeCredentials.load() else {
            throw NotchProviderError.needsAuth
        }

        do {
            let data = try await fetch(token: credentials.token)
            guard let text = String(data: data, encoding: .utf8) else {
                throw NotchProviderError.badResponse(status: 0)
            }
            let read = try OpenCodeUsage.windows(fromJSON: text)

            consecutiveRateLimits = 0
            retryNoEarlierThan = nil
            archive.saveBackoffUntil(nil, providerID: id)

            return ProviderSnapshot(
                id: id, displayName: displayName, glyph: glyph,
                fidelity: .official, status: .ok,
                windows: read, headlineID: "rolling"
            )
        } catch NotchProviderError.rateLimited(let retryAfter) {
            // Bookkeeping where the answer was, not down in `fetch`: the wait
            // has to outlive the request that earned it.
            consecutiveRateLimits += 1
            retryNoEarlierThan = Date().addingTimeInterval(retryAfter)
            archive.saveBackoffUntil(retryNoEarlierThan, providerID: id)
            NotchLog.usage.notice("opencode: rate limited (\(self.consecutiveRateLimits, privacy: .public)x), next attempt in \(retryAfter, privacy: .public)s")
            throw NotchProviderError.rateLimited(retryAfter: retryAfter)
        }
    }

    private func fetch(token: String) async throws -> Data {
        var request = URLRequest(url: OpenCodeUsage.endpoint)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 15

        let (data, response) = try await BoundedHTTP.data(for: request, on: session)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0

        // Upstream serves a missing Go plan as 401 through the same branch as a
        // bad key. Both read as "nothing readable here".
        if status == 401 { throw NotchProviderError.needsAuth }
        // A valid key not entitled to Go: readable, metering nothing.
        if status == 403 {
            throw NotchProviderError.nothingMetered("No OpenCode Go subscription on this key")
        }
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

    /// A minute, doubling per consecutive limit, capped so it always recovers
    /// on its own. The server's own hint only raises the floor.
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
            .trimmingCharacters(in: CharacterSet.whitespacesAndNewlines)
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
