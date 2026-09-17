import AppKit
import SwiftUI
import XCTest
import CodexBarCore
@testable import CodeRim

@MainActor
final class ExtendedProviderTests: XCTestCase {
    private func result(_ usage: CodexBarCore.UsageSnapshot, credits: CreditsSnapshot? = nil) -> ProviderFetchResult {
        .init(usage: usage, credits: credits, dashboard: nil, sourceLabel: "fixture", strategyID: "fixture", strategyKind: .apiToken)
    }
    private func window(_ percent: Double, synthetic: Bool = false) -> RateWindow {
        .init(usedPercent: percent, windowMinutes: 300, resetsAt: Date(timeIntervalSince1970: 2_000_000_000),
              resetDescription: nil, isSyntheticPlaceholder: synthetic)
    }
    private func descriptor(_ id: CodexBarCore.UsageProvider) -> ProviderDescriptor {
        ProviderDescriptorRegistry.descriptor(for: id)
    }

    func testCatalogCoversEveryUpstreamProviderWithoutChangingLegacyIdentities() {
        let upstream = Set(CodexBarCore.UsageProvider.allCases)
        XCTAssertEqual(upstream.count, 69)
        XCTAssertEqual(Set(ExtendedProviderCatalog.all.map(\.id)), upstream)
        XCTAssertEqual(NotchProviderCatalog.all.count, 70)
        XCTAssertEqual(Set(NotchProviderCatalog.all.map(\.id)).count, 70)
        for id in upstream {
            XCTAssertTrue(NotchProviderCatalog.all.contains { $0.id == ExtendedProviderCatalog.localID(id) }, id.rawValue)
        }
        XCTAssertEqual(ExtendedProviderCatalog.localID(.antigravity), "gemini")
        XCTAssertEqual(ExtendedProviderCatalog.localID(.gemini), "gemini-cli")
        XCTAssertEqual(ExtendedProviderCatalog.localID(.opencodego), "opencode")
        XCTAssertEqual(ExtendedProviderCatalog.localID(.opencode), "opencode-zen")
        XCTAssertEqual(ExtendedProviderCatalog.localID(.zai), "glm")
        XCTAssertEqual(ExtendedProviderCatalog.makeProviders().count, 59)
    }

    func testEveryAddedProviderHasDocumentationSourceSettingsAndRuntimeAdapter() throws {
        for descriptor in ExtendedProviderCatalog.additions {
            let id = ExtendedProviderCatalog.localID(descriptor.id)
            let guide = try XCTUnwrap(ExtendedProviderCatalog.guide(for: id), id)
            XCTAssertFalse(guide.summary.isEmpty, id)
            XCTAssertEqual(guide.url.host, "github.com", id)
            XCTAssertTrue(guide.url.path.contains(ExtendedProviderCatalog.revision), id)
            XCTAssertFalse(descriptor.fetchPlan.sourceModes.isEmpty, id)
            XCTAssertEqual(ExtendedNotchProvider(descriptor: descriptor).id, id)
            let config = ExtendedProviderConfiguration(providerID: descriptor.id)
            let encoded = try JSONEncoder().encode(config)
            let decoded = try JSONDecoder().decode(ExtendedProviderConfiguration.self, from: encoded)
            XCTAssertEqual(decoded.provider.id, descriptor.id.instanceID, id)
            XCTAssertFalse(decoded.allowBillableRequests, id)
            if descriptor.id != .stepfun {
                XCTAssertEqual(config.provider.cookieSource, .manual, id)
            }
        }
    }

