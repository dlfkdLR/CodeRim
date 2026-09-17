import Foundation
import XCTest
import CodexBarCore
@testable import CodeRim

@MainActor
final class ExtendedProviderAuditTests: XCTestCase {
    private func mappedCost(used: Double, limit: Double) -> ProviderSnapshot {
        let usage = CodexBarCore.UsageSnapshot(primary: nil, secondary: nil,
            providerCost: .init(used: used, limit: limit, currencyCode: "USD", updatedAt: Date()), updatedAt: Date())
        return ExtendedNotchProvider.snapshot(.init(usage: usage, credits: nil, dashboard: nil,
            sourceLabel: "fixture", strategyID: "fixture", strategyKind: .apiToken),
            descriptor: ProviderDescriptorRegistry.descriptor(for: .openrouter))
    }

    func testCostRatioCannotOverflowPercentageOrSnapshotEncoding() throws {
        for (used, limit) in [(Double.greatestFiniteMagnitude, Double.leastNonzeroMagnitude), (1e20, 1.0), (-1.0, 10.0)] {
            let snapshot = mappedCost(used: used, limit: limit)
            XCTAssertNil(snapshot.usedFraction, "Invalid ratio must remain textual")
            if snapshot.usedFraction == nil { XCTAssertFalse(snapshot.headlineText.isEmpty) }
            XCTAssertNoThrow(try JSONEncoder().encode(snapshot.windows))
        }
        XCTAssertEqual(mappedCost(used: 120, limit: 100).usedFraction, 1.2)
    }

    func testDetailRatioCannotOverflowPercentage() throws {
        let row = try ProviderDetailSection.Row(label: "Budget", value: "Reported total",
            progress: .init(used: 1e20, total: 1))
        let usage = CodexBarCore.UsageSnapshot(primary: nil, secondary: nil,
            details: [try .init(rows: [row])], updatedAt: Date())
        let snapshot = ExtendedNotchProvider.snapshot(.init(usage: usage, credits: nil, dashboard: nil,
            sourceLabel: "fixture", strategyID: "fixture", strategyKind: .apiToken),
            descriptor: ProviderDescriptorRegistry.descriptor(for: .openrouter))
        XCTAssertNil(snapshot.usedFraction)
        XCTAssertEqual(snapshot.windows.first?.summary, "Reported total")
    }

    func testMalformedManualAugmentCookiesNeverEnableBrowserImport() {
        for header in ["Cookie:", "Cookie: ", "\"\""] {
            var config = ExtendedProviderConfiguration(providerID: .augment)
            config.provider.cookieHeader = header
            XCTAssertEqual(config.settings?.augment?.cookieSource, .off, header)
        }
    }

    func testSaveDuringBatchRefreshReadsNewAccountImmediately() async throws {
        let provider = SuspendedAuditProvider()
        let store = makeStore(provider)
        let batch = Task { await store.refresh() }
        await waitUntil { provider.calls == 1 }
        store.invalidateAccount(providerID: provider.id)
        store.refresh(providerID: provider.id)
        provider.complete(0.9)
        await batch.value
        await waitUntil { provider.calls == 2 }
        provider.complete(0.2)
        await waitUntil { store.snapshots.first?.usedFraction == 0.2 }
        XCTAssertEqual(store.snapshots.first?.usedFraction, 0.2)
    }

    func testSaveDuringSingleRefreshCoalescesAndRetainsNewResponse() async throws {
        let provider = SuspendedAuditProvider()
        let store = makeStore(provider)
        store.refresh(providerID: provider.id)
        await waitUntil { provider.calls == 1 }
        store.invalidateAccount(providerID: provider.id)
        for _ in 0..<3 { store.refresh(providerID: provider.id) }
        provider.complete(0.9)
        await waitUntil { provider.calls == 2 }
        provider.complete(0.3)
        await waitUntil { store.snapshots.first?.usedFraction == 0.3 && store.refreshing.isEmpty }
        XCTAssertEqual(provider.calls, 2)
        XCTAssertEqual(store.snapshots.first?.usedFraction, 0.3)
    }

