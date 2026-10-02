import AppKit
import CodexBarCore
import Foundation

/// A Codenotch-style provider backed by CodexBar's reader for the same service.
///
/// The native reader borrows a sign-in another tool already holds on this Mac. When there is
/// none, the CodexBar reader takes over with whatever CodeRim itself was given: a GitHub token
/// from its device sign-in, a Google sign-in for Antigravity, a pasted key, or the browser
/// session the user signed in with. So a provider works without its command-line tool.
@MainActor
final class HybridNotchProvider: NotchProvider {
    let native: any NotchProvider
    let upstream: ExtendedNotchProvider
    private var readingUpstream = false

    init(native: any NotchProvider, descriptor: ProviderDescriptor) {
        self.native = native
        self.upstream = ExtendedNotchProvider(descriptor: descriptor)
    }

    init(native: any NotchProvider, upstream: ExtendedNotchProvider) {
        self.native = native
        self.upstream = upstream
    }

    /// The CodexBar reader for a native provider id, when it has one.
    static func wrapping(_ native: any NotchProvider) -> any NotchProvider {
        guard native.id != "ollama-local", let descriptor = ExtendedProviderCatalog.descriptor(for: native.id) else { return native }
        return HybridNotchProvider(native: native, descriptor: descriptor)
    }

    var id: String { native.id }
    var displayName: String { native.displayName }
    var glyph: ProviderGlyph { native.glyph }
    var isVisibleWhenAbsent: Bool { native.isVisibleWhenAbsent }

    func account() -> ProviderAccount? { readingUpstream ? upstream.account() : native.account() }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        var nativeFailure: NotchProviderError = .needsAuth
        do {
            let snapshot = try await native.fetchSnapshot()
            if snapshot.status != .needsAuth { readingUpstream = false; return snapshot }
        } catch NotchProviderError.needsAuth {
            // Fall through to the reader that uses CodeRim's own sign-in.
        } catch NotchProviderError.accessDenied {
            // macOS refused the borrowed credential; CodeRim's own may still read.
            nativeFailure = .accessDenied
        }
        let snapshot = try await upstream.fetchSnapshot()
        if snapshot.status == .needsAuth { readingUpstream = false; throw nativeFailure }
        readingUpstream = true
        return snapshot.relabeled(id: id, displayName: displayName, glyph: glyph)
    }

    var signInRoute: SignInRoute { Self.route(for: id, native: native.signInRoute) }

    func signOut() async { await native.signOut() }
    func presentSignIn() { native.presentSignIn() }
    func forgetCachedCredential() { native.forgetCachedCredential(); upstream.forgetCachedCredential() }

    /// Where adding the provider sends the user when nothing on this Mac is signed in yet.
    /// Tools already installed keep their own sign-in; otherwise CodeRim signs in itself,
    /// takes a key, or reads the browser session — the way CodexBar connects them.
    static func route(for id: String, native: SignInRoute) -> SignInRoute {
        func web(_ name: String, _ url: String, _ extra: String = "") -> SignInRoute {
            .guided(.init(name: name, action: .browser(URL(string: url)!),
                note: "Sign in on the \(name) website in your browser. CodeRim then reads that browser session" +
                    " — if macOS asks to let CodeRim use your browser's saved data, choose Always Allow." + extra,
                importsBrowserSession: true))
        }
        func key(_ name: String, _ note: String) -> SignInRoute {
            .guided(.init(name: name, action: .settings, note: note))
        }
        switch id {
        case "copilot":
            return .guided(.init(name: "GitHub", action: .inApp(.githubDevice),
                note: "CodeRim shows a short code and opens GitHub. Enter the code, approve, and it connects — no GitHub CLI needed."))
        case "cursor":
            if case .openApp = native { return native }
            return web("Cursor", "https://cursor.com/dashboard", " Or install the Cursor editor and sign in there.")
        case "grok":
            if SignInLauncher.installedTool("grok") != nil { return native }
            return web("Grok", "https://grok.com")
        case "opencode":
            if SignInLauncher.installedTool("opencode") != nil { return native }
            return web("OpenCode", "https://opencode.ai/auth")
        case "commandcode":
            return web("Command Code", "https://commandcode.ai/studio")
        case "glm":
            return key("Z.ai", "Paste your GLM Coding Plan API key below (create one at z.ai › API keys). CodeRim also finds a key Claude Code, ZCode or OpenCode already uses.")
        case "ollama":
            return key("Ollama", "Paste an Ollama API key below (create one at ollama.com › Settings › Keys).")
        case "gemini":
            if AntigravityOAuthConfig.resolvedClient() != nil {
                return .guided(.init(name: "Antigravity", action: .inApp(.antigravityGoogle),
                    note: "Your browser opens Google's sign-in. Choose the account you use with Antigravity and allow access; CodeRim connects on its own. The Antigravity app does not need to be running."))
            }
            return .guided(.init(name: "Antigravity", action: .browser(URL(string: "https://antigravity.google/download")!),
                note: "Install the Antigravity app from this page — CodeRim signs in with its Google sign-in. Then choose Try again here and pick your Google account."))
        default:
            return native
        }
    }
}

extension ProviderSnapshot {
    /// The same reading under the provider's own name, so a fallback reader never renames a cell.
    func relabeled(id: String, displayName: String, glyph: ProviderGlyph) -> ProviderSnapshot {
        var copy = ProviderSnapshot(id: id, displayName: displayName, glyph: glyph, fidelity: fidelity,
                                    status: status, windows: windows, headlineID: headlineID, accountPlan: accountPlan)
        copy.block = block
        copy.todaysTokens = todaysTokens
        copy.localTokenUsage = localTokenUsage
        return copy
    }
}