    func testAllExtendedAdaptersPreserveReportedTokenHistoryWithoutInventingDailyUsage() {
        for descriptor in ExtendedProviderCatalog.additions {
            let history = CostUsageTokenSnapshot(sessionTokens: 12, sessionCostUSD: nil,
                last30DaysTokens: 123456, last30DaysCostUSD: nil, historyDays: 14,
                historyLabel: "This billing period", daily: [], updatedAt: Date())
            let usage = CodexBarCore.UsageSnapshot(primary: nil, secondary: nil,
                costUsage: history, updatedAt: Date())
            let snapshot = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor)
            let row = snapshot.windows.first { $0.id == "reported-tokens" }
            XCTAssertEqual(row?.used, 123456, descriptor.id.rawValue)
            XCTAssertEqual(row?.label, "Tokens · This billing period", descriptor.id.rawValue)
            XCTAssertNil(row?.usedFraction, descriptor.id.rawValue)
            XCTAssertNil(snapshot.todaysTokens, descriptor.id.rawValue)
        }
    }

    func testTokenWindowDescriptionsSurviveWithoutBecomingFakeQuotas() {
        for detail in ["123,456 tokens", "4 req · 12,345 tok", "1.2 MTok used"] {
            let usage = CodexBarCore.UsageSnapshot(primary: .init(usedPercent: 0, windowMinutes: nil,
                resetsAt: nil, resetDescription: detail), secondary: nil, updatedAt: Date())
            let snapshot = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor(.llmproxy))
            XCTAssertEqual(snapshot.windows.first?.displayValue, detail)
        }
    }

    func testOpenAIAdminPayloadProjectsReportedTokens() throws {
        let iso = ISO8601DateFormatter()
        let start = try XCTUnwrap(iso.date(from: "2026-09-17T00:00:00Z"))
        let end = try XCTUnwrap(iso.date(from: "2026-09-18T00:00:00Z"))
        let now = try XCTUnwrap(iso.date(from: "2026-09-17T12:00:00Z"))
        let api = OpenAIAPIUsageSnapshot(daily: [.init(day: "2026-09-17", startTime: start, endTime: end,
            costUSD: 0.01, requests: 1, inputTokens: 100, cachedInputTokens: 30,
            outputTokens: 20, totalTokens: 120, lineItems: [], models: [])], updatedAt: now, historyDays: 14)
        let snapshot = ExtendedNotchProvider.snapshot(result(api.toUsageSnapshot()), descriptor: descriptor(.openai))
        XCTAssertEqual(snapshot.windows.first { $0.id == "reported-tokens" }?.used, 120)
        XCTAssertNil(snapshot.todaysTokens)
    }

    func testMistralPayloadPreservesMonthPeriodAndHistoricalCoverage() throws {
        let iso = ISO8601DateFormatter()
        let start = try XCTUnwrap(iso.date(from: "2026-09-01T00:00:00Z"))
        let now = try XCTUnwrap(iso.date(from: "2026-09-17T12:00:00Z"))
        let past = try XCTUnwrap(iso.date(from: "2026-09-10T12:00:00Z"))
        for end in [now, past] {
            let api = MistralUsageSnapshot(totalCost: 0.01, currency: "USD", currencySymbol: "$",
                totalInputTokens: 100, totalOutputTokens: 20, totalCachedTokens: 30, modelCount: 0,
                daily: [.init(day: "2026-09-10", cost: 0.01, inputTokens: 100, cachedTokens: 30,
                              outputTokens: 20, models: [])], startDate: start, endDate: end, updatedAt: now)
            let snapshot = ExtendedNotchProvider.snapshot(result(api.toUsageSnapshot()), descriptor: descriptor(.mistral))
            let row = snapshot.windows.first { $0.id == "reported-tokens" }
            XCTAssertEqual(row?.used, 150, "Mistral reports cached tokens as a separate lane")
            XCTAssertEqual(row?.label, end == now ? "Tokens · This month" : "Tokens · Reported 10-day period")
            XCTAssertNil(snapshot.todaysTokens)
        }
    }

    func testTokenHistoryWithUnknownCoverageDoesNotInventAPeriod() {
        let history = CostUsageTokenSnapshot(sessionTokens: nil, sessionCostUSD: nil,
            last30DaysTokens: 150, last30DaysCostUSD: nil, historyDays: 1,
            historyCoverageIsEstablished: false, daily: [], updatedAt: Date())
        let snapshot = ExtendedNotchProvider.snapshot(result(.init(primary: nil, secondary: nil,
            costUsage: history, updatedAt: Date())), descriptor: descriptor(.synthetic))
        let row = snapshot.windows.first { $0.id == "reported-tokens" }
        XCTAssertEqual(row?.used, 150)
        XCTAssertEqual(row?.label, "Tokens · Period unavailable")
    }

    func testBedrockTokenDetailKeepsItsReportedPeriod() {
        let usage = CodexBarCore.UsageSnapshot(primary: nil, secondary: nil, updatedAt: Date(),
            identity: .init(providerID: .bedrock, accountEmail: nil, accountOrganization: nil,
                            loginMethod: "AWS - Claude 14d: 123,456 tokens"))
        let snapshot = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor(.bedrock))
        XCTAssertEqual(snapshot.windows.first { $0.id == "reported-token-detail" }?.displayValue,
                       "Claude 14d: 123,456 tokens")
        XCTAssertNil(snapshot.todaysTokens)
    }

    func testLLMProxyCountsAndProviderBreakdownsKeepUnitsWithoutPercentages() {
        func textWindow(_ text: String) -> RateWindow {
            .init(usedPercent: 0, windowMinutes: nil, resetsAt: nil, resetDescription: text)
        }
        let usage = CodexBarCore.UsageSnapshot(primary: nil, secondary: textWindow("4 requests"),
            tertiary: textWindow("123,456 tokens"),
            extraRateWindows: [.init(id: "model", title: "Model A", window: textWindow("4 req · 123,456 tok"))],
            updatedAt: Date())
        let snapshot = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor(.llmproxy))
        XCTAssertEqual(snapshot.windows.map(\.label), ["Requests", "Tokens", "Model A"])
        XCTAssertEqual(snapshot.windows.map(\.displayValue), ["4 requests", "123,456 tokens", "4 req · 123,456 tok"])
        XCTAssertTrue(snapshot.windows.allSatisfy { $0.usedFraction == nil })
    }

    func testLongCatKeepsExactTokenQuotaCounts() {
        let usage = CodexBarCore.UsageSnapshot(primary: .init(usedPercent: 25, windowMinutes: nil,
            resetsAt: nil, resetDescription: "2500/10000"), secondary: nil, updatedAt: Date())
        let snapshot = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor(.longcat))
        XCTAssertEqual(snapshot.windows.first?.displayValue, "2500/10000")
        XCTAssertEqual(snapshot.windows.first?.usedFraction, 0.25)
    }

    func testUnknownTokenHistoryDoesNotBecomeZeroAndReportedZeroIsKept() {
        for total: Int? in [nil, 0, -1] {
            let history = CostUsageTokenSnapshot(sessionTokens: nil, sessionCostUSD: nil,
                last30DaysTokens: total, last30DaysCostUSD: nil, daily: [], updatedAt: Date())
            let snapshot = ExtendedNotchProvider.snapshot(result(.init(primary: nil, secondary: nil,
                costUsage: history, updatedAt: Date())), descriptor: descriptor(.synthetic))
            XCTAssertEqual(snapshot.windows.first { $0.id == "reported-tokens" }?.used, total == 0 ? 0 : nil)
        }
    }

    func testEveryAddedProviderRunsThroughFetchAdapter() async throws {
        for descriptor in ExtendedProviderCatalog.additions {
            var config = ExtendedProviderConfiguration(providerID: descriptor.id)
            config.allowBillableRequests = true // mock fetch only, no network
            if descriptor.id == .fireworks { config.environment["FIREWORKS_ACCOUNT_SLUG"] = "fixture" }
            let fixture = result(.init(primary: window(25), secondary: window(60), updatedAt: Date(),
                identity: .init(providerID: descriptor.id.instanceID, accountEmail: "fixture@example.invalid",
                                accountOrganization: nil, loginMethod: "Test plan")))
            let provider = ExtendedNotchProvider(descriptor: descriptor, configuration: { config }, fetch: { received, context in
                XCTAssertEqual(received.id, descriptor.id)
                XCTAssertFalse(context.webDebugDumpHTML)
                return fixture
            })
            let snapshot = try await provider.fetchSnapshot()
            XCTAssertEqual(snapshot.id, ExtendedProviderCatalog.localID(descriptor.id))
            XCTAssertEqual(snapshot.status, .ok, descriptor.id.rawValue)
            XCTAssertEqual(provider.account()?.label, "fixture@example.invalid", descriptor.id.rawValue)
        }
    }

    func testEveryAddedProviderMapsMissingCredentialsWithoutFakeUsage() async throws {
        for descriptor in ExtendedProviderCatalog.additions {
            var config = ExtendedProviderConfiguration(providerID: descriptor.id)
            config.allowBillableRequests = true
            if descriptor.id == .fireworks { config.environment["FIREWORKS_ACCOUNT_SLUG"] = "fixture" }
            let provider = ExtendedNotchProvider(descriptor: descriptor, configuration: { config }, fetch: { _, _ in
                throw ProviderFetchError.noAvailableStrategy(descriptor.id)
            })
            let snapshot = try await provider.fetchSnapshot()
            XCTAssertEqual(snapshot.status, .needsAuth, descriptor.id.rawValue)
            XCTAssertFalse(snapshot.hasReading, descriptor.id.rawValue)
            XCTAssertNil(provider.account())
        }
    }

    func testBillableProvidersCannotCallFetchWithoutConsent() async throws {
        for id: CodexBarCore.UsageProvider in [.azureopenai, .doubao, .bedrock] {
            let provider = ExtendedNotchProvider(descriptor: descriptor(id), configuration: { .init(providerID: id) }, fetch: { _, _ in
                XCTFail("Billable request must not execute")
                throw CancellationError()
            })
            let snapshot = try await provider.fetchSnapshot()
            guard case .unsupported = snapshot.status else { return XCTFail("Expected explicit consent requirement") }
            XCTAssertFalse(snapshot.hasReading)
        }
    }

    func testFireworksRequiresExplicitSlugInsteadOfWritingCodexBarConfig() async throws {
        let provider = ExtendedNotchProvider(descriptor: descriptor(.fireworks), configuration: { .init(providerID: .fireworks) }, fetch: { _, _ in
            XCTFail("Must not discover and persist into another application's config")
            throw CancellationError()
        })
        let snapshot = try await provider.fetchSnapshot()
        XCTAssertTrue(snapshot.statusMessage?.contains("FIREWORKS_ACCOUNT_SLUG") == true)
    }

    func testFractionsResetsNamedWindowsAndUnknownValuesKeepTheirMeaning() {
        let usage = CodexBarCore.UsageSnapshot(primary: window(25), secondary: window(120),
            tertiary: window(.nan), extraRateWindows: [
                .init(id: "unknown", title: "Unknown", window: window(100), usageKnown: false)
            ], updatedAt: Date(), dataConfidence: .estimated)
        let snapshot = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor(.synthetic))
        XCTAssertEqual(snapshot.windows.first?.usedFraction, 0.25)
        XCTAssertEqual(snapshot.windows.first?.duration, 18_000)
        XCTAssertEqual(snapshot.windows.first?.resetsAt, Date(timeIntervalSince1970: 2_000_000_000))
        XCTAssertEqual(snapshot.windows[1].usedFraction, 1.2)
        XCTAssertNil(snapshot.windows.first { $0.id == "tertiary" }?.usedFraction)
        XCTAssertNil(snapshot.windows.first { $0.label == "Unknown" }?.usedFraction)
        XCTAssertEqual(snapshot.fidelity, .derived)
    }

    func testSyntheticWindowsNeverBecomeZeroPercentReadings() {
        let snapshot = ExtendedNotchProvider.snapshot(result(.init(primary: window(0, synthetic: true),
            secondary: nil, updatedAt: Date())), descriptor: descriptor(.synthetic))
        XCTAssertFalse(snapshot.hasReading)
        XCTAssertEqual(snapshot.status, .ok)
        XCTAssertNil(snapshot.usedFraction)
        XCTAssertTrue(snapshot.statusMessage?.contains("did not report") == true)
    }

    func testBalanceOnlyAndProbeProvidersDoNotInventQuotaPercentages() {
        for id: CodexBarCore.UsageProvider in [.deepseek, .deepinfra, .moonshot, .azureopenai, .groq] {
            let usage = CodexBarCore.UsageSnapshot(primary: .init(usedPercent: 0, windowMinutes: nil,
                resetsAt: nil, resetDescription: "12.345 USD available"), secondary: nil, updatedAt: Date())
            let snapshot = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor(id))
            XCTAssertNil(snapshot.usedFraction, id.rawValue)
            XCTAssertEqual(snapshot.windows.first?.summary, "12.345 USD available", id.rawValue)
        }
    }

    func testCostAndFractionalCreditsRetainCurrencyAndPrecision() {
        let usage = CodexBarCore.UsageSnapshot(primary: nil, secondary: nil,
            providerCost: .init(used: 1.25, limit: 5, currencyCode: "EUR", period: "Monthly", updatedAt: Date()), updatedAt: Date())
        let credits = CreditsSnapshot(remaining: 0.125, events: [], updatedAt: Date())
        let snapshot = ExtendedNotchProvider.snapshot(result(usage, credits: credits), descriptor: descriptor(.openrouter))
        XCTAssertEqual(snapshot.windows.first?.usedFraction, 0.25)
        XCTAssertEqual(snapshot.windows.first?.summary, "1.25 EUR / 5 EUR")
        XCTAssertEqual(snapshot.windows.last?.summary, "0.125")
    }

    func testUnknownCreditBalanceNeverAppearsAsZero() {
        let usage = CodexBarCore.UsageSnapshot(primary: nil, secondary: nil, updatedAt: Date())
        let credits = CreditsSnapshot(remaining: 0, events: [], updatedAt: Date(), balanceReadSucceeded: false)
        XCTAssertFalse(ExtendedNotchProvider.snapshot(result(usage, credits: credits), descriptor: descriptor(.openrouter)).hasReading)
    }

    func testProviderDetailRowsRemainReadableWithoutQuota() throws {
        let row = try ProviderDetailSection.Row(label: "Balance", value: "¥1.2345")
        let detail = try ProviderDetailSection(title: "Account", rows: [row])
        let snapshot = ExtendedNotchProvider.snapshot(result(.init(primary: nil, secondary: nil,
            details: [detail], updatedAt: Date())), descriptor: descriptor(.moonshot))
        XCTAssertEqual(snapshot.windows.first?.summary, "¥1.2345")
        XCTAssertEqual(snapshot.compactRowCount, 0)
        XCTAssertNil(snapshot.ringFraction)
    }

    func testLegacyArchiveWindowDecodesWithoutNewDisplayField() throws {
        let data = Data(#"{"id":"session","label":"Session","usedFraction":0.5}"#.utf8)
        let decoded = try JSONDecoder().decode(LimitWindow.self, from: data)
        XCTAssertNil(decoded.displayValue)
        XCTAssertEqual(decoded.usedFraction, 0.5)
    }

    func testSourceConfigAndDocumentedEnvironmentAreScoped() throws {
        var config = ExtendedProviderConfiguration(providerID: .bedrock)
        config.provider.apiKey = "fixture-access-key"
        config.provider.secretKey = "fixture-secret-key"
        config.environment = ["AWS_SESSION_TOKEN": "fixture-session", "AWS_PROFILE": "work", "PATH": "/malicious", "UNRELATED_TOKEN": "not-forwarded"]
        let env = config.fetchEnvironment(base: ["PATH": "/usr/bin"])
        XCTAssertEqual(env["AWS_ACCESS_KEY_ID"], "fixture-access-key")
        XCTAssertEqual(env["AWS_SECRET_ACCESS_KEY"], "fixture-secret-key")
        XCTAssertEqual(env["AWS_SESSION_TOKEN"], "fixture-session")
        XCTAssertEqual(env["PATH"], "/usr/bin")
        XCTAssertNil(env["UNRELATED_TOKEN"])
        let decoded = try JSONDecoder().decode(ExtendedProviderConfiguration.self, from: JSONEncoder().encode(config))
        XCTAssertEqual(decoded.environment["AWS_PROFILE"], "work")
    }

    func testManualCookieSettingsArePassedToEveryCookieDescriptor() throws {
        for descriptor in ExtendedProviderCatalog.additions where descriptor.settingsSection.defaultContribution != nil {
            var config = ExtendedProviderConfiguration(providerID: descriptor.id)
            config.provider.cookieSource = .manual
            config.provider.cookieHeader = descriptor.id == .kimi ? "kimi-auth=fixture" : "session=fixture"
            if let settings = config.settings,
               let cookies = descriptor.settingsSection.cookieSettings(from: settings) {
                XCTAssertEqual(cookies.cookieSource, .manual, descriptor.id.rawValue)
            }
        }
    }

    func testErrorMessagesNeverContainResponseBodiesOrSecrets() {
        let error = NSError(domain: "provider", code: 403, userInfo: [NSLocalizedDescriptionKey: "Bearer private-token-123 response-body"])
        XCTAssertFalse(ExtendedNotchProvider.safeError(error, provider: "Example").contains("private-token"))
        let classified = ProviderFetchClassifiedError(kind: .apiFailure, message: "private-token-123")
        XCTAssertFalse(ExtendedNotchProvider.safeError(classified, provider: "Example").contains("private-token"))
    }

    func testNoAvailableAccountCannotInheritOtherProviderIdentity() async throws {
        let wrongIdentity = CodexBarCore.UsageSnapshot(primary: window(5), secondary: nil, updatedAt: Date(),
            identity: .init(providerID: .codex, accountEmail: "wrong@example.invalid", accountOrganization: nil, loginMethod: "Wrong plan"))
        let fixture = result(wrongIdentity)
        let provider = ExtendedNotchProvider(descriptor: descriptor(.openrouter), configuration: { .init(providerID: .openrouter) }, fetch: { _, _ in fixture })
        _ = try await provider.fetchSnapshot()
        XCTAssertNil(provider.account()?.label)
        XCTAssertNil(provider.account()?.plan)
    }

    func testProviderPickerFindsNewNames() {
        let rows = NotchProviderCatalog.all.map { ProviderRowModel(id: $0.id, name: $0.name, glyph: NotchProviderCatalog.glyph(for: $0.id), connected: false, statusLine: "", accountLine: nil, wasRefused: false, primary: .details) }
        XCTAssertEqual(ProviderPickerView.matching(rows, query: "DeepSeek").map(\.id), ["deepseek"])
    }
    func testBrowserOptOutIsEnforcedForWindsurfAndGroq() throws {
        let windsurf = ExtendedProviderConfiguration(providerID: .windsurf)
        XCTAssertEqual(windsurf.settings?.windsurf?.cookieSource, .off)
        var groq = ExtendedProviderConfiguration(providerID: .groq)
        groq.provider.source = .web
        XCTAssertEqual(groq.sourceMode(environment: [:]), .api)
        groq.provider.cookieHeader = "stytch_session=fixture-session"
        let env = groq.fetchEnvironment(base: [:])
        XCTAssertEqual(env["GROQ_SESSION_TOKEN"], "fixture-session")
        XCTAssertEqual(groq.sourceMode(environment: env), .web)
    }

    func testPrepaidAmountsAreNeverRelabeledAsSpend() {
        let usage = CodexBarCore.UsageSnapshot(primary: nil, secondary: nil,
            providerCost: .init(used: 100, limit: 0, currencyCode: "USD", period: "Prepaid credits", updatedAt: Date()), updatedAt: Date())
        let snapshot = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor(.xai))
        XCTAssertEqual(snapshot.windows.first?.label, "Prepaid credits")
        XCTAssertNil(snapshot.ringFraction)
    }

    func testEmptyManualCookieNeverEnablesFactoryOrAugmentBrowserImport() throws {
        for id: CodexBarCore.UsageProvider in [.factory, .augment] {
            let config = ExtendedProviderConfiguration(providerID: id)
            let settings = try XCTUnwrap(config.settings)
            XCTAssertEqual(descriptor(id).settingsSection.cookieSettings(from: settings)?.cookieSource, .off)
        }
    }

    func testDocumentedCookieEnvironmentReachesManualSettings() throws {
        for (id, key): (CodexBarCore.UsageProvider, String) in [(.perplexity, "PERPLEXITY_SESSION_TOKEN"), (.manus, "MANUS_COOKIE"), (.qwencloud, "QWEN_CLOUD_COOKIE"), (.minimax, "MINIMAX_COOKIE_HEADER"), (.devin, "DEVIN_BEARER_TOKEN"), (.devin, "DEVIN_AUTHORIZATION"), (.longcat, "LONGCAT_MANUAL_COOKIE")] {
            var config = ExtendedProviderConfiguration(providerID: id)
            config.environment[key] = "session=fixture"
            let settings = try XCTUnwrap(config.settings)
            if id == .devin {
                XCTAssertEqual(settings.devin?.cookieSource, .manual)
                XCTAssertEqual(settings.devin?.manualBearerToken, "session=fixture")
                continue
            }
            let cookie = try XCTUnwrap(descriptor(id).settingsSection.cookieSettings(from: settings))
            XCTAssertEqual(cookie.cookieSource, .manual, id.rawValue)
            XCTAssertEqual(cookie.manualCookieHeader, "session=fixture", id.rawValue)
        }
    }

    func testDeepSeekDoesNotFallBackToBrowserWithoutOptIn() {
        let config = ExtendedProviderConfiguration(providerID: .deepseek)
        XCTAssertEqual(config.sourceMode(environment: [:]), .api)
    }

    func testCrofAndVeniceBalancesRemainText() {
        for id: CodexBarCore.UsageProvider in [.crof, .venice] {
            let usage = CodexBarCore.UsageSnapshot(primary: .init(usedPercent: 0, windowMinutes: nil,
                resetsAt: nil, resetDescription: "$12.50 remaining"), secondary: nil, updatedAt: Date())
            let snapshot = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor(id))
            XCTAssertNil(snapshot.ringFraction)
            XCTAssertEqual(snapshot.windows.first?.summary, "$12.50 remaining")
        }
    }

    func testMiniMaxOptOutNeverSelectsBrowserStrategyWithoutManualAuth() {
        var config = ExtendedProviderConfiguration(providerID: .minimax)
        config.provider.source = .web
        XCTAssertEqual(config.sourceMode(environment: [:]), .api)
        config.provider.cookieHeader = "session=fixture"
        XCTAssertEqual(config.sourceMode(environment: [:]), .web)
        config.provider.cookieHeader = "Cookie: "
        XCTAssertEqual(config.sourceMode(environment: [:]), .api)
    }

    func testInvalidKimiManualHeaderCannotTriggerBrowserImport() {
        var config = ExtendedProviderConfiguration(providerID: .kimi)
        config.provider.cookieHeader = "foo=bar"
        XCTAssertEqual(config.settings?.kimi?.cookieSource, .off)
        config.provider.cookieHeader = "kimi-auth=fixture"
        XCTAssertEqual(config.settings?.kimi?.cookieSource, .manual)
    }

    func testJetBrainsCustomDirectoryReachesLocalProbe() {
        var config = ExtendedProviderConfiguration(providerID: .jetbrains)
        config.environment["IDE_BASE"] = "/tmp/fixture-ide"
        XCTAssertEqual(config.settings?.jetbrainsIDEBasePath, "/tmp/fixture-ide")
    }

    func testUnknownBobcoinAndZoomMateCeilingsNeverBecomeFullQuota() {
        for (id, summary): (CodexBarCore.UsageProvider, String) in [
            (.ibmbob, "25 Bobcoins used"), (.ibmbob, "25 / 0 Bobcoins"), (.zoommate, "Credits")
        ] {
            let usage = CodexBarCore.UsageSnapshot(primary: .init(usedPercent: 0, windowMinutes: nil,
                resetsAt: nil, resetDescription: summary), secondary: nil, updatedAt: Date())
            let value = ExtendedNotchProvider.snapshot(result(usage), descriptor: descriptor(id))
            XCTAssertNil(value.usedFraction, id.rawValue)
            XCTAssertNotNil(value.windows.first?.displayValue)
            XCTAssertFalse(value.windows.first?.summary.contains("100%") ?? true)
        }
        let known = CodexBarCore.UsageSnapshot(primary: .init(usedPercent: 0, windowMinutes: nil,
            resetsAt: nil, resetDescription: "0 / 100 Bobcoins"), secondary: nil, updatedAt: Date())
        XCTAssertEqual(ExtendedNotchProvider.snapshot(result(known), descriptor: descriptor(.ibmbob)).usedFraction, 0)
    }

    func testNativeAuthenticationErrorClearsPreviouslyLoadedAccount() async throws {
        actor Fetches {
            var count = 0
            let initial: ProviderFetchResult
            init(_ initial: ProviderFetchResult) { self.initial = initial }
            func next() throws -> ProviderFetchResult {
                count += 1
                if count == 1 { return initial }
                throw IBMBobUsageError.invalidCredentials
            }
        }
        let initial = result(.init(primary: window(25), secondary: nil, updatedAt: Date(),
            identity: .init(providerID: .ibmbob, accountEmail: "fixture@example.invalid",
                            accountOrganization: nil, loginMethod: "Fixture")))
        let fetches = Fetches(initial)
        let provider = ExtendedNotchProvider(descriptor: descriptor(.ibmbob),
            configuration: { .init(providerID: .ibmbob) }, fetch: { _, _ in try await fetches.next() })
        _ = try await provider.fetchSnapshot()
        XCTAssertNotNil(provider.account())
        let expired = try await provider.fetchSnapshot()
        XCTAssertEqual(expired.status, .needsAuth)
        XCTAssertTrue(expired.windows.isEmpty)
        XCTAssertNil(provider.account())
        XCTAssertTrue(NotchUsageStore.supersedesHistory(expired.status))
    }

    func testNativeNetworkErrorExportsNoSuccessfulQuota() async throws {
        let provider = ExtendedNotchProvider(descriptor: descriptor(.ibmbob),
            configuration: { .init(providerID: .ibmbob) }, fetch: { _, _ in
                throw IBMBobUsageError.networkError("Fixture timeout")
            })
        let result = try await provider.fetchSnapshot()
        guard case .error = result.status else { return XCTFail("Expected a failed refresh.") }
        XCTAssertTrue(result.windows.isEmpty)
    }

}
