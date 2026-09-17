import Foundation

/// Accepts the self-signed certificate a local language server presents, and
/// only on loopback. Anything not `127.0.0.1` / `localhost` / `::1` falls
/// through to the system's normal validation.
///
/// Ported from the MIT-licensed Codenotch (`LocalhostTrust`).
final class LocalhostTrust: NSObject, URLSessionDelegate {
    func urlSession(_ session: URLSession,
                    didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition,
                                                  URLCredential?) -> Void) {
        let host = challenge.protectionSpace.host
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              host == "127.0.0.1" || host == "localhost" || host == "::1",
              let trust = challenge.protectionSpace.serverTrust
        else { return completionHandler(.performDefaultHandling, nil) }

        completionHandler(.useCredential, URLCredential(trust: trust))
    }
}
