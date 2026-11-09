import CryptoKit
import Foundation

/// One source of usage numbers. Each adapter declares how trustworthy it is,
/// and the UI never dresses a derived number up as an official one.
@MainActor
protocol NotchProvider {
    var id: String { get }
    /// Enough to draw the cell even when a fetch has never succeeded.
    var displayName: String { get }
    var glyph: ProviderGlyph { get }
    func fetchSnapshot() async throws -> ProviderSnapshot
    /// Keep account methods as protocol requirements so existential calls dispatch to provider
    /// overrides.
    func account() -> ProviderAccount?
    /// Non-secret current-account identity, checked around each fetch and used to partition cached
    /// readings/backoff across switches.
    func accountIdentity() -> String?
    /// Where the user goes to sign in, when there is no account to read. A
    /// requirement for the same reason `account()` is.
    var signInRoute: SignInRoute { get }
    /// Discard app-owned sessions only; borrowed credentials belong to their tools.
    func signOut() async
    /// Present an owned sign-in modal; other providers use their signInRoute.
    func presentSignIn()
    /// Drop cached credentials so the next read can re-request Keychain access after a denial.
    func forgetCachedCredential()
    /// Whether this provider should keep a cell or placeholder in the notch when
    /// its account or daemon is unavailable. Most providers default to `true`
    /// so users see sign-in guidance; local daemon providers return `false`
    /// so an inactive service does not take up a ring in the notch.
    var isVisibleWhenAbsent: Bool { get }
}

extension NotchProvider {
    var isVisibleWhenAbsent: Bool { true }
    func accountIdentity() -> String? { nil }
}

/// Fingerprints for `accountIdentity()`: short SHA-256 prefixes, so neither a key nor an e-mail
/// address is written next to the readings it owns.
enum AccountIdentity {
    static func fingerprint(_ parts: String...) -> String {
        let digest = SHA256.hash(data: Data(parts.joined(separator: "\u{1F}").utf8))
        return digest.prefix(12).map { String(format: "%02x", $0) }.joined()
    }
}

/// A rate-limit wait that belongs to one account. A 429 earned by account A must not keep account B
/// from being read after a switch, and the doubling restarts for each account.
@MainActor
struct AccountBackoff {
    let providerID: String
    let archive: UsageArchive
    private(set) var consecutiveLimits = 0
    private var account: String?

    init(providerID: String, archive: UsageArchive) {
        self.providerID = providerID
        self.archive = archive
    }

    /// Seconds still to wait before `account` may be asked again, or nil.
    mutating func remainingWait(for account: String) -> TimeInterval? {
        if self.account != account { self.account = account; consecutiveLimits = 0 }
        return archive.loadBackoffUntil(providerID: providerID, account: account).map { $0.timeIntervalSinceNow }
    }

    mutating func recordLimit(retryAfter: TimeInterval, account: String) {
        if self.account != account { self.account = account; consecutiveLimits = 0 }
        consecutiveLimits += 1
        archive.saveBackoffUntil(Date().addingTimeInterval(retryAfter), providerID: providerID, account: account)
    }

    mutating func recordSuccess(account: String) {
        self.account = account
        consecutiveLimits = 0
        archive.saveBackoffUntil(nil, providerID: providerID, account: account)
    }

    nonisolated static func delay(forAttempt attempt: Int, retryAfter: TimeInterval?) -> TimeInterval {
        min(15 * 60, max(60 * pow(2, Double(min(attempt, 4))), retryAfter ?? 0))
    }

    /// Callers retain their endpoint's header-whitespace policy.
    nonisolated static func retryAfter(_ header: String?) -> TimeInterval? {
        guard let header else { return nil }
        if let seconds = TimeInterval(header) { return max(0, seconds) }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "GMT")
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        return formatter.date(from: header).map { max(0, $0.timeIntervalSinceNow) }
    }
}

enum NotchProviderError: Error {
    /// No usable credential — the user has to sign in again.
    case needsAuth
    /// The credential is there, and macOS refused to hand it over — the
    /// keychain prompt was declined. Not the same as being signed out: telling
    /// someone to sign in, when they are signed in and merely pressed Deny,
    /// sends them to fix something that is not broken.
    case accessDenied
    /// The credential is there but has expired, and the app that owns it will
    /// refresh it the next time it runs. Not the same as being signed out: the
    /// last reading is still true, just old.
    case credentialExpired
    /// The endpoint answered, but not with anything we understand.
    case badResponse(status: Int)
    /// The body was larger than a usage reading has any business being. Its own
    /// case rather than `badResponse`: the status was usually 200, and "HTTP
    /// 200" is not a useful thing to show someone.
    case responseTooLarge
    /// Asked to slow down. Carries the server's own retry hint when it gave one.
    case rateLimited(retryAfter: TimeInterval)
    /// The account is readable, but there is genuinely no quota being counted —
    /// Cursor's free plan reports an included limit of zero. Not an error, and
    /// it must not be shown as one.
    case nothingMetered(String)
}
