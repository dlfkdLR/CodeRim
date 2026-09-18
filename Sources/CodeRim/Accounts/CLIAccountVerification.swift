import Foundation

/// Only identity/status metadata leaves the official CLI probe. Credential
/// storage is shared with the CLI, never copied into a second login location.
enum CLIAccountVerification {
    static func requireCodex(_ response: [String: Any], account: SavedCodexAccount) throws {
        guard response["error"] == nil,
              let result = response["result"] as? [String: Any],
              let active = result["account"] as? [String: Any],
              active["type"] as? String == "chatgpt",
              active["email"] as? String == account.email else {
            throw AccountSwitchError.cliVerificationFailed
        }
        // account/read does not expose the workspace/subject. The coordinator
        // also rechecks the shared login's full ID after this probe completes.
    }

    static func requireClaude(_ data: Data, account: SavedClaudeAccount) throws {
        guard let status = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let loggedIn = status["loggedIn"] as? NSNumber,
              CFGetTypeID(loggedIn) == CFBooleanGetTypeID(), loggedIn.boolValue,
              status["authMethod"] as? String == "claude.ai",
              status["email"] as? String == account.email,
              status["orgId"] as? String == account.organizationID else {
            throw ClaudeAccountError.cliVerificationFailed
        }
    }
}
