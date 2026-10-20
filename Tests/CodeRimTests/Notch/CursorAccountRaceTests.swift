import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class CursorAccountRaceTests: XCTestCase {
    func testDropsSuccessWhenExternalAccountChangedDuringRequest() async throws {
        let provider = fixture(accounts: ["account-a", "account-b"])
        defer { ProviderFixtureProtocol.uninstall() }
        do {
            _ = try await provider.fetchSnapshot()
            XCTFail("An old account response must not be published")
        } catch NotchProviderError.needsAuth {} 
    }

    func testAcceptsRefreshedTokenForSameAccount() async throws {
        let provider = fixture(accounts: ["account-a", "account-a"])
        defer { ProviderFixtureProtocol.uninstall() }
        let result = try await provider.fetchSnapshot()
        XCTAssertEqual(result.windows.first?.usedFraction, 0.8)
    }

    private func fixture(accounts: [String]) -> CursorNotchProvider {
        ProviderFixtureProtocol.install { request in
            XCTAssertEqual(request.value(forHTTPHeaderField: "Cookie"), "WorkosCursorSessionToken=account-a::token-1")
            return (200, #"{"individualUsage":{"plan":{"autoPercentUsed":80}}}"#)
        }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ProviderFixtureProtocol.self]
        let source = CredentialsSequence(accounts: accounts)
        return CursorNotchProvider(session: URLSession(configuration: configuration), loadCredentials: { await source.next() })
    }
}
private actor CredentialsSequence {
    let accounts: [String]
    var index = 0
    init(accounts: [String]) { self.accounts = accounts }
    func next() -> CursorCredentials {
        let account = accounts[min(index, accounts.count - 1)]
        index += 1
        return CursorCredentials(accountID: account, accessToken: "token-\(index)")
    }
}
