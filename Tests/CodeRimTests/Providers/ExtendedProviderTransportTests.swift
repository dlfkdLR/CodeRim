import Foundation
import XCTest
import CodexBarCore
@testable import CodeRim

/// Real upstream fetch strategies and JS resource loading, with every HTTP request intercepted locally.
@MainActor
final class ExtendedProviderTransportTests: XCTestCase {
    override func setUpWithError() throws {
        // XCTest's main executable belongs to Xcode. Supply the upstream resolver's
        // development fallback inside the dependency checkout, never in Xcode itself.
        let productDirectory = Bundle(for: Self.self).bundleURL.deletingLastPathComponent()
        let name = "CodexBar_CodexBarCore.bundle"
        let source = productDirectory.appendingPathComponent(name)
        var ancestor = productDirectory
        while ancestor.path != "/" {
            let checkout = ancestor.appendingPathComponent("checkouts/CodexBar")
            if FileManager.default.fileExists(atPath: checkout.appendingPathComponent("Package.swift").path) {
                let directory = checkout.appendingPathComponent(".build/debug")
                try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                let destination = directory.appendingPathComponent(name)
                if !FileManager.default.fileExists(atPath: destination.path) {
                    try FileManager.default.copyItem(at: source, to: destination)
                }
                return
            }
            ancestor.deleteLastPathComponent()
        }
        XCTFail("Unable to locate the isolated SwiftPM dependency checkout for resource tests")
    }

