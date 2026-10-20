import Foundation
import CodexBarCore

/// Corrected billing readers shared with Windows, executed by the pinned upstream sandbox.
enum SharedScriptProvider {
    static func fetch(_ id: CodexBarCore.UsageProvider, environment: [String: String],
                      transport: any ProviderHTTPTransport = ProviderHTTPClient.shared) async throws -> ProviderFetchResult {
        let name: String
        let secret: String?
        let key: String
        var settings: [String: String] = [:]
        switch id {
        case .xai:
            name = "xai"
            key = XAISettingsReader.apiKeyEnvironmentKey
            secret = XAISettingsReader.apiKey(environment: environment)
            guard let team = XAISettingsReader.teamID(environment: environment) else { throw XAISettingsError.missingTeamID }
            guard !team.contains("/"), team != ".", team != ".." else { throw XAISettingsError.invalidTeamID }
            settings[XAISettingsReader.teamIDEnvironmentKey] = team
        case .poe:
            name = "poe"
            key = PoeSettingsReader.apiKeyEnvironmentKey
            secret = PoeSettingsReader.apiKey(environment: environment)
        default:
            throw ProviderPluginError.load("No shared reader for this provider")
        }
        guard let secret, !secret.isEmpty else {
            throw ProviderFetchClassifiedError(kind: .missingCredential, message: "Connect the provider in Settings.")
        }
        guard let url = Bundle.module.url(forResource: name, withExtension: "js") else {
            throw ProviderPluginError.load("Shared provider reader is missing")
        }
        let runtime = try ProviderPluginRuntime(source: String(contentsOf: url, encoding: .utf8),
                                                transport: transport, timeout: 20,
                                                responseSizeLimit: BoundedHTTP.defaultMaximumBytes)
        guard runtime.manifest.id == id.instanceID else {
            throw ProviderPluginError.invalidManifest("Shared provider identity does not match")
        }
        try Task.checkCancellation()
        let usage = try await runtime.fetchUsage(settings: settings, secrets: [key: secret])
        try Task.checkCancellation()
        return ProviderFetchResult(usage: usage, credits: nil, dashboard: nil,
                                   sourceLabel: "api", strategyID: name + ".shared-script", strategyKind: .apiToken)
    }
}
