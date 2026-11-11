import Foundation
import CodexBarCore

struct ExtendedProviderConfiguration: Codable, Sendable {
    var provider: ProviderConfig
    var environment: [String: String] = [:]
    var allowBillableRequests = false

    init(providerID: CodexBarCore.UsageProvider) {
        // Manual + empty means no browser import until explicitly enabled in this provider's settings.
        provider = ProviderConfig(id: providerID.instanceID, source: .auto, cookieSource: providerID == .stepfun ? .auto : .manual)
    }

    func environmentInput(_ value: String, for key: String) -> String {
        if provider.id.firstPartyProvider == .stepfun, key == "STEPFUN_PASSWORD" { return value }
        return value.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func fetchEnvironment(base: [String: String]) -> [String: String] {
        guard let id = provider.id.firstPartyProvider else { return base }
        let allowed = Set(ExtendedProviderGuides.all[id.rawValue]?.environmentKeys ?? [])
        var scoped = base
        for (key, value) in environment where allowed.contains(key) && !value.isEmpty { scoped[key] = value }
        if id == .deepseek, let token = provider.sanitizedCookieHeader {
            scoped["DEEPSEEK_PLATFORM_TOKEN"] = token
        }
        if id == .groq, let session = GroqConsoleSession.session(fromCookieHeader: provider.cookieHeader) {
            scoped[GroqConsoleSession.sessionEnvironmentKey] = session.directJWT
            scoped[GroqConsoleSession.sessionTokenEnvironmentKey] = session.sessionToken
        }
        return ProviderConfigEnvironment.applyProviderConfigOverrides(base: scoped, provider: id, config: provider)
    }

    func sourceMode(environment: [String: String]) -> ProviderSourceMode {
        if provider.id.firstPartyProvider == .minimax, provider.cookieSource != .auto {
            // Upstream's web strategy otherwise imports browser state on explicit refresh,
            // even with cookieSource off. Only let it run with a valid manual override.
            let manual = settings(environment: environment)?.minimax?.manualCookieHeader
            return provider.source == .api || MiniMaxCookieHeader.override(from: manual) == nil ? .api : .web
        }
        if provider.id.firstPartyProvider == .deepseek, provider.cookieSource != .auto {
            return DeepSeekSettingsReader.platformToken(environment: environment) == nil ? .api : (provider.source == .api ? .api : .web)
        }
        if provider.id.firstPartyProvider == .groq, provider.cookieSource != .auto,
           environment[GroqConsoleSession.sessionEnvironmentKey]?.isEmpty != false,
           environment[GroqConsoleSession.sessionTokenEnvironmentKey]?.isEmpty != false {
            return .api
        }
        return provider.source ?? .auto
    }

    var settings: ProviderSettingsSnapshot? { settings(environment: fetchEnvironment(base: [:])) }

    func settings(environment: [String: String]) -> ProviderSettingsSnapshot? {
        var provider = self.provider
        let cookieKeys: [CodexBarCore.UsageProvider: [String]] = [
            .alibaba: ["ALIBABA_CODING_PLAN_COOKIE"],
            .devin: ["DEVIN_BEARER_TOKEN", "DEVIN_AUTHORIZATION"],
            .longcat: ["LONGCAT_MANUAL_COOKIE", "longcat_manual_cookie"],
            .kimi: ["KIMI_MANUAL_COOKIE", "KIMI_AUTH_TOKEN"],
            .perplexity: ["PERPLEXITY_COOKIE", "PERPLEXITY_SESSION_TOKEN"],
            .manus: ["MANUS_COOKIE", "MANUS_SESSION_TOKEN"],
            .qwencloud: ["QWEN_CLOUD_COOKIE"],
            .minimax: ["MINIMAX_COOKIE", "MINIMAX_COOKIE_HEADER"],
        ]
        if let id = provider.id.firstPartyProvider, provider.sanitizedCookieHeader == nil,
           let header = cookieKeys[id]?.compactMap({ environment[$0] }).first(where: { !$0.isEmpty }) {
            provider.cookieHeader = header
            provider.cookieSource = .manual
        }
        if provider.cookieSource == .manual, CookieHeaderNormalizer.normalize(provider.cookieHeader) == nil,
           provider.id.firstPartyProvider != .stepfun {
            // Some upstream probes interpret an empty manual header as permission to import.
            provider.cookieSource = .off
        }
        if provider.id.firstPartyProvider == .kimi, provider.cookieSource != .auto,
           KimiCookieHeader.override(from: provider.cookieHeader) == nil {
            provider.cookieSource = .off
        }
        if provider.id.firstPartyProvider == .stepfun {
            return .make(stepfun: .init(cookieSource: provider.cookieSource ?? .auto,
                manualToken: provider.cookieHeader ?? "",
                username: StepFunSettingsReader.token(environment: environment) == nil ? StepFunSettingsReader.username(environment: environment) ?? "" : "",
                password: StepFunSettingsReader.token(environment: environment) == nil ? environment["STEPFUN_PASSWORD"] ?? "" : ""))
        }
        if provider.id.firstPartyProvider == .jetbrains {
            return .make(jetbrains: .init(ideBasePath: environment["IDE_BASE"]))
        }
        if provider.id.firstPartyProvider == .windsurf {
            return .make(windsurf: .init(usageDataSource: WindsurfUsageDataSource(rawValue: provider.source?.rawValue ?? "auto") ?? .auto,
                cookieSource: provider.cookieSource ?? .manual, manualCookieHeader: provider.cookieHeader))
        }
        guard let id = provider.id.firstPartyProvider,
              let contribution = ProviderDescriptorRegistry.descriptor(for: id).settingsSection
                .credentialContribution(context: ProviderCredentialSettingsContext(config: provider, account: nil))
        else { return nil }
        return ProviderSettingsSnapshot(contributions: [contribution])
    }

    static func billableNotice(for id: CodexBarCore.UsageProvider) -> String? {
        switch id {
        case .bedrock:
            "AWS charges for Cost Explorer requests; CloudWatch requests may also be billed. Each refresh can make multiple requests."
        case .azureopenai:
            "Validating this deployment sends a minimal model request that may be billed. This provider reports connection status, not an account quota."
        case .doubao:
            "Some API authentication paths send a minimal model request to inspect response limits and may be billed."
        default: nil
        }
    }
}
