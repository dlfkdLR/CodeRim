import Foundation
import XCTest
@testable import CodeRim

final class CLIAccountVerificationTests: XCTestCase {
    func testCodexRequiresSelectedChatGPTEmailAndSuccessfulResponse() throws {
        let selected = try codexAccount()
        let active: [String: Any] = ["type": "chatgpt", "email": selected.email, "planType": "pro"]
        XCTAssertNoThrow(try CLIAccountVerification.requireCodex(["result": ["account": active]], account: selected))
        for response: [String: Any] in [
            [:], ["result": [:]], ["result": ["account": NSNull()]],
            ["result": ["account": ["type": "apiKey", "email": selected.email]]],
            ["result": ["account": ["type": "chatgpt", "email": "other@example.test"]]],
            ["error": ["message": "synthetic"], "result": ["account": active]]
        ] {
            XCTAssertThrowsError(try CLIAccountVerification.requireCodex(response, account: selected)) {
                XCTAssertEqual($0 as? AccountSwitchError, .cliVerificationFailed)
            }
        }
    }

    func testClaudeRequiresBooleanLoginSubscriptionMethodEmailAndOrganization() throws {
        let selected = try ClaudeAccountFixture.saved("two")
        let status: [String: Any] = ["loggedIn": true, "authMethod": "claude.ai",
                                   "email": selected.email, "orgId": selected.organizationID]
        func data(_ object: [String: Any]) throws -> Data {
            try JSONSerialization.data(withJSONObject: object)
        }
        XCTAssertNoThrow(try CLIAccountVerification.requireClaude(data(status), account: selected))
        for (key, value): (String, Any) in [
            ("loggedIn", false), ("loggedIn", 1), ("loggedIn", "true"),
            ("authMethod", "api_key"), ("email", "other@example.test"), ("orgId", "another-org")
        ] {
            var changed = status; changed[key] = value
            XCTAssertThrowsError(try CLIAccountVerification.requireClaude(data(changed), account: selected)) {
                XCTAssertEqual($0 as? ClaudeAccountError, .cliVerificationFailed)
            }
        }
        for missing in status.keys {
            var changed = status; changed.removeValue(forKey: missing)
            XCTAssertThrowsError(try CLIAccountVerification.requireClaude(data(changed), account: selected))
        }
        XCTAssertThrowsError(try CLIAccountVerification.requireClaude(Data("not JSON".utf8), account: selected))
    }

    private func codexAccount() throws -> SavedCodexAccount {
        let claims: [String: Any] = ["sub": "synthetic", "email": "synthetic@example.test"]
        let payload = try JSONSerialization.data(withJSONObject: claims).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
        let login: [String: Any] = ["auth_mode": "chatgpt", "tokens": [
            "account_id": "workspace", "id_token": "e30.\(payload).synthetic",
            "access_token": "synthetic-access", "refresh_token": "synthetic-refresh"]]
        return try SavedCodexAccount(loginData: JSONSerialization.data(withJSONObject: login))
    }
}
