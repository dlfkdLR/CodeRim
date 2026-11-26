#if CODERIM_RUST_ACCOUNTING
import CodexBarCore
import XCTest
@testable import CodeRim

@MainActor
final class MigrationProviderAccessTests: XCTestCase {
    func testSavedRolesEndpointsAndBillingConsent() async throws {
        func scoped(_ id: CodexBarCore.UsageProvider, _ update: (inout ExtendedProviderConfiguration) -> Void) -> [String: String]? {
            var config = ExtendedProviderConfiguration(providerID: id)
            update(&config)
            return MigrationProviderAccess.environment(for: config, descriptor: ProviderDescriptorRegistry.descriptor(for: id))
        }
        XCTAssertEqual(scoped(.openai) { $0.environment["OPENAI_API_KEY"] = " ordinary " }, ["OPENAI_API_KEY": "ordinary"])
        XCTAssertEqual(scoped(.openai) {
            $0.environment = ["OPENAI_API_KEY": "ordinary", "OPENAI_ADMIN_KEY": "previous"]
            $0.provider.apiKey = "current"
            $0.provider.workspaceID = "project"
        }, ["OPENAI_ADMIN_KEY": "current", "OPENAI_PROJECT_ID": "project"])
        XCTAssertEqual(scoped(.azureopenai) {
            $0.provider.apiKey = "owned"
            $0.provider.enterpriseHost = "https://synthetic.example.invalid"
            $0.provider.workspaceID = "deployment"
        }, ["AZURE_OPENAI_API_KEY": "owned", "AZURE_OPENAI_ENDPOINT": "https://synthetic.example.invalid",
            "AZURE_OPENAI_DEPLOYMENT_NAME": "deployment"])
        XCTAssertNil(scoped(.bedrock) { $0.provider.apiKey = "id"; $0.environment["AWS_PROFILE"] = "ignored" })
        XCTAssertEqual(scoped(.bedrock) {
            $0.provider.apiKey = "id"; $0.provider.secretKey = "secret"; $0.provider.region = "fixture-region"
            $0.environment = ["AWS_PROFILE": "ignored", "AWS_SESSION_TOKEN": "session", "AWS_REGION": "previous-region"]
        }, ["AWS_ACCESS_KEY_ID": "id", "AWS_SECRET_ACCESS_KEY": "secret", "AWS_SESSION_TOKEN": "session",
            "AWS_REGION": "previous-region", "CODEXBAR_BEDROCK_AUTH_MODE": "keys"])
        XCTAssertNil(scoped(.doubao) { $0.provider.apiKey = "AKLT-fixture" })
        XCTAssertEqual(scoped(.doubao) {
            $0.provider.apiKey = "AKLT-fixture"; $0.provider.secretKey = "signed"; $0.provider.region = "region"
        }, ["VOLCENGINE_ACCESS_KEY_ID": "AKLT-fixture", "VOLCENGINE_SECRET_ACCESS_KEY": "signed", "VOLCENGINE_REGION": "region"])
        XCTAssertEqual(scoped(.wayfinder) {
            $0.environment = ["WAYFINDER_GATEWAY_URL": "http://localhost:8080", "HOME": "/ignored"]
        }, ["WAYFINDER_GATEWAY_URL": "http://localhost:8080"])
        var alias = ExtendedProviderConfiguration(providerID: .deepseek)
        alias.environment["DEEPSEEK_KEY"] = "legacy-env"
        alias.provider.pluginSecrets = ["DEEPSEEK_API_KEY": "legacy-plugin"]
        XCTAssertEqual(MigrationProviderAccess.savedAPIKey(alias, for: .deepseek), "legacy-env")
        MigrationProviderAccess.setAPIKey("new", in: &alias, for: .deepseek)
        XCTAssertEqual(MigrationProviderAccess.savedAPIKey(alias, for: .deepseek), "new")
        MigrationProviderAccess.setAPIKey("", in: &alias, for: .deepseek)
        XCTAssertNil(MigrationProviderAccess.environment(for: alias, descriptor: ProviderDescriptorRegistry.descriptor(for: .deepseek)))
        for id: CodexBarCore.UsageProvider in [.azureopenai, .bedrock, .doubao] {
            let descriptor = ProviderDescriptorRegistry.descriptor(for: id)
            let provider = ExtendedNotchProvider(descriptor: descriptor, ownedOnly: true,
                configuration: { .init(providerID: id) })
            let snapshot = try await provider.fetchSnapshot()
            guard case .unsupported(let message) = snapshot.status else { return XCTFail("Missing billing consent") }
            XCTAssertTrue(message.contains("Enable billed requests"))
        }
    }

