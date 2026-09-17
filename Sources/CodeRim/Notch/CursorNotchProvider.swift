import AppKit
import Foundation

/// Reads Cursor usage as the account already signed in to the Cursor editor on
/// this Mac. Borrowing the editor's own session removes the "which account?"
/// question — there is only ever the one being used. No editor session, no
/// ring.
///
/// Ported from the MIT-licensed Codenotch (`CursorLocalProvider`), editor path
/// only.
@MainActor
final class CursorNotchProvider: NotchProvider {
    let id = "cursor"
    let displayName = "Cursor"
    let glyph: ProviderGlyph = .cursor
    var isVisibleWhenAbsent: Bool { false }

    private let endpoint = URL(string: "https://cursor.com/api/usage-summary")!
    private let session: URLSession

    init(session: URLSession = .shared) {
        self.session = session
    }

    var signInRoute: SignInRoute {
        // Bundle-id lookup, not a hard-coded /Applications path: Cursor can
        // live in ~/Applications, and a miss would hide the Open button from
        // someone who does have the editor.
        let installed = NSWorkspace.shared.urlForApplication(
            withBundleIdentifier: CursorCredentials.bundleID
        ) != nil
        return CursorCredentials.signInRoute(editorInstalled: installed)
    }

    func account() -> ProviderAccount? { CursorCredentials.account() }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        // Reads the editor's SQLite store; keep it off the main actor.
        let credentials = try await Task.detached { try CursorCredentials.load() }.value

        var request = URLRequest(url: endpoint)
        request.setValue(credentials.sessionCookie, forHTTPHeaderField: "Cookie")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 15

        let (data, response) = try await BoundedHTTP.data(for: request, on: session)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0

        if status == 401 || status == 403 { throw NotchProviderError.needsAuth }
        guard (200..<300).contains(status) else {
            throw NotchProviderError.badResponse(status: status)
        }

        let body = String(data: data, encoding: .utf8) ?? ""
        // Size only, never the payload: this response carries the plan, spend
        // and team billing figures, and `privacy: .public` would write them to
        // the unified log in the clear — where any other process can read them
        // and where `log collect` and sysdiagnose pick them up.
        NotchLog.usage.debug("cursor usage -> \(data.count, privacy: .public) bytes")

        let windows = try CursorUsage.windows(fromJSON: body)
        return ProviderSnapshot(
            id: id,
            displayName: displayName,
            glyph: glyph,
            fidelity: .official,
            status: .ok,
            windows: windows,
            headlineID: CursorUsage.headlineID(in: windows)
        )
    }
}
