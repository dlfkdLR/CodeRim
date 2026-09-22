import Foundation
import XCTest
import CodexBarCore
@testable import CodeRim

final class StepFunPasswordRecoveryTests: XCTestCase, @unchecked Sendable {
    final class Calls: @unchecked Sendable {
        private let lock = NSLock()
        private var values: [String] = []
        func append(_ value: String) { lock.withLock { values.append(value) } }
        var all: [String] { lock.withLock { values } }
    }
    private func context(manual: Bool = false, passwordLogin: Bool = true, off: Bool = false) -> ProviderFetchContext {
        let browser = BrowserDetection()
        var environment = ["STEPFUN_TOKEN": "environment-token", "STEPFUN_USERNAME": " user ", "STEPFUN_PASSWORD": " pass word "]
        if !passwordLogin { environment.removeValue(forKey: "STEPFUN_USERNAME"); environment.removeValue(forKey: "STEPFUN_PASSWORD") }
        return ProviderFetchContext(runtime: .app, sourceMode: .auto, includeCredits: false,
            webTimeout: 20, webDebugDumpHTML: false, verbose: false,
            env: environment,
            settings: .make(stepfun: .init(cookieSource: off ? .off : manual ? .manual : .auto, manualToken: manual ? "manual-token" : "")),
            fetcher: UsageFetcher(environment: [:]), claudeFetcher: ClaudeUsageFetcher(browserDetection: browser, environment: [:]), browserDetection: browser)
    }
    private static func snapshot() -> StepFunUsageSnapshot {
        StepFunUsageSnapshot(fiveHourUsageLeftRate: 0.67, weeklyUsageLeftRate: 0.5,
            fiveHourUsageResetTime: Date(timeIntervalSince1970: 1800000000), weeklyUsageResetTime: Date(timeIntervalSince1970: 1800000000),
            planName: "Fixture", updatedAt: Date(timeIntervalSince1970: 1799999999))
    }
    private static func result() -> ProviderFetchResult {
        .init(usage: snapshot().toUsageSnapshot(), credits: nil, dashboard: nil, sourceLabel: "web", strategyID: "stepfun.web", strategyKind: .web)
    }
    func testWorkingTokenOrCacheDoesNotStartPasswordLogin() async throws {
        let calls = Calls()
        _ = try await StepFunPasswordRecovery.fetch(ProviderDescriptorRegistry.descriptor(for: .stepfun), context: context(),
            upstream: { _, _ in XCTFail("Must preserve direct stage errors"); throw CancellationError() }, cached: { nil },
            login: { _, _ in XCTFail("Working token must not log in"); throw CancellationError() },
            refresh: { _ in XCTFail("Working token must not refresh"); throw CancellationError() },
            usage: { token in XCTAssertEqual(token, "environment-token"); calls.append("token"); return Self.snapshot() },
            cache: { _ in XCTFail("Working token must not mutate cache") })
        XCTAssertEqual(calls.all, ["token"])
    }
    func testAuthFailureUsesExactPasswordThenCachesOnlySuccessfulQuota() async throws {
        let calls = Calls()
        let result = try await StepFunPasswordRecovery.fetch(ProviderDescriptorRegistry.descriptor(for: .stepfun), context: context(),
            cached: { nil },
            login: { user, password in
                XCTAssertEqual(user, "user"); XCTAssertEqual(password, " pass word "); calls.append("login"); return "new-token"
            },
            refresh: { token in XCTAssertEqual(token, "environment-token"); calls.append("refresh"); throw StepFunUsageError.tokenRefreshFailed("HTTP 401") },
            usage: { token in
                if token == "environment-token" { calls.append("token"); throw StepFunUsageError.apiError("HTTP 401") }
                XCTAssertEqual(token, "new-token"); calls.append("usage"); return Self.snapshot()
            },
            cache: { token in XCTAssertEqual(token, "new-token"); calls.append("cache") })
        XCTAssertEqual(calls.all, ["token", "refresh", "login", "usage", "cache"])
        XCTAssertEqual(result.usage.primary?.usedPercent ?? -1, 33, accuracy: 0.00001)
    }
    func testManualTokenNeverUsesPasswordFallback() async {
        do {
            _ = try await StepFunPasswordRecovery.fetch(ProviderDescriptorRegistry.descriptor(for: .stepfun), context: context(manual: true),
                upstream: { _, context in
                    XCTAssertEqual(context.settings?.stepfun?.cookieSource, .manual)
                    XCTAssertEqual(context.settings?.stepfun?.manualToken, "manual-token")
                    throw StepFunUsageError.apiError("HTTP 401")
                }, cached: { XCTFail("Manual must not read auto cache"); return nil },
                login: { _, _ in XCTFail("Manual token switched account"); throw CancellationError() },
                refresh: { _ in XCTFail("Manual must use selected strategy"); throw CancellationError() },
                usage: { _ in XCTFail("Unexpected usage"); throw CancellationError() }, cache: { _ in XCTFail("Unexpected cache") })
            XCTFail("Expected authentication error")
        } catch { XCTAssertTrue(StepFunPasswordRecovery.authenticationFailure(error)) }
    }
    func testRefreshRateLimitServerParseAndCancellationCannotTriggerPasswordLogin() async {
        let errors: [Error] = [StepFunUsageError.tokenRefreshFailed("HTTP 429"), StepFunUsageError.tokenRefreshFailed("HTTP 503"),
                               StepFunUsageError.parseFailed("fixture"), StepFunUsageError.networkError("fixture"), CancellationError()]
        for error in errors {
            do {
                _ = try await StepFunPasswordRecovery.fetch(ProviderDescriptorRegistry.descriptor(for: .stepfun), context: context(),
                    cached: { nil }, login: { _, _ in XCTFail("Non-authentication failure retried login"); throw CancellationError() },
                    refresh: { _ in throw error },
                    usage: { _ in throw StepFunUsageError.apiError("HTTP 401") }, cache: { _ in XCTFail("Unexpected cache") })
                XCTFail("Expected refresh error")
            } catch { XCTAssertFalse(StepFunPasswordRecovery.authenticationFailure(error)) }
        }
    }
    func testFailedFallbackQuotaIsNotCached() async {
        do {
            _ = try await StepFunPasswordRecovery.fetch(ProviderDescriptorRegistry.descriptor(for: .stepfun), context: context(),
                cached: { nil }, login: { _, _ in "new-token" },
                refresh: { _ in throw StepFunUsageError.tokenRefreshFailed("HTTP 401") },
                usage: { token in throw StepFunUsageError.apiError(token == "environment-token" ? "HTTP 401" : "HTTP 503") },
                cache: { _ in XCTFail("Failed quota cached") })
            XCTFail("Expected quota failure")
        } catch { XCTAssertFalse(StepFunPasswordRecovery.authenticationFailure(error)) }
    }
    func testSuccessfulRefreshSkipsPasswordAndCommitsRecoveredToken() async throws {
        let calls = Calls()
        _ = try await StepFunPasswordRecovery.fetch(ProviderDescriptorRegistry.descriptor(for: .stepfun), context: context(),
            cached: { "cached-token" }, login: { _, _ in XCTFail("Unnecessary password login"); throw CancellationError() },
            refresh: { token in XCTAssertEqual(token, "cached-token"); return "new-token" },
            usage: { token in
                if token == "cached-token" { throw StepFunUsageError.apiError("HTTP 401") }
                calls.append(token); return Self.snapshot()
            }, cache: { token in calls.append("cached:" + token) })
        XCTAssertEqual(calls.all, ["new-token", "cached:new-token"])
    }
    func testRejectedCacheTriesConfiguredTokenBeforePassword() async throws {
        let calls = Calls()
        _ = try await StepFunPasswordRecovery.fetch(ProviderDescriptorRegistry.descriptor(for: .stepfun), context: context(),
            cached: { "expired-cache" },
            login: { _, _ in XCTFail("Configured token was bypassed"); throw CancellationError() },
            refresh: { token in calls.append("refresh:" + token); throw StepFunUsageError.tokenRefreshFailed("HTTP 401") },
            usage: { token in
                calls.append("usage:" + token)
                if token == "expired-cache" { throw StepFunUsageError.apiError("HTTP 401") }
                XCTAssertEqual(token, "environment-token")
                return Self.snapshot()
            }, cache: { token in calls.append("cache:" + token) })
        XCTAssertEqual(calls.all, ["usage:expired-cache", "refresh:expired-cache", "usage:environment-token", "cache:environment-token"])
    }
    func testTokenOnlyUsesGuardedRefreshWithoutUpstreamCacheMutation() async throws {
        let calls = Calls()
        _ = try await StepFunPasswordRecovery.fetch(ProviderDescriptorRegistry.descriptor(for: .stepfun), context: context(passwordLogin: false),
            upstream: { _, _ in XCTFail("Token-only bypassed guarded refresh"); throw CancellationError() },
            cached: { "expired-cache" }, login: { _, _ in XCTFail("No password configured"); throw CancellationError() },
            refresh: { token in XCTAssertEqual(token, "expired-cache"); return "new-token" },
            usage: { token in
                if token == "expired-cache" { throw StepFunUsageError.apiError("HTTP 401") }
                return Self.snapshot()
            }, cache: { token in calls.append(token) })
        XCTAssertEqual(calls.all, ["new-token"])
    }
    func testOffSourceKeepsTheUpstreamUnavailableContract() async throws {
        let calls = Calls()
        _ = try await StepFunPasswordRecovery.fetch(ProviderDescriptorRegistry.descriptor(for: .stepfun), context: context(off: true),
            upstream: { _, context in
                XCTAssertEqual(context.settings?.stepfun?.cookieSource, .off)
                calls.append("upstream"); return Self.result()
            },
            cached: { XCTFail("Off mode read cache"); return nil },
            login: { _, _ in XCTFail("Off mode logged in"); throw CancellationError() })
        XCTAssertEqual(calls.all, ["upstream"])
    }
}
