import Foundation
import XCTest
@testable import CodexBarCore
import SweetCookieKit
@testable import CodeRim

final class SafeBrowserProviderFetchTests: XCTestCase, @unchecked Sendable {
    private static let token = "fixture-access-token-123456789"
    private static let refresh = "fixture-refresh-token-123456789"
    private static let next = "fixture-next-access-123456789"
    private static let nextRefresh = "fixture-next-refresh-123456789"
    final class State: @unchecked Sendable {
        private let lock = NSLock()
        private var _cache: String?, _changed = false, _phase = 0, _calls: [String] = []
        var cache: String? { lock.withLock { _cache } }
        var phase: Int { get { lock.withLock { _phase } } set { lock.withLock { _phase = newValue } } }
        var changed: Bool { get { lock.withLock { _changed } } set { lock.withLock { _changed = newValue } } }
        var calls: [String] { lock.withLock { _calls } }
        func call(_ value: String) { lock.withLock { _calls.append(value) } }
        func commit(_ expected: String?, _ value: String) -> Bool { lock.withLock { guard !_changed, _cache == expected else { return false }; _cache = value; return true } }
    }
    struct Transport: ProviderHTTPTransport {
        let handler: @Sendable (URLRequest) throws -> (Int, String)
        func data(for request: URLRequest) async throws -> (Data, URLResponse) {
            let (status, body) = try handler(request)
            return (Data(body.utf8), HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!)
        }
    }
    private func context(_ provider: CodexBarCore.UsageProvider, mode: ProviderSourceMode = .web, env: [String: String] = [:]) -> ProviderFetchContext {
        let browser = BrowserDetection()
        return ProviderFetchContext(runtime: .app, sourceMode: mode, includeCredits: false, includeOptionalUsage: false,
            webTimeout: 20, webDebugDumpHTML: false, verbose: false, env: env,
            settings: provider == .minimax ? .make(minimax: .init(cookieSource: .auto, manualCookieHeader: nil)) : .make(factory: .init(cookieSource: .auto, manualCookieHeader: nil)),
            fetcher: UsageFetcher(environment: [:]), claudeFetcher: ClaudeUsageFetcher(browserDetection: browser, environment: [:]), browserDetection: browser)
    }
    private func dependencies() -> SafeBrowserProviderFetch.Dependencies {
        var value = SafeBrowserProviderFetch.Dependencies()
        value.profiles = { _, _ in [] }; value.storage = { _, _, _ in [:] }; value.stores = { _ in [] }; value.cookies = { _, _ in [] }
        value.safariFactory = { [] }; value.storedFactoryCookies = { [] }; value.storedFactoryBearer = { nil }; value.storedFactoryRefresh = { nil }
        value.transport = Transport { _ in XCTFail("Unexpected provider request"); throw URLError(.unsupportedURL) }
        value.deepSeekAPI = { _, _, _ in XCTFail("Unexpected API request"); throw CancellationError() }
        value.deepSeekWeb = { _, _ in XCTFail("Unexpected web request"); throw CancellationError() }
        return value
    }
    private static func balance() -> DeepSeekUsageSnapshot { .init(isAvailable: true, currency: "USD", totalBalance: 10, grantedBalance: 2, toppedUpBalance: 8, updatedAt: Date(timeIntervalSince1970: 1800000000)) }
    private func fetch(_ provider: CodexBarCore.UsageProvider, context: ProviderFetchContext? = nil,
                       dependencies: SafeBrowserProviderFetch.Dependencies, state: State = State()) async throws -> ProviderFetchResult {
        try await SafeBrowserProviderFetch.$automaticAllowed.withValue(true) {
            try await SafeBrowserProviderFetch.$cacheRead.withValue({ state.cache }) {
                try await SafeBrowserProviderFetch.$cacheCommit.withValue({ state.commit($0, $1) }) {
                    try await SafeBrowserProviderFetch.fetch(ProviderDescriptorRegistry.descriptor(for: provider), context: context ?? self.context(provider), dependencies: dependencies)
                }
            }
        }
    }
    func testDeepSeekAPIWithoutScopedDetailsNeverReadsBrowser() async throws {
        var deps = dependencies(); deps.profiles = { _, _ in XCTFail("Unscoped API read a browser"); throw CancellationError() }
        deps.deepSeekAPI = { key, token, optional in XCTAssertEqual(key, "fixture-api-key"); XCTAssertNil(token); XCTAssertFalse(optional); return Self.balance() }
        let result = try await fetch(.deepseek, context: context(.deepseek, mode: .api, env: ["DEEPSEEK_API_KEY": "fixture-api-key"]), dependencies: deps)
        XCTAssertEqual(result.sourceLabel, "api")
    }
    func testDeepSeekAccountReplacementDiscardsLateSuccessAndError() async {
        for fails in [false, true] {
            let state = State(); var deps = dependencies()
            deps.profiles = { _, _ in [.init(browser: .chrome, url: URL(fileURLWithPath: "/fixture/Default"))] }
            deps.storage = { _, _, _ in ["userToken": state.changed ? Self.next : Self.token] }
            deps.deepSeekWeb = { token, _ in XCTAssertEqual(token, Self.token); state.changed = true; if fails { throw URLError(.timedOut) }; return Self.balance() }
            do { _ = try await fetch(.deepseek, dependencies: deps, state: state); XCTFail("Late account response accepted") }
            catch { XCTAssertEqual(error as? LocalStorageReadError, .changed) }
        }
    }
    func testDeepSeekAmbiguousCurrentProfilesNeverChooseOne() async {
        var deps = dependencies(); deps.profiles = { _, _ in ["Default", "Profile 1"].map { .init(browser: .chrome, url: URL(fileURLWithPath: "/fixture/" + $0)) } }
        deps.storage = { _, _, _ in ["userToken": Self.token] }
        do { _ = try await fetch(.deepseek, dependencies: deps); XCTFail("Ambiguous selection accepted") } catch { XCTAssertTrue(error is ProviderFetchClassifiedError) }
    }
    private static func factoryReply(_ request: URLRequest) -> (Int, String) {
        if request.url?.path == "/api/app/auth/me" { return (200, #"{"organization":{"id":"org_1","subscription":{"factoryTier":"team"}},"userProfile":{"id":"user-1","email":"fixture@example.invalid"}}"#) }
        if request.url?.path == "/api/billing/limits" { return (404, "{}") }
        if request.url?.path == "/api/organization/subscription/usage" { return (200, #"{"usage":{"standard":{"userTokens":100,"totalAllowance":1000,"usedRatio":0.1}},"userId":"user-1"}"#) }
        return (404, "{}")
    }
    private func factoryDependencies(_ state: State, changeOnRefresh: Bool = false, failQuota: Bool = false) -> SafeBrowserProviderFetch.Dependencies {
        var deps = dependencies(); deps.profiles = { _, _ in [.init(browser: .chrome, url: URL(fileURLWithPath: "/fixture/Default"))] }
        deps.storage = { _, origin, _ in origin == "https://app.factory.ai" ? ["workos:access-token": state.changed ? Self.next : Self.token, "workos:refresh-token": Self.refresh] : [:] }
        deps.transport = Transport { request in
            guard let url = request.url else { throw URLError(.badURL) }; state.call(url.host! + url.path)
            if url.host == "api.workos.com" {
                XCTAssertEqual(url.absoluteString, "https://api.workos.com/user_management/authenticate")
                XCTAssertEqual(request.httpMethod, "POST"); XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
                let body = try XCTUnwrap(try JSONSerialization.jsonObject(with: request.httpBody!) as? [String: String])
                XCTAssertEqual(body["refresh_token"], Self.refresh); XCTAssertEqual(body["grant_type"], "refresh_token")
                if changeOnRefresh { state.changed = true }
                return (200, "{\"access_token\":\"\(Self.next)\",\"refresh_token\":\"\(Self.nextRefresh)\"}")
            }
            XCTAssertTrue(["app.factory.ai", "auth.factory.ai", "api.factory.ai"].contains(url.host!))
            if request.value(forHTTPHeaderField: "Authorization") == "Bearer " + Self.token { return (401, "{}") }
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer " + Self.next)
            return failQuota ? (503, "{}") : Self.factoryReply(request)
        }
        return deps
    }
    func testFactoryRotationUsesActualPublicFetcherAndRetainsFirstQuota() async throws {
        let state = State(), deps = factoryDependencies(state)
        let first = try await fetch(.factory, dependencies: deps, state: state)
        XCTAssertEqual(first.usage.primary?.usedPercent, 10); XCTAssertEqual(first.sourceLabel, "web")
        XCTAssertTrue(state.cache?.hasPrefix("CodeRimFactory1:") == true)
        let second = try await fetch(.factory, dependencies: deps, state: state)
        XCTAssertEqual(second.usage.primary?.usedPercent, 10)
        XCTAssertEqual(state.calls.filter { $0.hasPrefix("api.workos.com") }.count, 1)
    }
    func testFactoryRotatedRefreshSurvivesLaterQuotaFailure() async {
        let state = State()
        do { _ = try await fetch(.factory, dependencies: factoryDependencies(state, failQuota: true), state: state); XCTFail("Expected quota error") } catch { XCTAssertNotNil(state.cache) }
    }
    func testFactoryChangedProfileCannotCommitRotatedToken() async {
        let state = State()
        do { _ = try await fetch(.factory, dependencies: factoryDependencies(state, changeOnRefresh: true), state: state); XCTFail("Changed source accepted") }
        catch { XCTAssertEqual(error as? LocalStorageReadError, .changed); XCTAssertNil(state.cache) }
    }
    func testFactoryRefreshSemanticAndTransportBounds() async {
        for (status, body) in [(200, #"{"access_token":"first-fixture-token-123456","access_token":"second-fixture-token-123456"}"#), (200, "{}"), (429, "{}"), (503, "{}"), (200, String(repeating: " ", count: 65537))] {
            let state = State(); let transport = Transport { _ in state.call("request"); return (status, body) }
            do { _ = try await SafeBrowserProviderFetch.factoryRefresh(refresh: Self.refresh, group: nil, transport: transport); XCTFail("Invalid response accepted") } catch { XCTAssertEqual(state.calls.count, 1) }
        }
    }
    func testCookieURIScopeExpiryAndConflictingNames() throws {
        func cookie(_ domain: String, _ path: String, _ value: String, _ scope: BrowserCookieScope = .hostOnly, expiry: Date? = nil) -> BrowserCookieRecord {
            .init(domain: domain, name: "session", path: path, value: value, expires: expiry, isSecure: true, isHTTPOnly: true, scope: scope)
        }
        let url = URL(string: "https://api.workos.com/user_management/authenticate")!
        XCTAssertEqual(try SafeBrowserProviderFetch.cookieHeader([cookie("workos.com", "/", "domain", .domain)], for: url), "session=domain")
        for record in [cookie("workos.com", "/", "wrong"), cookie("api.workos.com", "/user", "wrong"), cookie("api.workos.com.evil", "/", "wrong"), cookie("api.workos.com", "/", "expired", expiry: Date(timeIntervalSince1970: 1))] {
            XCTAssertEqual(try SafeBrowserProviderFetch.cookieHeader([record], for: url), "")
        }
        XCTAssertThrowsError(try SafeBrowserProviderFetch.cookieHeader([cookie("api.workos.com", "/", "one"), cookie("api.workos.com", "/", "two")], for: url))
    }
    func testMiniMaxRequestCannotForwardBearerWithoutMatchingCookieTuple() async {
        let transport = Transport { _ in XCTFail("Invalid URI cookie tuple was transmitted"); throw CancellationError() }
        let records: [BrowserCookieRecord] = [
            .init(domain: "platform.minimax.io", name: "HERTZ-SESSION", path: "/user-center", value: Self.token, expires: nil, isSecure: true, isHTTPOnly: true),
            .init(domain: "minimax.io", name: "group_id", path: "/", value: "123", expires: nil, isSecure: true, isHTTPOnly: true, scope: .domain)]
        let scoped = SafeBrowserProviderFetch.CookieTransport(base: transport, records: records, allowedHosts: ["api.minimax.io", "platform.minimax.io"], requiredHertz: Self.token, requiredGroup: "123")
        for address in ["https://api.minimax.io/v1/api/remains", "https://platform.minimax.io/api/remains"] {
            var request = URLRequest(url: URL(string: address)!); request.setValue("Bearer " + Self.next, forHTTPHeaderField: "Authorization")
            do { _ = try await scoped.data(for: request); XCTFail("URI mismatch accepted") } catch { XCTAssertEqual(error as? LocalStorageReadError, .changed) }
        }
        let shadowed = SafeBrowserProviderFetch.CookieTransport(base: transport, records: [
            .init(domain: "minimax.io", name: "HERTZ-SESSION", path: "/", value: Self.token, expires: nil, isSecure: true, isHTTPOnly: true, scope: .domain),
            .init(domain: "api.minimax.io", name: "group_id", path: "/v1", value: "different-account", expires: nil, isSecure: true, isHTTPOnly: true)],
            allowedHosts: ["api.minimax.io"], requiredHertz: Self.token, expectedGroup: "123")
        do { _ = try await shadowed.data(for: URLRequest(url: URL(string: "https://api.minimax.io/v1/remains")!)); XCTFail("Path-specific group shadow accepted") }
        catch { XCTAssertEqual(error as? LocalStorageReadError, .changed) }
    }
    func testFactoryRateLimitServerErrorAndTimeoutDoNotRotateRefreshToken() async {
        for status in [429, 503, -1] {
            let state = State(); var deps = factoryDependencies(state)
            deps.transport = Transport { request in
                XCTAssertNotEqual(request.url?.host, "api.workos.com")
                if status == -1 { throw URLError(.timedOut) }; return (status, "{}")
            }
            do { _ = try await fetch(.factory, dependencies: deps, state: state); XCTFail("Expected provider error") } catch { XCTAssertNil(state.cache) }
        }
    }
    func testSavedFactoryBearerChangeDuringAuthErrorCannotFallBackToNewRefresh() async {
        let state = State(); var deps = dependencies()
        deps.storedFactoryBearer = { state.changed ? Self.next : Self.token }
        deps.storedFactoryRefresh = { XCTFail("Changed saved session was used for fallback"); return Self.nextRefresh }
        deps.transport = Transport { request in
            XCTAssertNotEqual(request.url?.host, "api.workos.com"); state.changed = true; return (401, "{}")
        }
        do { _ = try await fetch(.factory, dependencies: deps, state: state); XCTFail("Changed saved account accepted") }
        catch { XCTAssertEqual(error as? LocalStorageReadError, .changed); XCTAssertNil(state.cache) }
    }
    func testFactoryStoredCookiesPreserveHostPathAndDerivedBearerScope() async throws {
        let state = State(); var deps = dependencies()
        let cookie = HTTPCookie(properties: [.domain: "auth.factory.ai", .path: "/api/app", .name: "access-token", .value: Self.token, .secure: "TRUE"])!
        deps.storedFactoryCookies = { [cookie] }
        deps.transport = Transport { request in
            state.call(request.url!.host! + request.url!.path)
            if request.url?.host == "auth.factory.ai", request.url?.path.hasPrefix("/api/app/") == true {
                XCTAssertEqual(request.value(forHTTPHeaderField: "Cookie"), "access-token=" + Self.token)
                XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer " + Self.token)
                return Self.factoryReply(request)
            }
            XCTAssertEqual(request.value(forHTTPHeaderField: "Cookie"), "")
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
            if request.url?.host == "auth.factory.ai" { return Self.factoryReply(request) }
            if request.url?.path == "/api/billing/limits" { return (404, "{}") }
            return (401, "{}")
        }
        let result = try await fetch(.factory, dependencies: deps, state: state)
        XCTAssertEqual(result.usage.primary?.usedPercent, 10)
        XCTAssertFalse(state.calls.isEmpty)
    }
    private func factoryRecoveryDependencies(_ state: State, stored: Bool, secondStatus: Int, changeSource: Bool = false) -> SafeBrowserProviderFetch.Dependencies {
        var deps = dependencies()
        deps.storedFactoryRefresh = { stored ? (state.changed ? Self.nextRefresh : Self.refresh) : nil }
        deps.stores = { browser in browser == .chrome && !stored ? [.init(browser: .chrome,
            profile: .init(id: "/fixture/Default", name: "Default"), kind: .primary, label: "fixture",
            databaseURL: URL(fileURLWithPath: "/fixture/Default/Cookies"))] : [] }
        deps.cookies = { _, domains in domains == ["workos.com"] ? [.init(domain: "api.workos.com", name: "wos-session", path: "/",
            value: state.changed ? "changed-cookie" : "original-cookie", expires: nil, isSecure: true, isHTTPOnly: true)] : [] }
        deps.transport = Transport { request in
            let isRefresh = request.url?.host == "api.workos.com"
            state.call("\(state.phase):\(isRefresh ? "refresh" : "quota")")
            if isRefresh { return (200, "{\"access_token\":\"\(Self.next)\",\"refresh_token\":\"\(Self.nextRefresh)\"}") }
            if state.phase == 1 && secondStatus != 200 {
                if changeSource { state.changed = true }
                if secondStatus == -1 { throw URLError(.timedOut) }
                return (secondStatus, "{}")
            }
            return Self.factoryReply(request)
        }
        return deps
    }
    func testFactoryRotatedAccessReusesStoredAndCookieSessionsWithoutAnotherRefresh() async throws {
        for stored in [false, true] {
            let state = State()
            let selected = factoryRecoveryDependencies(state, stored: stored, secondStatus: 200)
            let first = try await fetch(.factory, dependencies: selected, state: state)
            state.phase = 1
            let second = try await fetch(.factory, dependencies: selected, state: state)
            XCTAssertEqual(first.usage.primary?.usedPercent, 10); XCTAssertEqual(second.usage.primary?.usedPercent, 10)
            XCTAssertEqual(state.calls.filter { $0.hasSuffix(":refresh") }.count, 1)
        }
    }
    func testFactoryChangedRecoverySourceStopsBeforeRefreshAfterAccessFailure() async throws {
        for stored in [false, true] {
            for status in [401, 403, 429, 503] {
                let state = State()
                let selected = factoryRecoveryDependencies(state, stored: stored, secondStatus: status, changeSource: true)
                _ = try await fetch(.factory, dependencies: selected, state: state)
                let cache = state.cache; state.phase = 1
                do { _ = try await fetch(.factory, dependencies: selected, state: state); XCTFail("Changed source accepted") }
                catch { XCTAssertEqual(error as? LocalStorageReadError, .changed) }
                XCTAssertEqual(state.calls.filter { $0 == "1:refresh" }.count, 0)
                XCTAssertEqual(state.cache, cache)
            }
        }
    }
    func testFactoryStoredRotatedAccessRefreshesOnlyForAuthenticationFailure() async throws {
        for status in [401, 403, 429, 503, -1] {
            let state = State()
            let deps = factoryRecoveryDependencies(state, stored: true, secondStatus: status)
            _ = try await fetch(.factory, dependencies: deps, state: state)
            state.phase = 1
            do { _ = try await fetch(.factory, dependencies: deps, state: state); XCTFail("Expected access failure") }
            catch { XCTAssertFalse(error is LocalStorageReadError) }
            XCTAssertEqual(state.calls.filter { $0 == "1:refresh" }.count, [401, 403].contains(status) ? 1 : 0)
        }
    }
    func testMiniMaxGroupOnlyStorageCannotBorrowUnprovedCookieAccount() async {
        let state = State(); var deps = dependencies()
        let profile = SafeBrowserProviderFetch.Profile(browser: .chrome, url: URL(fileURLWithPath: "/fixture/Default"))
        deps.profiles = { _, _ in [profile] }
        deps.stores = { _ in [.init(browser: .chrome, profile: .init(id: profile.url.path, name: "Default"), kind: .primary, label: "Chrome Default", databaseURL: profile.url.appendingPathComponent("Cookies"))] }
        deps.cookies = { _, _ in [.init(domain: "platform.minimax.io", name: "HERTZ-SESSION", path: "/", value: Self.token, expires: nil, isSecure: true, isHTTPOnly: true)] }
        deps.storage = { _, _, _ in ["group_id": "123"] }
        deps.transport = Transport { _ in state.call("transmitted"); throw URLError(.unsupportedURL) }
        do { _ = try await fetch(.minimax, dependencies: deps, state: state); XCTFail("Unproved group was accepted") }
        catch { XCTAssertEqual(error as? LocalStorageReadError, .invalid) }
        XCTAssertTrue(state.calls.isEmpty, "No request may attach an unrelated storage group")
    }
    func testMiniMaxRejectsMismatchedProfileAndCookieGroupBeforeHTTP() async {
        var deps = dependencies(); let profile = SafeBrowserProviderFetch.Profile(browser: .chrome, url: URL(fileURLWithPath: "/fixture/Default"))
        deps.profiles = { _, _ in [profile] }
        deps.stores = { _ in [.init(browser: .chrome, profile: .init(id: profile.url.path, name: "Default"), kind: .primary, label: "Chrome Default", databaseURL: profile.url.appendingPathComponent("Cookies"))] }
        deps.cookies = { _, _ in [
            .init(domain: "platform.minimax.io", name: "HERTZ-SESSION", path: "/", value: Self.token, expires: nil, isSecure: true, isHTTPOnly: true),
            .init(domain: "platform.minimax.io", name: "group_id", path: "/", value: "1", expires: nil, isSecure: true, isHTTPOnly: true)] }
        for storedGroup in [nil, "2"] as [String?] {
            deps.storage = { _, _, _ in var fields = ["access_token": Self.next]; fields["group_id"] = storedGroup; return fields }
            do { _ = try await fetch(.minimax, dependencies: deps); XCTFail("Mixed account accepted") } catch { XCTAssertEqual(error as? LocalStorageReadError, .invalid) }
        }
    }
}

@MainActor
final class SafeBrowserProviderCacheTests: XCTestCase {
    @MainActor private final class Holder { var provider: ExtendedNotchProvider? }
    nonisolated private static func result() -> ProviderFetchResult {
        .init(usage: CodexBarCore.UsageSnapshot(primary: .init(usedPercent: 10, windowMinutes: nil, resetsAt: nil, resetDescription: nil), secondary: nil, updatedAt: Date()), credits: nil, dashboard: nil, sourceLabel: "web", strategyID: "fixture", strategyKind: .web)
    }
    func testRotationKeepsCurrentRevisionAndClearRejectsLateCommit() async throws {
        // Upstream's explicit test store prevents Keychain calls even in an unusual test host.
        try await KeychainCacheStore.withImplicitTestStoreForTesting {
            for clearDuringFetch in [false, true] {
                let holder = Holder()
                var config = ExtendedProviderConfiguration(providerID: .factory); config.provider.cookieSource = .auto
                let provider = ExtendedNotchProvider(descriptor: ProviderDescriptorRegistry.descriptor(for: .factory), configuration: { config }, fetch: { _, _ in
                    XCTAssertTrue(SafeBrowserProviderFetch.automaticAllowed)
                    let expected = await SafeBrowserProviderFetch.cacheRead()
                    if clearDuringFetch { await MainActor.run { holder.provider?.forgetCachedCredential() } }
                    let committed = await SafeBrowserProviderFetch.cacheCommit(expected, "CodeRimFactory1:Zml4dHVyZQ==")
                    XCTAssertEqual(committed, !clearDuringFetch)
                    return Self.result()
                })
                holder.provider = provider; provider.forgetCachedCredential()
                do { let value = try await provider.fetchSnapshot(); XCTAssertFalse(clearDuringFetch); XCTAssertEqual(value.status, .ok) }
                catch { XCTAssertTrue(clearDuringFetch); XCTAssertTrue(error is CancellationError) }
                provider.forgetCachedCredential()
            }
        }
    }
    func testManualAndOffDoNotOptIntoAutomaticReader() async throws {
        for source in [ProviderCookieSource.manual, .off] {
            var config = ExtendedProviderConfiguration(providerID: .factory); config.provider.cookieSource = source; config.provider.cookieHeader = "session=fixture"
            let provider = ExtendedNotchProvider(descriptor: ProviderDescriptorRegistry.descriptor(for: .factory), configuration: { config }, fetch: { _, context in
                XCTAssertFalse(SafeBrowserProviderFetch.automaticAllowed); XCTAssertEqual(context.settings?.factory?.cookieSource, source)
                return Self.result()
            })
            _ = try await provider.fetchSnapshot()
        }
    }
}
