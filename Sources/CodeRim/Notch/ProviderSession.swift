import Foundation

/// The session every credential-bearing provider poll runs on.
///
/// These requests carry a borrowed session — Cursor's editor cookie, Grok's
/// CLI bearer, the GLM plan key — and answer with plan, spend and team billing
/// figures. `URLSession.shared` is the wrong place for that traffic:
///
/// - Its `URLCache` is disk-backed, so a billing response lands in the app's
///   cache directory as plaintext JSON and outlives the poll that fetched it.
/// - Its cookie jar is process-wide and persistent, so one provider's
///   `Set-Cookie` is offered back to every other provider on the same host.
/// - Its credential storage participates in system-wide auth challenges.
///
/// `ChatGPTProfileClient` already built its own session for exactly these
/// reasons. This is that decision, shared — the notch providers borrow
/// credentials the same way and deserve the same handling.
///
/// Ephemeral rather than a configured default: `URLSessionConfiguration
/// .ephemeral` keeps caches, cookies and credentials in memory for the
/// session's lifetime and never writes them to disk.
enum ProviderSession {
    /// One session for every remote provider. `URLSession` pools connections
    /// per session, so sharing one keeps a single connection pool across the
    /// nine endpoints polled on a timer rather than nine of them.
    static let shared: URLSession = make()

    static func make() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 30
        return URLSession(configuration: configuration)
    }
}