    func testEveryProviderRequiresItsReviewedSourceAndNeverUsesOldDiscoverySettings() async throws {
        for descriptor in ProviderDescriptorRegistry.all {
            var config = ExtendedProviderConfiguration(providerID: descriptor.id)
            config.provider.cookieHeader = "synthetic-ignored"
            config.provider.cookieSource = .auto
            config.environment["CODEXBAR_CONFIG"] = "/fixture/no-config"
            config.environment["HOME"] = "/fixture/no-home"
            if MigrationProviderAccess.officialCLI(descriptor.id) != nil || descriptor.id == .wayfinder {
                XCTAssertEqual(MigrationProviderAccess.environment(for: config, descriptor: descriptor), [:])
                continue
            }
            XCTAssertNil(MigrationProviderAccess.environment(for: config, descriptor: descriptor))
            if descriptor.id == .bedrock {
                config.environment["AWS_PROFILE"] = "fixture-external-profile"
                config.provider.secretKey = "owned-secret"
            }
            guard let mode = MigrationProviderAccess.api[descriptor.id.rawValue] else {
                let provider = ExtendedNotchProvider(descriptor: descriptor, ownedOnly: true, configuration: {
                    XCTFail("Unexpected configuration read for \(descriptor.id.rawValue)")
                    return config
                })
                let snapshot = try await provider.fetchSnapshot()
                XCTAssertEqual(snapshot.status, .unsupported(MigrationProviderAccess.unavailable))
                continue
            }
            for name in MigrationProviderAccess.keyAliases[descriptor.id.rawValue] ?? [] {
                config.environment[name] = " synthetic-alias "
                XCTAssertEqual(MigrationProviderAccess.environment(for: config, descriptor: descriptor)?[mode.key], "synthetic-alias")
                config.environment.removeValue(forKey: name)
            }
            config.environment[mode.key] = " synthetic-env-key "
            for key in MigrationProviderAccess.options[descriptor.id.rawValue] ?? [] { config.environment[key] = "synthetic-option" }
            var env = try XCTUnwrap(MigrationProviderAccess.environment(for: config, descriptor: descriptor))
            XCTAssertEqual(env[mode.key], "synthetic-env-key")
            XCTAssertNil(env["CODEXBAR_CONFIG"])
            XCTAssertNil(env["HOME"])
            config.provider.apiKey = " synthetic-saved-key "
            env = try XCTUnwrap(MigrationProviderAccess.environment(for: config, descriptor: descriptor))
            XCTAssertEqual(env[mode.key], "synthetic-saved-key")
            if [.fireworks, .xai, .poe, .codebuff].contains(descriptor.id) { continue }
            let browser = BrowserDetection()
            let context = ProviderFetchContext(runtime: .app, sourceMode: .api, includeCredits: true,
                includeOptionalUsage: false, webTimeout: 1, webDebugDumpHTML: false, verbose: false,
                env: env, settings: nil, fetcher: UsageFetcher(environment: env),
                claudeFetcher: ClaudeUsageFetcher(browserDetection: browser, environment: env), browserDetection: browser)
            let strategies = await descriptor.fetchPlan.pipeline.resolveStrategies(context)
            XCTAssertTrue(strategies.contains { $0.id == mode.strategy && $0.kind == .apiToken },
                          "Missing reviewed API strategy for \(descriptor.id.rawValue)")
        }
    }
}
#endif
