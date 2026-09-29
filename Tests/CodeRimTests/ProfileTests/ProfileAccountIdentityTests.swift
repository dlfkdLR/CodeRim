import Foundation
import XCTest
@testable import CodeRim

final class ProfileAccountIdentityTests: XCTestCase {
    func testSharedWorkspaceUsersHaveDifferentNonIdentifyingKeys() throws {
        let a = try credential(subject: "user-a")
        let b = try credential(subject: "user-b")
        XCTAssertNotNil(a.accountKey)
        XCTAssertEqual(a.accountKey?.count, 64)
        XCTAssertNotEqual(a.accountKey, b.accountKey)
        XCTAssertFalse(try XCTUnwrap(a.accountKey).contains("workspace"))
        XCTAssertNil(ProfileCredential(accessToken: "opaque-token", accountID: "workspace").accountKey)
    }

    func testResponseFromPreviousUserInSameWorkspaceIsRejected() async throws {
        let sequence = CredentialSequence([try credential(subject: "user-a"), try credential(subject: "user-b")])
        do {
            _ = try await client(sequence).fetch()
            XCTFail("An old account's server response must not be displayed after a switch")
        } catch {
            XCTAssertEqual(error as? ProfileUsageError, .invalidCredentials)
        }
    }

    func testRotationForSameUserKeepsResponseOwnership() async throws {
        let a = try credential(subject: "user-a", signature: "one")
        let rotated = try credential(subject: "user-a", signature: "two")
        let result = try await client(CredentialSequence([a, rotated])).fetch()
        XCTAssertEqual(result.accountKey, a.accountKey)
        XCTAssertEqual(result.lifetime, 1_000)
    }

    func testOpaqueCredentialsMustRemainExactlyTheSame() async throws {
        let a = ProfileCredential(accessToken: "opaque-a", accountID: "workspace")
        let b = ProfileCredential(accessToken: "opaque-b", accountID: "workspace")
        do {
            _ = try await client(CredentialSequence([a, b])).fetch()
            XCTFail("Unidentified token changes must fail closed")
        } catch {
            XCTAssertEqual(error as? ProfileUsageError, .invalidCredentials)
        }
    }

    private func credential(subject: String, signature: String = "signature") throws -> ProfileCredential {
        let data = try JSONSerialization.data(withJSONObject: ["sub": subject])
        return ProfileCredential(accessToken: "header.\(data.base64EncodedString()).\(signature)", accountID: "workspace")
    }

    private func client(_ sequence: CredentialSequence) -> ChatGPTProfileClient {
        ChatGPTProfileClient(credentialLoader: { sequence.next() }, requestLoader: { _ in
            ProfileHTTPResponse(data: Data(#"{"stats":{"lifetime_tokens":1000,"daily_usage_buckets":[{"start_date":"2026-09-22","tokens":1000}]},"metadata":{"generated_at":"2026-09-23T00:00:00Z","stats_as_of":"2026-09-22","stats_error":null}}"#.utf8),
                response: HTTPURLResponse(url: ChatGPTProfileClient.endpoint, statusCode: 200,
                    httpVersion: nil, headerFields: nil)!)
        })
    }
}

private final class CredentialSequence: @unchecked Sendable {
    private let lock = NSLock()
    private let credentials: [ProfileCredential]
    private var index = 0
    init(_ credentials: [ProfileCredential]) { self.credentials = credentials }
    func next() -> ProfileCredential {
        lock.lock()
        defer { lock.unlock() }
        defer { index += 1 }
        return credentials[min(index, credentials.count - 1)]
    }
}
