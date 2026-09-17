import Foundation

/// Gemini usage as Antigravity sees it, read from Antigravity's own local
/// language server (`AntigravityBridge`). No ring unless the Antigravity IDE or
/// its `agy` CLI is running — the figure comes from Antigravity, so Antigravity
/// has to be there.
///
/// Ported from the MIT-licensed Codenotch (`AntigravityProvider`), bridge path
/// only. The Google `:loadCodeAssist` account-tier call and the SQLite
/// counted-requests fallback are not carried across.
@MainActor
final class AntigravityNotchProvider: NotchProvider {
    // The id stays `gemini`: it keys the archive, and `authPrompt` already
    // names Antigravity for it.
    let id = "gemini"
    let displayName = "Antigravity"
    let glyph: ProviderGlyph = .antigravity
    var isVisibleWhenAbsent: Bool { false }

    private let session: URLSession
    private let discover: @Sendable () -> AntigravityBridge.Endpoint?

    init(discover: (@Sendable () -> AntigravityBridge.Endpoint?)? = nil) {
        self.session = URLSession(configuration: .ephemeral,
                                  delegate: LocalhostTrust(), delegateQueue: nil)
        self.discover = discover ?? { AntigravityBridge.discover() }
    }

    var signInRoute: SignInRoute {
        .openApp(bundleID: "com.google.antigravity", name: "Antigravity")
    }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        // Discovery spawns `ps` and `lsof`; keep it off the main actor.
        let find = discover
        guard let endpoint = await Task.detached(operation: { find() }).value else {
            throw NotchProviderError.needsAuth
        }

        let windows = try await AntigravityBridge.quota(from: endpoint, session: session)
        guard !windows.isEmpty else {
            throw NotchProviderError.nothingMetered("Antigravity reported no metered quota")
        }

        let headline = windows.max(by: { ($0.usedFraction ?? 0) < ($1.usedFraction ?? 0) })?.id
        return ProviderSnapshot(
            id: id, displayName: displayName, glyph: glyph,
            fidelity: .official, status: .ok,
            windows: windows, headlineID: headline
        )
    }
}
