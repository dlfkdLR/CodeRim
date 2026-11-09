import Foundation

/// Display the account attached to a reading so borrowed credentials cannot silently show another
/// account’s usage.
struct ProviderAccount: Equatable {
    /// Email or display name, where the credential carries one.
    let label: String?
    /// The plan, named the way the provider names it.
    let plan: String?
    /// Which app's credential this borrows.
    let source: String
    /// The provider's own usage page, for checking this against the source.
    let manageURL: URL?

    /// One line for the settings row.
    var summary: String {
        [label, plan.map { $0 == $0.lowercased() ? $0.capitalized : $0 }, "via \(source)"]
            .compactMap { $0 }
            .joined(separator: " · ")
    }
}

/// Route sign-in through an owned session, the credential’s app, or explicit setup guidance.
enum SignInRoute: Equatable {
    /// The provider owns the session and can present its own sign-in window.
    case modal(name: String)
    /// Launch the app that owns the credential.
    case openApp(bundleID: String, name: String)
    /// Nothing to launch; Claude Code is a command, not an application.
    case guidance(String)
    /// CodeRim starts the sign-in itself: a Terminal window running the tool's login
    /// command, the provider's login page in the browser, or its key field in Settings.
    /// Adding a provider runs this once and then watches until the account appears.
    case guided(GuidedSignIn)

    var actionTitle: String? {
        switch self {
        case .modal(let name):     return "Sign in to \(name)"
        case .openApp(_, let name): return "Open \(name)"
        case .guidance:            return nil
        case .guided(let guided):  return guided.actionTitle
        }
    }

    var explanation: String {
        switch self {
        case .modal(let name):      return "Sign in to \(name) to read this account."
        case .openApp(_, let name): return "\(name) opens next. Sign in there with your account and keep it running: CodeRim reads your usage from it and connects on its own."
        case .guidance(let text):   return text
        case .guided(let guided):   return guided.note
        }
    }

    /// Direct account changes to the session or tool that owns the credential.
    var switchHint: String {
        switch self {
        case .modal(let name):      return "Sign out in the \(name) window to use another account."
        case .openApp(_, let name): return "Switch accounts in \(name); the notch follows."
        case .guidance:             return "Switch accounts in the tool that owns it; the notch follows."
        case .guided(let guided):   return "Switch accounts in \(guided.name); the notch follows."
        }
    }

    /// What switching a provider off does and does not reach, said plainly, so
    /// nobody switches off here expecting to be signed out of the tool as well.
    var signOutCaveat: String {
        switch self {
        case .modal(let name):
            return "Signs out of \(name) — the session belongs to CodeRim."
        case .openApp(_, let name):
            return "You stay signed in to \(name) — end that session in \(name) itself."
        case .guidance, .guided:
            return "You stay signed in to the tool that owns the account."
        }
    }
}

extension NotchProvider {
    /// Default a protocol requirement, rather than an extension-only member, so provider overrides
    /// dispatch dynamically.
    func account() -> ProviderAccount? { nil }

    var signInRoute: SignInRoute {
        .guidance("Sign in with the tool that owns this account.")
    }

    /// Nothing of our own to discard, by default.
    func signOut() async {}

    /// No modal of our own to show, by default — `NotchUsageStore.signIn` falls back
    /// to the route.
    func presentSignIn() {}

    /// Providers that hold nothing in memory have nothing to drop.
    func forgetCachedCredential() {}
}

/// A provider as the settings sheet needs it.
struct ProviderSummary: Identifiable, Equatable {
    /// Expose Keychain refusal recovery for providers that may read login-keychain credentials,
    /// including cursor-agent.
    var usesKeychain: Bool {
        id == "claude" || id.hasPrefix("claude-") || id == "gemini" || id == "cursor"
    }

    let id: String
    let name: String
    let glyph: ProviderGlyph
    let account: ProviderAccount?
    let signIn: SignInRoute
    /// Track access denial separately: a denied refresh retains the prior snapshot and must still offer
    /// Allow access.
    var wasRefusedAccess: Bool = false
}
