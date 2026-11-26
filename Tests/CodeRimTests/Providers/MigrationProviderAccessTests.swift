#if CODERIM_RUST_ACCOUNTING
import CodexBarCore
import XCTest
@testable import CodeRim

@MainActor
final class MigrationProviderAccessTests: XCTestCase {
    func testEveryProviderRequiresItsReviewedSourceAndNeverUsesOldDiscoverySettings() async throws {
        for descriptor in ProviderDescriptorRegistry.all {
            var config = ExtendedProviderConfiguration(providerID: descriptor.id)
            config.provider.cookieHeader = "synthetic-ignored"
            config.provider.cookieSource = .auto
            config.environment["CODEXBAR_CONFIG"] = "/fixture/no-config"
            config.environment["HOME"] = "/fixture/no-home"
            XCTAssertNil(MigrationProviderAccess.environment(for: config, descriptor: descriptor))
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
            if [.fireworks, .xai, .poe].contains(descriptor.id) { continue }
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
