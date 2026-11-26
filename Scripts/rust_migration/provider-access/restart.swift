import CodexBarCore
import Foundation

@main
enum Driver {
    static let ids: [UsageProvider] = [.openai, .azureopenai, .deepseek, .bedrock, .copilot, .openrouter]
    static func load(_ id: UsageProvider) throws -> ExtendedProviderConfiguration {
        try ExtendedProviderConfigurationStore.load(ProviderDescriptorRegistry.descriptor(for: id))
    }
    static func main() throws {
        let phase = CommandLine.arguments[1]
        if phase.hasPrefix("original-") {
            for id in ids {
                let descriptor = ProviderDescriptorRegistry.descriptor(for: id)
                var config = try LegacyExtendedProviderConfigurationStore.load(descriptor)
                if phase == "original-rollback" {
                    precondition(config.provider.apiKey == "fixture-new-" + id.rawValue && config.provider.workspaceID == "fixture-workspace")
                    continue
                }
                precondition(config.provider.apiKey == nil)
                config.provider.workspaceID = "fixture-workspace"
                config.provider.enterpriseHost = "https://example.invalid"
                config.environment = ["CODEXBAR_CONFIG": "/never-read", "OPENAI_API_KEY": "fixture-standard"]
                if id == .bedrock { config.provider.secretKey = "fixture-secret" }
                if id == .openrouter { config.environment["OPENROUTER_MANAGEMENT_API_KEY"] = "fixture-management" }
                if id == .deepseek {
                    config.environment["DEEPSEEK_KEY"] = "fixture-old-" + id.rawValue
                } else {
                    config.provider.apiKey = "fixture-old-" + id.rawValue
                }
                try LegacyExtendedProviderConfigurationStore.save(config)
            }
        } else if phase == "candidate-edit" {
            for id in ids {
                var config = try load(id)
                precondition(MigrationProviderAccess.savedAPIKey(config, for: id) == "fixture-old-" + id.rawValue)
                MigrationProviderAccess.setAPIKey("fixture-new-" + id.rawValue, in: &config, for: id)
                if id == .deepseek { precondition(config.environment["DEEPSEEK_KEY"] == nil) }
                try ExtendedProviderConfigurationStore.save(config)
            }
        } else {
            let isOld = phase == "candidate-reopen-old"
            for id in ids {
                let config = try load(id)
                let expected = (isOld ? "fixture-old-" : "fixture-new-") + id.rawValue
                precondition(MigrationProviderAccess.savedAPIKey(config, for: id) == expected && config.provider.workspaceID == "fixture-workspace")
                precondition(config.provider.enterpriseHost == "https://example.invalid" && config.environment["CODEXBAR_CONFIG"] == "/never-read")
                #if CODERIM_RUST_ACCOUNTING
                if let mode = MigrationProviderAccess.api[id.rawValue] {
                    let environment = MigrationProviderAccess.environment(for: config, descriptor: ProviderDescriptorRegistry.descriptor(for: id))!
                    precondition(environment[mode.key] == expected && environment["CODEXBAR_CONFIG"] == nil)
                    switch id {
                    case .bedrock: precondition(environment["AWS_SECRET_ACCESS_KEY"] == "fixture-secret" && environment["CODEXBAR_BEDROCK_AUTH_MODE"] == "keys")
                    case .azureopenai: precondition(environment["AZURE_OPENAI_DEPLOYMENT_NAME"] == "fixture-workspace" && environment["AZURE_OPENAI_ENDPOINT"] == "https://example.invalid")
                    case .openrouter: precondition(environment["OPENROUTER_MANAGEMENT_API_KEY"] == "fixture-management")
                    default: break
                    }
                }
                #endif
            }
        }
        let summary = ["phase": phase, "providers": String(ids.count), "privateQueries": String(PrivateKeychain.shared.trace.count)]
        print(String(decoding: try JSONSerialization.data(withJSONObject: summary, options: .sortedKeys), as: UTF8.self))
    }
}
