import Foundation
import os

/// Self-signed TLS is allowed only after the live TCP peer is bound to the
/// discovered current-user process. A certificate alone is not that identity.
final class LocalhostTrust: NSObject, URLSessionDelegate, @unchecked Sendable {
    private let listener: LocalTCPPeer.Address
    private let process: LocalTCPPeer.ProcessIdentity
    private let verifyPeer: @Sendable (LocalTCPPeer.Address, LocalTCPPeer.ProcessIdentity) -> Bool
    private let verification = OSAllocatedUnfairLock(initialState: false)

    init(listener: LocalTCPPeer.Address, process: LocalTCPPeer.ProcessIdentity,
         verifyPeer: @escaping @Sendable (LocalTCPPeer.Address, LocalTCPPeer.ProcessIdentity) -> Bool = { LocalTCPPeer.verify($0, process: $1) }) {
        self.listener = listener
        self.process = process
        self.verifyPeer = verifyPeer
    }

    var verified: Bool { verification.withLock { $0 } }

    func urlSession(_ session: URLSession,
                    didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition,
                                                  URLCredential?) -> Void) {
        // Completing this challenge precedes sending HTTP headers, including
        // CSRF. Refuse even normally trusted certificates for an unbound peer.
        let space = challenge.protectionSpace
        let host = space.host.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        guard space.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              host == listener.host, space.port == listener.port,
              listener.isLoopback, let trust = space.serverTrust,
              verifyPeer(listener, process) else {
            return completionHandler(.cancelAuthenticationChallenge, nil)
        }
        verification.withLock { $0 = true }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
}
