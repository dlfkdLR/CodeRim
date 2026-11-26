#if CODERIM_RUST_ACCOUNTING
import CodexBarCore
import Foundation
import Security

/// Only these pinned upstream strategies accept the supplied API key without
/// consulting a browser, another tool, or a fallback strategy. A new upstream
/// strategy must be reviewed before its identifier can be added here.
enum MigrationProviderAccess {
    struct API {
        let strategy: String
        let key: String
        init(_ strategy: String, _ key: String) { self.strategy = strategy; self.key = key }
    }

    static let api: [String: API] = [
        "aiand": API("aiand.api", "AIAND_API_KEY"),
        "chutes": API("chutes.api", "CHUTES_API_KEY"),
        "clawrouter": API("clawrouter.js", "CLAWROUTER_API_KEY"),
        "clinepass": API("clinepass.js", "CLINE_API_KEY"),
        "crof": API("crof.js", "CROFAI_API_KEY"),
        "deepgram": API("deepgram.js", "DEEPGRAM_API_KEY"),
        "deepinfra": API("deepinfra.api", "DEEPINFRA_API_KEY"),
        "deepseek": API("deepseek.api", "DEEPSEEK_API_KEY"),
        "elevenlabs": API("elevenlabs.api", "ELEVENLABS_API_KEY"),
        "fireworks": API("fireworks.api", "FIREWORKS_API_KEY"),
        "kilo": API("kilo.api", "KILO_API_KEY"),
        "litellm": API("litellm.api", "LITELLM_API_KEY"),
        "llmproxy": API("llmproxy.api", "LLM_PROXY_API_KEY"),
        "minimax": API("minimax.api", "MINIMAX_API_KEY"),
        "moonshot": API("moonshot.api", "MOONSHOT_API_KEY"),
        "neuralwatt": API("neuralwatt.api", "NEURALWATT_API_KEY"),
        "opencodego": API("opencodego.api", "OPENCODE_API_KEY"),
        "openrouter": API("openrouter.js", "OPENROUTER_API_KEY"),
        "poe": API("poe.shared-script", "POE_API_KEY"),
        "synthetic": API("synthetic.js", "SYNTHETIC_API_KEY"),
        "venice": API("venice.js", "VENICE_API_KEY"),
        "xai": API("xai.shared-script", "XAI_MANAGEMENT_API_KEY"),
        "zai": API("zai.js", "Z_AI_API_KEY"),
    ]

    // Compatibility with API keys previously saved in this provider's CodeRim configuration.
    static let keyAliases: [String: [String]] = [
        "crof": ["CROF_API_KEY"], "deepinfra": ["DEEPINFRA_TOKEN"], "deepseek": ["DEEPSEEK_KEY"],
        "elevenlabs": ["XI_API_KEY"], "fireworks": ["FIREWORKS_KEY"], "minimax": ["MINIMAX_CODING_API_KEY"],
        "moonshot": ["MOONSHOT_KEY"], "venice": ["VENICE_KEY"],
        "zai": ["GLM_API_KEY", "BIGMODEL_API_KEY", "ZHIPUAI_API_KEY", "ZHIPU_API_KEY"]
    ]

    // Extra non-credential inputs accepted by the audited readers. Old discovery
    // toggles, filesystem paths and browser-session settings are never forwarded.
    static let options: [String: Set<String>] = [
        "chutes": ["CHUTES_API_URL"], "clawrouter": ["CLAWROUTER_BASE_URL"],
        "deepgram": ["DEEPGRAM_API_URL", "DEEPGRAM_PROJECT_ID"],
        "fireworks": ["FIREWORKS_ACCOUNT_SLUG"],
        "litellm": ["LITELLM_BASE_URL"], "llmproxy": ["LLM_PROXY_BASE_URL"],
        "minimax": ["MINIMAX_HOST"], "moonshot": ["MOONSHOT_REGION"],
        "neuralwatt": ["NEURALWATT_API_URL"], "openrouter": ["OPENROUTER_API_URL"],
        "xai": ["XAI_TEAM_ID"], "zai": ["Z_AI_API_HOST"]
    ]

    static let unavailable = "This provider has no verified CodeRim sign-in or API-key usage source in this build."

    static func route(for descriptor: ProviderDescriptor) -> SignInRoute {
        let name = descriptor.metadata.displayName
        switch descriptor.id {
        case .copilot:
            return .guided(.init(name: "GitHub", action: .inApp(.githubDevice),
                                 note: "Approve the code with GitHub to connect this account to CodeRim."))
        default:
            guard api[descriptor.id.rawValue] != nil || descriptor.id == .ollama else { return .guidance(unavailable) }
            return .guided(.init(name: name, action: .settings,
                                 note: "Enter your \(name) API key in CodeRim Settings to read usage."))
        }
    }

    /// Start with app-owned saved fields. Old browser-import preferences cannot grant access.
    static func environment(for config: ExtendedProviderConfiguration,
                            descriptor: ProviderDescriptor) -> [String: String]? {
        guard let mode = api[descriptor.id.rawValue] else { return nil }
        let keys = [mode.key] + (keyAliases[descriptor.id.rawValue] ?? [])
        let key = ([config.provider.apiKey] + keys.map { config.environment[$0] }).compactMap {
            $0?.trimmingCharacters(in: .whitespacesAndNewlines)
        }.first(where: { !$0.isEmpty })
        guard let key else { return nil }
        let permitted = options[descriptor.id.rawValue] ?? []
        var environment = config.environment.filter { permitted.contains($0.key) && !$0.value.isEmpty }
        environment[mode.key] = key
        return environment
    }

    static func fetch(_ descriptor: ProviderDescriptor, context: ProviderFetchContext) async throws -> ProviderFetchResult {
        if descriptor.id == .xai || descriptor.id == .poe {
            return try await SharedScriptProvider.fetch(descriptor.id, environment: context.env)
        }
        if descriptor.id == .fireworks {
            // The existing CodeRim Fireworks API reader never writes the upstream tool's config.
            return try await ExtendedNotchProvider.fetchUpstream(descriptor, context: context)
        }
        let id = api[descriptor.id.rawValue]?.strategy
        guard let id, let strategy = await descriptor.fetchPlan.pipeline.resolveStrategies(context)
            .first(where: { $0.id == id && $0.kind == .apiToken }) else { throw NotchProviderError.needsAuth }
        return try await strategy.fetch(context)
    }

    static func copilotCredentials() throws -> GitHubCopilotCredentials {
        let descriptor = ProviderDescriptorRegistry.descriptor(for: .copilot)
        let config: ExtendedProviderConfiguration
        do {
            config = try ExtendedProviderConfigurationStore.load(descriptor,
                interactive: ProviderInteractionContext.current == .userInitiated)
        } catch let error as ExtendedProviderConfigurationStore.StoreError
            where [errSecInteractionNotAllowed, errSecAuthFailed, errSecUserCanceled].contains(error.status) {
            throw NotchProviderError.accessDenied
        }
        guard let key = config.provider.apiKey?.trimmingCharacters(in: .whitespacesAndNewlines),
              !key.isEmpty else { throw NotchProviderError.needsAuth }
        return GitHubCopilotCredentials(token: key, username: nil, source: "CodeRim GitHub sign-in")
    }
}
#endif
