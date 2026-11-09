import Darwin
import Foundation
import Security
import XCTest
@testable import CodeRim

final class LocalhostTrustTests: XCTestCase {
    private let listener = LocalTCPPeer.Address(host: "127.0.0.1", port: 63881)
    private let process = LocalTCPPeer.ProcessIdentity(pid: 123, userID: getuid(), startedSeconds: 1, startedMicroseconds: 2)

    func testCertificateDoesNotAuthorizeAnUnverifiedProcess() throws {
        let delegate = LocalhostTrust(listener: listener, process: process, verifyPeer: { _, _ in false })
        let challenge = try challenge(host: listener.host, port: listener.port)
        delegate.urlSession(.shared, didReceive: challenge) { disposition, credential in
            XCTAssertEqual(disposition, .cancelAuthenticationChallenge)
            XCTAssertNil(credential)
        }
        XCTAssertFalse(delegate.verified)
    }

    func testOnlyTheSelectedAddressPortAndProcessCanCompleteTLS() throws {
        let expectedListener = listener
        let expectedProcess = process
        let delegate = LocalhostTrust(listener: listener, process: process, verifyPeer: { listener, process in
            listener == expectedListener && process == expectedProcess
        })
        for (host, port) in [("::1", listener.port), (listener.host, listener.port + 1), ("localhost", listener.port)] {
            delegate.urlSession(.shared, didReceive: try challenge(host: host, port: port)) { disposition, credential in
                XCTAssertEqual(disposition, .cancelAuthenticationChallenge)
                XCTAssertNil(credential)
            }
            XCTAssertFalse(delegate.verified)
        }
        delegate.urlSession(.shared, didReceive: try challenge(host: listener.host, port: listener.port)) { disposition, credential in
            XCTAssertEqual(disposition, .useCredential)
            XCTAssertNotNil(credential)
        }
        XCTAssertTrue(delegate.verified)
    }

    private func challenge(host: String, port: Int) throws -> URLAuthenticationChallenge {
        // Public dummy certificate only; expiration is irrelevant to the
        // custom trust policy. No private key or provider credential is used.
        let der = Data(base64Encoded: "MIICpDCCAYwCCQDySssr/65XVDANBgkqhkiG9w0BAQsFADAUMRIwEAYDVQQDDAlsb2NhbGhvc3QwHhcNMjYxMTA0MjI0MTM2WhcNMjYxMTA1MjI0MTM2WjAUMRIwEAYDVQQDDAlsb2NhbGhvc3QwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQCegYWYXz2Ivyw09brvReYA0Zq35AFbPNKhAMkCes3JSjymt6pc5s6xKBihD5V6Zf9lTKrqA1FgJgLV84uPwyK7NKZta4TkEBQtnZ4Q8NrG3A1EF6Z7UvWj8qH3Rnc4JxKvsly9ouKbnPgVWxhOOidIBt0XKKYMfqgA8TWoTYu1pjLq8n1aeDmtAhdCAzUedvX7g1yQ7b89KLHulCf8fR7YyqG1LqHcjzhDCbYhgtkuQeIOhw5/g1IecO/kAueilpfNT+foAzPht+VQ4BqC56Tv7TWONHQGnUJQIioP6L4c9QsNWGyjP00SYmiTx8tsyCafQighyUUs6Io+biFYbX1hAgMBAAEwDQYJKoZIhvcNAQELBQADggEBAFsknf1aWy4YXw9ksy48jaSV/+5oZFwN7/1b+izcX/ADFgsH7AK97fG/fDAZdoEpRQxWvLoHAXr0hscE15ruv8iLRdOf5Edz46QcgPZc6yAH8nYFCYPGyL5PB5x/3kAVVLgOcSJ5IzD6r+wXvTO/7YWkikxaerQeNb9tf5EMmzOuGl4aUIjM+o5qSRbOe5slc2KrXcAZSZMoVrICgvRtiJbt09dJD+ErZp12e8bfBzscvpnIt6JDVwHXU8RrnpwnjTPKlR2AkYB6dItGALisxnsXplWghnjp7oA8rW9vP9AK37sVB6XG5ak7rERXhBa2WPqrLscie/jeuMWMKyTR5NE=")!
        let certificate = try XCTUnwrap(SecCertificateCreateWithData(nil, der as CFData))
        var trust: SecTrust?
        XCTAssertEqual(SecTrustCreateWithCertificates(certificate, SecPolicyCreateBasicX509(), &trust), errSecSuccess)
        return URLAuthenticationChallenge(protectionSpace: TestProtectionSpace(host: host, port: port, trust: try XCTUnwrap(trust)),
            proposedCredential: nil, previousFailureCount: 0, failureResponse: nil, error: nil, sender: TestChallengeSender())
    }
}

private final class TestProtectionSpace: URLProtectionSpace, @unchecked Sendable {
    private let trust: SecTrust
    init(host: String, port: Int, trust: SecTrust) {
        self.trust = trust
        super.init(host: host, port: port, protocol: "https", realm: nil,
                   authenticationMethod: NSURLAuthenticationMethodServerTrust)
    }
    required init?(coder: NSCoder) { return nil }
    override var serverTrust: SecTrust? { trust }
}

private final class TestChallengeSender: NSObject, URLAuthenticationChallengeSender {
    func use(_ credential: URLCredential, for challenge: URLAuthenticationChallenge) {}
    func continueWithoutCredential(for challenge: URLAuthenticationChallenge) {}
    func cancel(_ challenge: URLAuthenticationChallenge) {}
    func performDefaultHandling(for challenge: URLAuthenticationChallenge) {}
    func rejectProtectionSpaceAndContinue(with challenge: URLAuthenticationChallenge) {}
}