    func testRemovedProviderCannotRestartQueuedRefresh() async throws {
        let provider = SuspendedAuditProvider()
        let store = makeStore(provider)
        store.refresh(providerID: provider.id)
        await waitUntil { provider.calls == 1 }
        store.refresh(providerID: provider.id)
        store.disconnected = [provider.id]
        provider.complete(0.9)
        await waitUntil { store.refreshing.isEmpty }
        XCTAssertEqual(provider.calls, 1)
        XCTAssertTrue(store.snapshots.isEmpty)
    }

    func testExtendedProviderRejectsLateAccountResponse() async throws {
        let gate = AuditFetchGate()
        let descriptor = ProviderDescriptorRegistry.descriptor(for: .openrouter)
        let provider = ExtendedNotchProvider(descriptor: descriptor, configuration: { .init(providerID: .openrouter) }, fetch: { _, _ in
            await gate.wait()
        })
        let fetch = Task { try await provider.fetchSnapshot() }
        await waitUntil { gate.waiting }
        provider.forgetCachedCredential()
        gate.complete()
        do { _ = try await fetch.value; XCTFail("Old account result accepted") }
        catch is CancellationError {} catch { XCTFail("Unexpected error") }
        XCTAssertNil(provider.account())
    }

    func testTypedHTTPFailuresDoNotClassifyBySecretBearingMessageText() {
        XCTAssertEqual(ExtendedProviderFailureClassifier.status(for: CodexBarCore.WarpUsageError.apiError(401, "secret"), provider: "Warp"), .needsAuth)
        guard case .unsupported = ExtendedProviderFailureClassifier.status(for: CodexBarCore.WarpUsageError.apiError(403, "secret"), provider: "Warp") else { return XCTFail("Expected permission guidance") }
        XCTAssertNil(ExtendedProviderFailureClassifier.status(for: CodexBarCore.WarpUsageError.apiError(429, "401 secret"), provider: "Warp"))
        XCTAssertNil(ExtendedProviderFailureClassifier.status(for: CodexBarCore.LiteLLMUsageError.apiError("401 private response"), provider: "LiteLLM"))
    }

    func testUnclassifiedFailureClearsOldAccountAndUsageWithoutLeakingBody() async throws {
        var fails = false
        let fixture = ProviderFetchResult(usage: .init(primary: .init(usedPercent: 25, windowMinutes: nil, resetsAt: nil, resetDescription: nil), secondary: nil, updatedAt: Date()), credits: nil, dashboard: nil, sourceLabel: "fixture", strategyID: "fixture", strategyKind: .apiToken)
        let provider = ExtendedNotchProvider(descriptor: ProviderDescriptorRegistry.descriptor(for: .litellm), configuration: { .init(providerID: .litellm) }, fetch: { _, _ in
            if await MainActor.run(body: { fails }) { throw CodexBarCore.LiteLLMUsageError.apiError("401 private response") }
            return fixture
        })
        let success = try await provider.fetchSnapshot()
        XCTAssertTrue(success.hasReading)
        XCTAssertNotNil(provider.account())
        fails = true
        let snapshot = try await provider.fetchSnapshot()
        guard case .error = snapshot.status else { return XCTFail("Expected explicit failed refresh") }
        XCTAssertTrue(snapshot.windows.isEmpty)
        XCTAssertNil(provider.account())
        XCTAssertFalse(snapshot.statusMessage?.contains("private response") == true)
    }

    func testQueuedExplicitRefreshPreservesUserInteractionContext() async {
        let provider = SuspendedAuditProvider()
        let store = makeStore(provider)
        store.refresh(providerID: provider.id)
        await waitUntil { provider.calls == 1 }
        ProviderInteractionContext.$current.withValue(.userInitiated) { store.refresh(providerID: provider.id) }
        provider.complete(0.1)
        await waitUntil { provider.calls == 2 }
        XCTAssertEqual(provider.interactions, [.background, .userInitiated])
        provider.complete(0.2)
        await waitUntil { store.refreshing.isEmpty }
    }

