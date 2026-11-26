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
        "alibaba": API("alibaba-coding-plan.api", "ALIBABA_CODING_PLAN_API_KEY"),
        "amp": API("amp.api", "AMP_API_KEY"),
        "azureopenai": API("azureopenai.api", "AZURE_OPENAI_API_KEY"),
        "bedrock": API("bedrock.api", "AWS_ACCESS_KEY_ID"),
        "chutes": API("chutes.api", "CHUTES_API_KEY"),
        "clawrouter": API("clawrouter.js", "CLAWROUTER_API_KEY"),
        "clinepass": API("clinepass.js", "CLINE_API_KEY"),
        "codebuff": API("codebuff.api", "CODEBUFF_API_KEY"),
        "crof": API("crof.js", "CROFAI_API_KEY"),
        "deepgram": API("deepgram.js", "DEEPGRAM_API_KEY"),
        "deepinfra": API("deepinfra.api", "DEEPINFRA_API_KEY"),
        "deepseek": API("deepseek.api", "DEEPSEEK_API_KEY"),
        "doubao": API("doubao.api", "ARK_API_KEY"),
        "elevenlabs": API("elevenlabs.api", "ELEVENLABS_API_KEY"),
        "factory": API("factory.api", "FACTORY_API_KEY"),
        "fireworks": API("fireworks.api", "FIREWORKS_API_KEY"),
        "groq": API("groq.api", "GROQ_API_KEY"),
        "ibmbob": API("ibmbob.api", "BOBSHELL_API_KEY"),
        "kilo": API("kilo.api", "KILO_API_KEY"),
        "kimi": API("kimi.api", "KIMI_CODE_API_KEY"),
        "litellm": API("litellm.api", "LITELLM_API_KEY"),
        "llmproxy": API("llmproxy.api", "LLM_PROXY_API_KEY"),
        "minimax": API("minimax.api", "MINIMAX_API_KEY"),
        "moonshot": API("moonshot.api", "MOONSHOT_API_KEY"),
        "neuralwatt": API("neuralwatt.api", "NEURALWATT_API_KEY"),
        "openai": API("openai.api.balance", "OPENAI_ADMIN_KEY"),
        "opencodego": API("opencodego.api", "OPENCODE_API_KEY"),
        "openrouter": API("openrouter.js", "OPENROUTER_API_KEY"),
        "poe": API("poe.shared-script", "POE_API_KEY"),
        "sub2api": API("sub2api.js", "SUB2API_API_KEY"),
        "synthetic": API("synthetic.js", "SYNTHETIC_API_KEY"),
        "venice": API("venice.js", "VENICE_API_KEY"),
        "warp": API("warp.api", "WARP_API_KEY"),
        "xai": API("xai.shared-script", "XAI_MANAGEMENT_API_KEY"),
        "zai": API("zai.js", "Z_AI_API_KEY"),
        "zenmux": API("zenmux.api", "ZENMUX_MANAGEMENT_API_KEY"),
    ]

    // Compatibility with API keys previously saved in this provider's CodeRim configuration.
    static let keyAliases: [String: [String]] = [
        "alibaba": ["DASHSCOPE_API_KEY", "ALIBABA_QWEN_API_KEY"], "warp": ["WARP_TOKEN"],
        "doubao": ["DOUBAO_API_KEY", "VOLCENGINE_API_KEY"],
        "crof": ["CROF_API_KEY"], "deepinfra": ["DEEPINFRA_TOKEN"], "deepseek": ["DEEPSEEK_KEY"],
        "elevenlabs": ["XI_API_KEY"], "fireworks": ["FIREWORKS_KEY"], "minimax": ["MINIMAX_CODING_API_KEY"],
        "moonshot": ["MOONSHOT_KEY"], "venice": ["VENICE_KEY"],
        "zai": ["GLM_API_KEY", "BIGMODEL_API_KEY", "ZHIPUAI_API_KEY", "ZHIPU_API_KEY"]
    ]

    static func officialCLI(_ id: CodexBarCore.UsageProvider) -> String? {
        switch id {
        case .augment: "augment.cli"
        case .alibabatokenplan: "alibaba-token-plan.cli"
        default: nil
        }
    }

    static func supports(_ id: CodexBarCore.UsageProvider) -> Bool {
        api[id.rawValue] != nil || id == .wayfinder || officialCLI(id) != nil
    }

    // Extra non-credential inputs accepted by the audited readers. Old discovery
    // toggles, filesystem paths and browser-session settings are never forwarded.
    static let options: [String: Set<String>] = [
        "azureopenai": ["AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_DEPLOYMENT_NAME", "AZURE_OPENAI_API_VERSION"],
        "bedrock": ["AWS_REGION", "CODEXBAR_BEDROCK_BUDGET"], "codebuff": ["CODEBUFF_API_URL"],
        "openai": ["OPENAI_PROJECT_ID"], "groq": ["GROQ_API_URL"], "kimi": ["KIMI_CODE_BASE_URL"],
        "sub2api": ["SUB2API_BASE_URL"], "wayfinder": ["WAYFINDER_GATEWAY_URL"],
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
        case .alibabatokenplan:
            return .guidance("Sign in with the official Bailian CLI. CodeRim reads its Token Plan usage command.")
        case .augment:
            return .guidance("Sign in with the official Augment CLI. CodeRim reads its account status.")
        case .wayfinder:
            return .guidance("Start the Wayfinder gateway. You can set its address in CodeRim Settings.")
        case .copilot:
            return .guided(.init(name: "GitHub", action: .inApp(.githubDevice),
                                 note: "Approve the code with GitHub to connect this account to CodeRim."))
        default:
            guard api[descriptor.id.rawValue] != nil || descriptor.id == .ollama else { return .guidance(unavailable) }
            return .guided(.init(name: name, action: .settings,
                                 note: "Enter your \(name) API key in CodeRim Settings to read usage."))
        }
    }

    private static func primaryKeyNames(_ id: CodexBarCore.UsageProvider) -> [String] {
        guard let mode = api[id.rawValue] else { return [] }
        return [mode.key] + (keyAliases[id.rawValue] ?? [])
            + (id == .doubao ? DoubaoSettingsReader.accessKeyIDEnvironmentKeys : [])
    }

    static func savedAPIKey(_ config: ExtendedProviderConfiguration, for id: CodexBarCore.UsageProvider) -> String? {
        let names = primaryKeyNames(id)
        return ([config.provider.apiKey] + names.map { config.environment[$0] } + names.map { config.provider.pluginSecrets?[$0] })
            .compactMap { $0?.trimmingCharacters(in: .whitespacesAndNewlines) }.first { !$0.isEmpty }
    }

    static func setAPIKey(_ value: String, in config: inout ExtendedProviderConfiguration, for id: CodexBarCore.UsageProvider) {
        let key = value.trimmingCharacters(in: .whitespacesAndNewlines)
        config.provider.apiKey = key.isEmpty ? nil : key
        for name in primaryKeyNames(id) {
            config.environment.removeValue(forKey: name)
            config.provider.pluginSecrets?.removeValue(forKey: name)
        }
    }

    /// Earlier Settings releases stored these same user inputs as typed provider fields.
    static func savedOption(_ key: String, in config: ExtendedProviderConfiguration) -> String? {
        if let value = config.environment[key] { return value }
        if key == "OPENROUTER_MANAGEMENT_API_KEY" { return config.provider.pluginSecrets?[key] }
        if key == "AWS_REGION" { return config.provider.region }
        return switch key {
        case "AZURE_OPENAI_ENDPOINT", "LITELLM_BASE_URL", "LLM_PROXY_BASE_URL", "SUB2API_BASE_URL":
            config.provider.enterpriseHost
        case "AZURE_OPENAI_DEPLOYMENT_NAME", "DEEPGRAM_PROJECT_ID", "OPENAI_PROJECT_ID", "XAI_TEAM_ID":
            config.provider.workspaceID
        default: nil
        }
    }

    /// Start with app-owned saved fields. Old browser-import preferences cannot grant access.
    static func environment(for config: ExtendedProviderConfiguration,
                            descriptor: ProviderDescriptor) -> [String: String]? {
        let id = descriptor.id
        if officialCLI(id) != nil { return [:] }
        let permitted = options[id.rawValue] ?? []
        var environment: [String: String] = [:]
        for name in permitted {
            if let value = savedOption(name, in: config), !value.isEmpty { environment[name] = value }
        }
        if id == .wayfinder { return environment }
        guard let mode = api[id.rawValue] else { return nil }
        let key = savedAPIKey(config, for: id)
        if id == .openai, key == nil, let standard = config.environment["OPENAI_API_KEY"]?
            .trimmingCharacters(in: .whitespacesAndNewlines), !standard.isEmpty {
            environment["OPENAI_API_KEY"] = standard
            return environment
        }
        if id == .doubao {
            // Reuse its typed Ark/Volcengine key distinction with only CodeRim-owned inputs.
            let keys = DoubaoSettingsReader.apiKeyEnvironmentKeys + DoubaoSettingsReader.accessKeyIDEnvironmentKeys
                + DoubaoSettingsReader.secretAccessKeyEnvironmentKeys + DoubaoSettingsReader.regionEnvironmentKeys
            let saved = config.environment.filter { keys.contains($0.key) }
            let scoped = descriptor.credentials?.applyConfig(base: saved, config: config.provider) ?? [:]
            guard DoubaoSettingsReader.apiKey(environment: scoped) != nil
                    || DoubaoSettingsReader.codingPlanCredentials(environment: scoped) != nil else { return nil }
            // Return canonical keys for stable owner fingerprints across old aliases.
            if let signed = DoubaoSettingsReader.codingPlanCredentials(environment: scoped) {
                return [DoubaoSettingsReader.accessKeyIDEnvironmentKeys[0]: signed.accessKeyID,
                        DoubaoSettingsReader.secretAccessKeyEnvironmentKeys[0]: signed.secretAccessKey,
                        DoubaoSettingsReader.regionEnvironmentKeys[0]: signed.region]
            }
            return DoubaoSettingsReader.apiKey(environment: scoped).map { [mode.key: $0] }
        }
        guard let key else { return nil }
        if id == .openrouter, let management = config.environment["OPENROUTER_MANAGEMENT_API_KEY"]
            ?? config.provider.pluginSecrets?["OPENROUTER_MANAGEMENT_API_KEY"], !management.isEmpty {
            environment["OPENROUTER_MANAGEMENT_API_KEY"] = management
        }
        if id == .bedrock {
            guard let secret = (config.provider.secretKey ?? config.environment["AWS_SECRET_ACCESS_KEY"])?
                .trimmingCharacters(in: .whitespacesAndNewlines), !secret.isEmpty else { return nil }
            environment["AWS_SECRET_ACCESS_KEY"] = secret
            environment["CODEXBAR_BEDROCK_AUTH_MODE"] = "keys"
            if let token = config.environment["AWS_SESSION_TOKEN"], !token.isEmpty { environment["AWS_SESSION_TOKEN"] = token }
        }
        environment[mode.key] = key
        return environment
    }

    static func fetch(_ descriptor: ProviderDescriptor, context: ProviderFetchContext) async throws -> ProviderFetchResult {
        if let cli = officialCLI(descriptor.id) {
            return try await OfficialUsageCLI.fetch(descriptor, strategy: cli, context: context)
        }
        if descriptor.id == .xai || descriptor.id == .poe {
            return try await SharedScriptProvider.fetch(descriptor.id, environment: context.env)
        }
        if descriptor.id == .fireworks {
            // The existing CodeRim Fireworks API reader never writes the upstream tool's config.
            return try await ExtendedNotchProvider.fetchUpstream(descriptor, context: context)
        }
        if descriptor.id == .codebuff {
            guard let key = context.env["CODEBUFF_API_KEY"] else { throw NotchProviderError.needsAuth }
            let usage = try await CodebuffUsageFetcher.fetchUsage(apiKey: key, environment: context.env,
                                                                   includeSubscription: false)
            return ProviderFetchResult(usage: usage.toUsageSnapshot(), credits: nil, dashboard: nil,
                sourceLabel: "api", strategyID: "codebuff.api", strategyKind: .apiToken)
        }
        let id: String? = switch descriptor.id {
        case .wayfinder: "wayfinder.api"
        default: api[descriptor.id.rawValue]?.strategy
        }
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
