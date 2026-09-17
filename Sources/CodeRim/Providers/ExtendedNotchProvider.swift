import Foundation
import Security
#if canImport(FoundationXML)
import FoundationXML
#endif
import CodexBarCore

@MainActor
final class ExtendedNotchProvider: NotchProvider {
    typealias Fetch = @Sendable (ProviderDescriptor, ProviderFetchContext) async throws -> ProviderFetchResult
    let descriptor: ProviderDescriptor
    private let fetch: Fetch
    private let configuration: () throws -> ExtendedProviderConfiguration
    private var currentAccount: ProviderAccount?
    private var revision = UUID()
    static let cacheService = "dev.codexmeter.provider-cache"

    init(descriptor: ProviderDescriptor,
         configuration: (() throws -> ExtendedProviderConfiguration)? = nil,
         fetch: @escaping Fetch = { descriptor, context in try await ExtendedNotchProvider.fetchUpstream(descriptor, context: context) }) {
        self.descriptor = descriptor
        self.configuration = configuration ?? { try ExtendedProviderConfigurationStore.load(descriptor, interactive: ProviderInteractionContext.current == .userInitiated) }
        self.fetch = fetch
    }

    var id: String { ExtendedProviderCatalog.localID(descriptor.id) }
    var displayName: String { descriptor.metadata.displayName }
    var glyph: ProviderGlyph { NotchProviderCatalog.glyph(for: id) }
    var isVisibleWhenAbsent: Bool { true }
    var signInRoute: SignInRoute { .guidance("Configure \(displayName) in its provider settings. " + (ExtendedProviderCatalog.guide(for: id)?.summary ?? "")) }
    func account() -> ProviderAccount? { currentAccount }
    func forgetCachedCredential() {
        revision = UUID()
        currentAccount = nil
        // The dependency's default cache belongs to CodexBar, not this app.
        KeychainCacheStore.withServiceOverrideForTesting(Self.cacheService) {
            _ = CookieHeaderCache.clearAllScopes(provider: descriptor.id)
        }
    }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        let version = revision
        let config: ExtendedProviderConfiguration
        do { config = try configuration() }
        catch let error as ExtendedProviderConfigurationStore.StoreError {
            if error.status == errSecInteractionNotAllowed || error.status == errSecAuthFailed || error.status == errSecUserCanceled {
                throw NotchProviderError.accessDenied
            }
            return empty(.error("Provider settings could not be loaded from Keychain. Open provider settings and try again."))
        } catch {
            return empty(.error("Provider settings could not be decoded. Open provider settings and save them again."))
        }
        if let notice = ExtendedProviderConfiguration.billableNotice(for: descriptor.id), !config.allowBillableRequests {
            return empty(.unsupported("Enable billed requests in this provider's settings. " + notice))
        }
        let environment = config.fetchEnvironment(base: ProcessInfo.processInfo.environment)
        // Upstream discovery persists to CodexBar's config. Require an explicit account here.
        if descriptor.id == .fireworks, environment["FIREWORKS_ACCOUNT_SLUG"]?.isEmpty != false {
            return empty(.unsupported("Set FIREWORKS_ACCOUNT_SLUG in Additional provider settings, then save."))
        }
        let browser = BrowserDetection()
        let context = ProviderFetchContext(runtime: .app, sourceMode: config.sourceMode(environment: environment),
            includeCredits: true, includeOptionalUsage: descriptor.id != .deepseek || config.provider.cookieSource == .auto, webTimeout: 20, webDebugDumpHTML: false, verbose: false,
            env: environment, settings: config.settings(environment: environment), fetcher: UsageFetcher(environment: environment),
            claudeFetcher: ClaudeUsageFetcher(browserDetection: browser, environment: environment),
            browserDetection: browser,
            providerManualTokenUpdater: { [weak self] provider, token in
                await self?.persistRecoveredToken(token, provider: provider, version: version)
            })
        do {
            let result = try await KeychainCacheStore.withServiceOverrideForTesting(Self.cacheService) {
                try await fetch(descriptor, context)
            }
            try Task.checkCancellation()
            guard revision == version else { throw CancellationError() }
            currentAccount = ProviderAccount(label: result.usage.accountEmail(for: descriptor.id),
                plan: result.usage.loginMethod(for: descriptor.id), source: result.sourceLabel,
                manageURL: descriptor.metadata.dashboardURL.flatMap(URL.init(string:)))
            return Self.snapshot(result, descriptor: descriptor)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            guard revision == version else { throw CancellationError() }
            try Task.checkCancellation()
            // Only typed failures establish authentication or permission state.
            if let status = ExtendedProviderFailureClassifier.status(for: error, provider: displayName) {
                return empty(status)
            }
            // Some upstream native errors discard the HTTP status into arbitrary text.
            // Never keep an old successful quota after a failure that may be a revoked key.
            return empty(.error(Self.safeError(error, provider: displayName)))
        }
    }

    nonisolated static func fetchUpstream(_ descriptor: ProviderDescriptor, context: ProviderFetchContext) async throws -> ProviderFetchResult {
        if descriptor.id == .jetbrains {
            let local = try Self.validatedJetBrainsQuota(settings: context.settings)
            // An IDE without an activated quota writes {type: "Unknown"}. The upstream
            // percentage helper turns its missing maximum into 0% used / 100% left.
            guard local.quotaInfo.maximum.isFinite, local.quotaInfo.maximum > 0,
                  local.quotaInfo.used.isFinite else {
                throw ProviderFetchClassifiedError(kind: .missingCredential,
                    message: "JetBrains AI has not reported a valid quota. Sign in and activate AI in the IDE.")
            }
            return ProviderFetchResult(usage: try local.toUsageSnapshot(), credits: nil, dashboard: nil,
                sourceLabel: "local", strategyID: "jetbrains.local", strategyKind: .localProbe)
        }
        guard descriptor.id == .fireworks else { return try await descriptor.fetch(context: context) }
        // The registry strategy writes discovered slugs into CodexBar's config, including
        // after a configured slug returns 404. Use its public read-only fetcher instead.
        guard let key = FireworksSettingsReader.apiKey(environment: context.env) else {
            throw FireworksUsageError.missingCredentials
        }
        let usage = try await FireworksUsageFetcher.fetchUsage(apiKey: key,
            accountSlug: FireworksSettingsReader.accountSlug(environment: context.env))
        return ProviderFetchResult(usage: usage.toUsageSnapshot(), credits: nil, dashboard: nil,
            sourceLabel: "api · " + usage.accountSlug, strategyID: "fireworks.api", strategyKind: .apiToken)
    }

    nonisolated private static func validatedJetBrainsQuota(settings: ProviderSettingsSnapshot?) throws -> JetBrainsStatusSnapshot {
        let custom = settings?.jetbrainsIDEBasePath?.trimmingCharacters(in: .whitespacesAndNewlines)
        let ide = custom?.isEmpty != false ? JetBrainsIDEDetector.detectLatestIDE() : nil
        let path = custom.flatMap { $0.isEmpty ? nil : JetBrainsIDEDetector.quotaFilePath(for: ($0 as NSString).expandingTildeInPath) }
            ?? ide?.quotaFilePath
        guard let path else { throw JetBrainsStatusProbeError.noIDEDetected }
        let data = try Data(contentsOf: URL(fileURLWithPath: path))
        let document = try XMLDocument(data: data, options: .nodeLoadExternalEntitiesNever)
        let raw = try document.nodes(forXPath: "//component[@name='AIAssistantQuotaManager2']/option[@name='quotaInfo']/@value").first?.stringValue
        let object = raw.flatMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any] }
        // Validate the original fields before the upstream parser defaults missing values to zero.
        guard let current = object?["current"] as? String, let used = Double(current), used.isFinite, used >= 0,
              let maximum = object?["maximum"] as? String, let total = Double(maximum), total.isFinite, total > 0 else {
            throw ProviderFetchClassifiedError(kind: .missingCredential,
                message: "JetBrains AI has not reported a valid quota. Sign in and activate AI in the IDE.")
        }
        return try JetBrainsStatusProbe.parseXMLData(data, detectedIDE: ide)
    }

    private func persistRecoveredToken(_ token: String, provider: CodexBarCore.UsageProvider, version: UUID) {
        guard revision == version, provider == descriptor.id,
              var value = try? configuration() else { return }
        value.provider.cookieHeader = token
        value.provider.cookieSource = .manual
        // Failure to persist never turns a successful fetch into a fabricated saved configuration.
        try? ExtendedProviderConfigurationStore.save(value)
    }

    private func empty(_ status: ProviderStatus) -> ProviderSnapshot {
        currentAccount = nil
        return ProviderSnapshot(id: id, displayName: displayName, glyph: glyph, fidelity: .official, status: status, windows: [])
    }

    static func safeError(_ error: Error, provider: String) -> String {
        if error is ProviderFetchError { return "\(provider) needs a configured account or supported local tool. Open provider settings for connection instructions." }
        if let classified = error as? ProviderFetchClassifiedError {
            return "\(provider): \(classified.kind.rawValue). Check the account and connection settings, then refresh."
        }
        let value = error as NSError
        if value.domain == NSURLErrorDomain { return "\(provider) could not be reached (network error \(value.code)). Try refreshing." }
        return "\(provider) could not return usage. Check the configured credentials, required plan, and provider documentation."
    }

    /// Native percentage renderers use integer text. Keep overflow and invalid ratios textual.
    private static func displayableFraction(_ value: Double) -> Double? {
        guard value.isFinite, value >= 0, value * 100 < Double(Int.max) / 2 else { return nil }
        return value
    }

    static func snapshot(_ result: ProviderFetchResult, descriptor: ProviderDescriptor) -> ProviderSnapshot {
        let usage = result.usage
        var windows: [LimitWindow] = []
        func append(_ window: RateWindow?, id: String, label: String, known: Bool = true) {
            guard let window, !window.isSyntheticPlaceholder else { return }
            let fraction = known && window.usedPercent.isFinite && window.usedPercent >= 0 && window.usedPercent < Double(Int.max) / 2 ? window.usedPercent / 100 : nil
            guard fraction != nil || window.resetsAt != nil else { return }
            windows.append(LimitWindow(id: id, label: label, usedFraction: fraction,
                resetsAt: window.resetsAt, duration: window.windowMinutes.map { Double($0) * 60 }))
        }
        let labels = descriptor.presentation.rateWindowLabels(metadata: descriptor.metadata, snapshot: usage)
        // Balance-only providers sometimes synthesize a full/empty primary lane. Never turn that into a quota.
        let bobSummary = usage.primary?.resetDescription ?? ""
        let bobUnknownLimit = descriptor.id == .ibmbob
            && (!bobSummary.contains(" / ")
                || bobSummary.range(of: #"/\s*0(?:\.0+)?\s+Bobcoins"#, options: .regularExpression) != nil)
        // Upstream's ZoomMate RateWindow collapses unlimited/missing ceilings to zero.
        // Without a reset or a nonzero fraction, retain an unknown quota rather than 100%.
        let zoomUnknownLimit = descriptor.id == .zoommate
            && usage.primary?.usedPercent == 0 && usage.primary?.resetsAt == nil
        let textOnly = bobUnknownLimit || zoomUnknownLimit || descriptor.metadata.balanceOnly || descriptor.id == .azureopenai || descriptor.id == .groq
            || (descriptor.id == .crof && usage.secondary == nil)
            || (descriptor.id == .venice && usage.primary?.resetDescription?.contains("epoch allocation") != true)
        if textOnly {
            for (index, window) in [usage.primary, usage.secondary, usage.tertiary].enumerated() {
                if let text = window?.resetDescription, !text.isEmpty {
                    windows.append(LimitWindow(id: "reported-\(index)",
                        label: [labels.primary, labels.secondary, labels.tertiary][index],
                        displayValue: zoomUnknownLimit && text == "Credits" ? "Quota not reported" : text))
                }
            }
        } else {
            append(usage.primary, id: "primary", label: labels.primary)
            if descriptor.id == .crof, let text = usage.secondary?.resetDescription {
                windows.append(LimitWindow(id: "secondary", label: labels.secondary, displayValue: text))
            } else {
                append(usage.secondary, id: "secondary", label: labels.secondary)
            }
            append(usage.tertiary, id: "tertiary", label: labels.tertiary)
        }
        for (index, extra) in descriptor.presentation.extraRateWindows(snapshot: usage).enumerated() {
            append(extra.window, id: "extra-\(index)-\(extra.id)", label: extra.title, known: extra.usageKnown)
        }
        if let cost = usage.providerCost, cost.used.isFinite, cost.limit.isFinite {
            let fraction = cost.limit > 0 ? Self.displayableFraction(cost.used / cost.limit) : nil
            let amount = cost.used.formatted(.number.precision(.fractionLength(0...4))) + " " + cost.currencyCode
            let ceiling = cost.limit.formatted(.number.precision(.fractionLength(0...4))) + " " + cost.currencyCode
            windows.append(LimitWindow(id: "spend", label: cost.period ?? "Reported usage",
                usedFraction: fraction, resetsAt: cost.resetsAt, displayValue: fraction == nil ? amount : "\(amount) / \(ceiling)"))
            if let balance = cost.balance, balance.isFinite {
                windows.append(LimitWindow(id: "balance", label: "Balance",
                    displayValue: balance.formatted(.number.precision(.fractionLength(0...4))) + " " + cost.currencyCode))
            }
        }
        if let credits = result.credits, credits.balanceReadSucceeded, credits.remaining.isFinite {
            windows.append(LimitWindow(id: "credits", label: "Credits remaining",
                displayValue: credits.remaining.formatted(.number.precision(.fractionLength(0...4)))))
        }
        for (sectionIndex, section) in usage.details.enumerated() {
            for (rowIndex, row) in section.rows.enumerated() {
                let ratio = row.progress.flatMap { Self.displayableFraction($0.used / $0.total) }
                windows.append(LimitWindow(id: "detail-\(sectionIndex)-\(rowIndex)", group: section.title,
                    label: row.label, usedFraction: ratio,
                    displayValue: [row.value, row.secondaryValue].compactMap { $0 }.joined(separator: " · ")))
            }
        }
        let id = ExtendedProviderCatalog.localID(descriptor.id)
        let headline = windows.first(where: { $0.usedFraction != nil })?.id ?? windows.first?.id
        return ProviderSnapshot(id: id, displayName: descriptor.metadata.displayName,
            glyph: NotchProviderCatalog.glyph(for: id),
            fidelity: usage.dataConfidence == .estimated ? .derived : .official,
            status: .ok,
            windows: windows, headlineID: headline, accountPlan: usage.loginMethod(for: descriptor.id))
    }
}

struct ExtendedProviderFetchFailure: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}
