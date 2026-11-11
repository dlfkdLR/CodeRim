import Foundation

/// Creates, claims and saves one new connection; a failed claim/save removes only the new account.
enum MobileEnrollment {
    static func enroll(_ link: MobilePairingLink, client: any MobileRelayServing,
                       credentials: any MobileCredentialStoring) async throws -> (MobileCredential, MobileClaimResult) {
        let session: MobileSessionToken = try await client.send("POST", "/v1/accounts", token: nil, body: MobileEmpty())
        let saved = MobileCredential(endpoint: link.relay, session: session)
        do {
            let joined: MobileClaimResult = try await client.send("POST", "/v1/pairing/claim", token: saved.token,
                body: MobilePairingRequest(id: link.id, secret: link.secret))
            try await credentials.save(saved)
            return (saved, joined)
        } catch {
            let _: MobileOK? = try? await client.send("DELETE", "/v1/account", token: saved.token, body: MobileEmpty())
            throw error
        }
    }
}
