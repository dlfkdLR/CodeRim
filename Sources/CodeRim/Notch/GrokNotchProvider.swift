import Foundation

/// Reads Grok Build usage from the billing endpoint the CLI's `/usage` uses.
/// The credential is Grok's own `~/.grok/auth.json` session — the CLI's job to
/// refresh, not this app's. No signed-in Grok CLI, no ring.
///
/// Ported from the MIT-licensed Codenotch (`GrokLocalProvider`).
@MainActor
final class GrokNotchProvider: NotchProvider {
    let id = "grok"
    let displayName = "Grok"
    let glyph: ProviderGlyph = .grok
    var isVisibleWhenAbsent: Bool { false }

    private let creditsURL = URL(string: "https://cli-chat-proxy.grok.com/v1/billing?format=credits")!
    private let session: URLSession
    private let authURL: URL

    init(session: URLSession = .shared, authURL: URL = GrokCredentials.authURL) {
        self.session = session
        self.authURL = authURL
    }

    var signInRoute: SignInRoute {
        .guidance("Run `grok login` — it signs in and refreshes the token this reads.")
    }

    func account() -> ProviderAccount? { GrokCredentials.account() }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        let authURL = self.authURL
        let credentials = try await Task.detached { try GrokCredentials.load(from: authURL) }.value
        if credentials.isExpired { throw NotchProviderError.credentialExpired }

        let credits = try await body(from: creditsURL, token: credentials.accessToken)
        // Size only — see the note in `CursorNotchProvider`. A billing response
        // is not something to write into the system log.
        NotchLog.usage.debug("grok credits -> \(credits.utf8.count, privacy: .public) bytes")

        return ProviderSnapshot(
            id: id,
            displayName: displayName,
            glyph: glyph,
            fidelity: .official,
            status: .ok,
            windows: try GrokUsage.windows(creditsJSON: credits),
            headlineID: "credits"
        )
    }

    private func body(from url: URL, token: String) async throws -> String {
        var request = URLRequest(url: url)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("xai-grok-cli", forHTTPHeaderField: "X-XAI-Token-Auth")
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