    func testTargetedRefreshDoesNotGetOverwrittenByLaterBatchResponse() async {
        let first = SuspendedAuditProvider()
        let second = SuspendedAuditProvider(id: "elevenlabs")
        let suite = "CodeRim.ProviderAudit.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = NotchUsageStore(providers: [first, second], archive: UsageArchive(defaults: defaults))
        let batch = Task { await store.refresh() }
        await waitUntil { first.calls == 1 }
        store.invalidateAccount(providerID: first.id)
        store.refresh(providerID: first.id)
        first.complete(0.9)
        await waitUntil { second.calls == 1 && first.calls == 2 }
        first.complete(0.2)
        await waitUntil { store.snapshots.first(where: { $0.id == first.id })?.usedFraction == 0.2 }
        second.complete(0.4)
        await batch.value
        XCTAssertEqual(store.snapshots.first(where: { $0.id == first.id })?.usedFraction, 0.2)
        XCTAssertEqual(store.snapshots.first(where: { $0.id == second.id })?.usedFraction, 0.4)
        await waitUntil { store.refreshing.isEmpty }
    }

    func testAlibabaDocumentedSettingsAndManualEnvironmentCookieReachFetcher() throws {
        var config = ExtendedProviderConfiguration(providerID: .alibaba)
        config.environment = ["ALIBABA_CODING_PLAN_COOKIE": "session=fixture",
            "ALIBABA_CODING_PLAN_HOST": "bailian.console.aliyun.com",
            "ALIBABA_CODING_PLAN_REQUIRE_PROVIDER_ENDPOINT_OVERRIDES": "true"]
        let env = config.fetchEnvironment(base: [:])
        XCTAssertEqual(env["ALIBABA_CODING_PLAN_HOST"], "bailian.console.aliyun.com")
        XCTAssertEqual(env["ALIBABA_CODING_PLAN_REQUIRE_PROVIDER_ENDPOINT_OVERRIDES"], "true")
        XCTAssertEqual(config.settings(environment: env)?.alibaba?.cookieSource, .manual)
        XCTAssertEqual(config.settings(environment: env)?.alibaba?.manualCookieHeader, "session=fixture")
        XCTAssertEqual(ExtendedProviderGuides.all["alibaba"]?.document, "alibaba-coding-plan.md")
    }

    private func makeStore(_ provider: SuspendedAuditProvider) -> NotchUsageStore {
        let suite = "CodeRim.ProviderAudit.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        addTeardownBlock { defaults.removePersistentDomain(forName: suite) }
        return NotchUsageStore(providers: [provider], archive: UsageArchive(defaults: defaults))
    }

    private func waitUntil(_ condition: () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        for _ in 0..<100 {
            if condition() { return }
            try? await Task.sleep(for: .milliseconds(10))
        }
        XCTFail("Expected state did not arrive", file: file, line: line)
    }
}

@MainActor
private final class SuspendedAuditProvider: NotchProvider {
    let id: String
    init(id: String = "openrouter") { self.id = id }
    let displayName = "OpenRouter"
    var glyph: ProviderGlyph { .third }
    var isVisibleWhenAbsent: Bool { true }
    var signInRoute: SignInRoute { .guidance("Fixture") }
    var calls = 0
    var interactions: [ProviderInteraction] = []
    private var continuation: CheckedContinuation<ProviderSnapshot, Never>?
    func account() -> ProviderAccount? { nil }
    func fetchSnapshot() async throws -> ProviderSnapshot {
        calls += 1
        interactions.append(ProviderInteractionContext.current)
        return await withCheckedContinuation { continuation = $0 }
    }
    func complete(_ fraction: Double) {
        continuation?.resume(returning: .init(id: id, displayName: displayName, glyph: glyph,
            fidelity: .official, status: .ok, windows: [.init(id: "quota", label: "Quota", usedFraction: fraction)]))
        continuation = nil
    }
}

@MainActor
private final class AuditFetchGate {
    var waiting: Bool { continuation != nil }
    private var continuation: CheckedContinuation<ProviderFetchResult, Never>?
    func wait() async -> ProviderFetchResult { await withCheckedContinuation { continuation = $0 } }
    func complete() {
        continuation?.resume(returning: .init(usage: .init(primary: nil, secondary: nil, updatedAt: Date(),
            identity: .init(providerID: .openrouter, accountEmail: "old@example.invalid", accountOrganization: nil, loginMethod: "Old")),
            credits: nil, dashboard: nil, sourceLabel: "fixture", strategyID: "fixture", strategyKind: .apiToken))
        continuation = nil
    }
}