    func testOpenRouterScriptRequestsAndParsesCreditsAndKeyLimit() async throws {
        ProviderFixtureProtocol.install { request in
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer fixture-key")
            switch request.url?.path {
            case "/api/v1/credits": return (200, #"{"data":{"total_credits":100,"total_usage":25}}"#)
            case "/api/v1/key": return (200, #"{"data":{"label":"test","limit":100,"limit_remaining":75,"usage":25}}"#)
            default: throw URLError(.unsupportedURL)
            }
        }
        defer { ProviderFixtureProtocol.uninstall() }
        let provider = make(.openrouter)
        let snapshot = try await provider.fetchSnapshot()
        XCTAssertEqual(snapshot.status, .ok)
        XCTAssertTrue(snapshot.windows.contains { $0.usedFraction == 0.25 })
        XCTAssertTrue(snapshot.windows.contains { $0.summary.contains("75") })
    }

    func testElevenLabsNativeFetcherParsesSubscriptionAndReset() async throws {
        ProviderFixtureProtocol.install { request in
            XCTAssertEqual(request.url?.host, "api.elevenlabs.io")
            XCTAssertEqual(request.value(forHTTPHeaderField: "xi-api-key"), "fixture-key")
            return (200, #"{"tier":"creator","character_count":12500,"character_limit":100000,"can_extend_character_limit":true,"next_character_count_reset_unix":2000000000,"voice_limit":30,"voice_count":3}"#)
        }
        defer { ProviderFixtureProtocol.uninstall() }
        let snapshot = try await make(.elevenlabs).fetchSnapshot()
        XCTAssertEqual(snapshot.status, .ok)
        XCTAssertEqual(snapshot.windows.first?.usedFraction, 0.125)
        XCTAssertEqual(snapshot.windows.first?.resetsAt, Date(timeIntervalSince1970: 2_000_000_000))
    }

    func testLiteLLMUsesConfiguredEndpointAndPreservesUserBudget() async throws {
        ProviderFixtureProtocol.install { request in
            XCTAssertEqual(request.url?.host, "litellm.example.invalid")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer fixture-key")
            switch request.url?.path {
            case "/key/info": return (200, #"{"info":{"user_id":"fixture-user","spend":25,"max_budget":100}}"#)
            case "/user/info": return (200, #"{"user_id":"fixture-user","user_info":{"user_id":"fixture-user","user_email":"test@example.invalid","spend":25,"max_budget":100},"teams":[],"keys":[]}"#)
            default: throw URLError(.unsupportedURL)
            }
        }
        defer { ProviderFixtureProtocol.uninstall() }
        var config = ExtendedProviderConfiguration(providerID: .litellm)
        config.provider.apiKey = "fixture-key"
        config.provider.enterpriseHost = "https://litellm.example.invalid"
        let provider = ExtendedNotchProvider(descriptor: ProviderDescriptorRegistry.descriptor(for: .litellm), configuration: { config })
        let snapshot = try await provider.fetchSnapshot()
        XCTAssertEqual(snapshot.status, .ok)
        XCTAssertTrue(snapshot.windows.contains { $0.usedFraction == 0.25 })
    }

    func testHTTPUnauthorizedDoesNotProduceSuccessfulZeroReading() async throws {
        ProviderFixtureProtocol.install { _ in (401, #"{"error":{"message":"fixture-private-secret"}}"#) }
        defer { ProviderFixtureProtocol.uninstall() }
        do {
            let snapshot = try await make(.openrouter).fetchSnapshot()
            XCTAssertNotEqual(snapshot.status, .ok)
            XCTAssertFalse(snapshot.hasReading)
        } catch {
            XCTAssertFalse(error.localizedDescription.contains("fixture-private-secret"))
        }
    }

    func testFireworksRecoversStaleSlugWithoutUsingConfigWritingStrategy() async throws {
        ProviderFixtureProtocol.install { request in
            switch request.url?.path {
            case "/v1/accounts/old-team/billing/summary": return (404, "{}")
            case "/v1/accounts": return (200, #"{"accounts":[{"name":"accounts/current-team"}]}"#)
            case "/v1/accounts/current-team/billing/summary":
                return (200, #"{"lineItems":[{"totalCost":{"currencyCode":"USD","units":"2","nanos":250000000}}]}"#)
            default: throw URLError(.unsupportedURL)
            }
        }
        defer { ProviderFixtureProtocol.uninstall() }
        var config = ExtendedProviderConfiguration(providerID: .fireworks)
        config.provider.apiKey = "fixture-key"
        config.environment["FIREWORKS_ACCOUNT_SLUG"] = "old-team"
        let provider = ExtendedNotchProvider(descriptor: ProviderDescriptorRegistry.descriptor(for: .fireworks), configuration: { config })
        let snapshot = try await provider.fetchSnapshot()
        XCTAssertEqual(snapshot.status, .ok)
        XCTAssertTrue(snapshot.windows.contains { $0.summary == "2.25 USD" })
        XCTAssertEqual(provider.account()?.source, "api · current-team")
    }

    func testJetBrainsUnknownQuotaDoesNotBecomeFullRemainingAllowance() async throws {
        for json in [#"{"type":"Unknown"}"#, #"{"type":"AI Pro","maximum":"100"}"#,
                     #"{"current":"invalid","maximum":"100"}"#, #"{"current":"0","maximum":"0"}"#] {
            let snapshot = try await jetBrainsSnapshot(quotaJSON: json)
            XCTAssertEqual(snapshot.status, .needsAuth)
            XCTAssertFalse(snapshot.hasReading)
            XCTAssertTrue(snapshot.windows.isEmpty)
        }
    }

    func testJetBrainsValidQuotaIsReadFromRealXML() async throws {
        let snapshot = try await jetBrainsSnapshot(quotaJSON: #"{"type":"AI Pro","current":"25","maximum":"100","tariffQuota":{"available":"75"}}"#)
        XCTAssertEqual(snapshot.status, .ok)
        XCTAssertEqual(snapshot.windows.first?.usedFraction, 0.25)
    }

    func testAllBundledProviderScriptsLoadTheirRealRuntime() throws {
        let product = Bundle(for: Self.self).bundleURL.deletingLastPathComponent()
        let bundle = try XCTUnwrap(Bundle(url: product.appendingPathComponent("CodexBar_CodexBarCore.bundle")))
        let scripts = bundle.paths(forResourcesOfType: "js", inDirectory: nil)
            .map { URL(fileURLWithPath: $0).deletingPathExtension().lastPathComponent }
            .filter { $0 != "provider-plugin-prelude" && !$0.hasPrefix("sucrase-") }
        XCTAssertEqual(Set(scripts), Set(["t3chat", "xai", "sub2api", "venice", "openai", "manus", "crof", "clawrouter", "deepgram", "qoder", "clinepass", "synthetic", "perplexity", "zai", "openrouter", "poe"]))
        for script in scripts {
            let runtime = try ProviderPluginRuntime(bundledPlugin: script)
            XCTAssertFalse(runtime.manifest.id.rawValue.isEmpty, script)
        }
        print("PROVIDER_SCRIPT_RUNTIME_COUNT=\(scripts.count)")
    }

    func testElevenLabsRevokedCredentialsDiscardPreviousUsageAndAccount() async throws {
        let suite = "CodeRim.ProviderTransport.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite); ProviderFixtureProtocol.uninstall() }
        let provider = make(.elevenlabs)
        let store = NotchUsageStore(providers: [provider], archive: UsageArchive(defaults: defaults))
        ProviderFixtureProtocol.install { _ in
            (200, #"{"tier":"creator","character_count":12500,"character_limit":100000,"next_character_count_reset_unix":2000000000}"#)
        }
        await store.refresh()
        XCTAssertEqual(store.snapshots.first?.status, .ok)
        XCTAssertTrue(store.snapshots.first?.hasReading == true)
        XCTAssertNotNil(provider.account())
        ProviderFixtureProtocol.install { _ in (401, #"{"detail":{"status":"invalid_api_key","message":"fixture-private-secret"}}"#) }
        await store.refresh()
        XCTAssertEqual(store.snapshots.first?.status, .needsAuth)
        XCTAssertTrue(store.snapshots.first?.windows.isEmpty == true)
        XCTAssertNil(provider.account())
        ProviderFixtureProtocol.install { _ in (403, #"{"detail":{"status":"missing_permissions","message":"fixture-private-secret"}}"#) }
        await store.refresh()
        guard case .unsupported = store.snapshots.first?.status else { return XCTFail("Required API permissions must be actionable") }
        XCTAssertFalse(store.snapshots.first?.statusMessage?.contains("fixture-private-secret") == true)
    }

    private func jetBrainsSnapshot(quotaJSON: String) async throws -> ProviderSnapshot {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let options = directory.appendingPathComponent("options")
        try FileManager.default.createDirectory(at: options, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let encoded = quotaJSON.replacingOccurrences(of: "\"", with: "&quot;")
        let xml = "<application><component name=\"AIAssistantQuotaManager2\"><option name=\"quotaInfo\" value=\"\(encoded)\" /></component></application>"
        try xml.write(to: options.appendingPathComponent("AIAssistantQuotaManager2.xml"), atomically: true, encoding: .utf8)
        var config = ExtendedProviderConfiguration(providerID: .jetbrains)
        config.environment["IDE_BASE"] = directory.path
        return try await ExtendedNotchProvider(descriptor: ProviderDescriptorRegistry.descriptor(for: .jetbrains),
            configuration: { config }).fetchSnapshot()
    }

    private func make(_ id: CodexBarCore.UsageProvider) -> ExtendedNotchProvider {
        var config = ExtendedProviderConfiguration(providerID: id)
        config.provider.apiKey = "fixture-key"
        config.provider.source = .api
        return ExtendedNotchProvider(descriptor: ProviderDescriptorRegistry.descriptor(for: id), configuration: { config })
    }
}

final class ProviderFixtureProtocol: URLProtocol, @unchecked Sendable {
    typealias Handler = @Sendable (URLRequest) throws -> (Int, String)
    private static let lock = NSLock()
    nonisolated(unsafe) private static var handler: Handler?

    static func install(_ value: @escaping Handler) {
        lock.withLock { handler = value }
        _ = URLProtocol.registerClass(Self.self)
    }
    static func uninstall() {
        URLProtocol.unregisterClass(Self.self)
        lock.withLock { handler = nil }
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            guard let handler = Self.lock.withLock({ Self.handler }), let url = request.url else { throw URLError(.cancelled) }
            let (status, json) = try handler(request)
            let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: ["Content-Type": "application/json"])!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(json.utf8))
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {}
}
