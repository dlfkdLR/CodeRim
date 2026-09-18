import Foundation
import Security
import LocalAuthentication
import CodexBarCore

struct ExtendedProviderConfiguration: Codable, Sendable {
    var provider: ProviderConfig
    var environment: [String: String] = [:]
    var allowBillableRequests = false

    init(providerID: CodexBarCore.UsageProvider) {
        // Manual + empty means no browser import until explicitly enabled in this provider's settings.
        provider = ProviderConfig(id: providerID.instanceID, source: .auto, cookieSource: providerID == .stepfun ? .auto : .manual)
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

/// A separate, exact Keychain item per provider. No secrets in UserDefaults, logs or CodexBar's config.
enum ExtendedProviderConfigurationStore {
    /// The shipping bundle identifier, pinned rather than read.
    ///
    /// A Keychain service name is an address, not a version stamp: every saved
    /// provider credential lives under it, and a service name derived from
    /// `Bundle.main.bundleIdentifier` moves the moment the identifier does —
    /// silently, taking every configured provider with it, with no error and
    /// nothing to migrate back from. The rename to CodeRim already kept this
    /// identifier for exactly that reason; pinning it here means a *future*
    /// rename cannot undo that decision by accident.
    ///
    /// `KeychainAccountVault` has always pinned its service string
    /// (`com.hecholp.codexmeter.saved-codex-accounts.v1`), which is why saved
    /// accounts survived the rename while these would not have. This is the
    /// same rule applied to the same kind of data.
    ///
    /// Unconditional, deliberately: a build with a different identifier is
    /// still this app on this Mac for this user, and letting it read the
    /// providers the user configured is not a boundary worth drawing. macOS
    /// draws the real one — the Keychain item's own ACL prompts when a
    /// differently-signed binary asks for it.
    static let service = "dev.codexmeter.CodexMeter.extended-providers"

    private static func query(_ id: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: id, kSecAttrSynchronizable as String: false]
    }

    static func load(_ descriptor: ProviderDescriptor, interactive: Bool = false) throws -> ExtendedProviderConfiguration {
        var request = query(descriptor.id.rawValue)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        if !interactive {
            let context = LAContext()
            context.interactionNotAllowed = true
            request[kSecUseAuthenticationContext as String] = context
        }
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return .init(providerID: descriptor.id) }
        guard status == errSecSuccess, let data = result as? Data else { throw StoreError(status: status) }
        let value = try JSONDecoder().decode(ExtendedProviderConfiguration.self, from: data)
        guard value.provider.id == descriptor.id.instanceID else { throw StoreError(status: errSecDecode) }
        return value
    }

    static func save(_ value: ExtendedProviderConfiguration) throws {
        let data = try JSONEncoder().encode(value)
        let request = query(value.provider.id.rawValue)
        let values = [kSecValueData as String: data]
        var status = SecItemUpdate(request as CFDictionary, values as CFDictionary)
        if status == errSecItemNotFound {
            status = SecItemAdd(request.merging(values) { _, new in new } as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw StoreError(status: status) }
    }

    struct StoreError: LocalizedError {
        let status: OSStatus
        var errorDescription: String? { "Provider settings could not be accessed in Keychain (\(status))." }
    }
}
