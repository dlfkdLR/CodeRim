import Foundation
import CodexBarCore
import SweetCookieKit

/// Replaces only opted-in automatic storage discovery. Public provider fetchers retain quota semantics.
/// In particular, a failed current-state read never delegates back to upstream's historical/raw reader.
enum SafeBrowserProviderFetch {
    @TaskLocal static var automaticAllowed = false
    @TaskLocal static var cacheRead: @Sendable () async -> String? = { nil }
    @TaskLocal static var cacheCommit: @Sendable (String?, String) async -> Bool = { _, _ in false }
    struct Profile: Sendable, Equatable {
        let browser: Browser
        let url: URL
        var id: String { browser.rawValue + ":" + url.lastPathComponent }
        var label: String { browser.displayName + " " + url.lastPathComponent }
    }
    struct Dependencies: Sendable {
        var profiles: @Sendable ([Browser], BrowserDetection) throws -> [Profile] = { try liveProfiles($0, $1) }
        var storage: @Sendable (URL, String, [String]) throws -> [String: String] = {
            try BoundedLocalStorageReader.read(directory: $0.appendingPathComponent("Local Storage/leveldb"), origin: $1, keys: $2)
        }
        var stores: @Sendable (Browser) -> [BrowserCookieStore] = { BrowserCookieClient().stores(for: $0) }
        var cookies: @Sendable (BrowserCookieStore, [String]) throws -> [BrowserCookieRecord] = { try liveCookies($0, $1) }
        var transport: any ProviderHTTPTransport = StorageHTTPTransport()
        var safariFactory: @Sendable () throws -> [SafeBrowserSessionCredential] = {
            try SafeSafariLocalStorageReader.factoryCredentials(root: FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Library/Containers/com.apple.Safari/Data/Library/WebKit/WebsiteData/Default"))
        }
        var storedFactoryCookies: @Sendable () async -> [HTTPCookie] = { await FactorySessionStore.shared.getCookies() }
        var storedFactoryBearer: @Sendable () async -> String? = { await FactorySessionStore.shared.getBearerToken() }
        var storedFactoryRefresh: @Sendable () async -> String? = { await FactorySessionStore.shared.getRefreshToken() }
        var deepSeekAPI: @Sendable (String, String?, Bool) async throws -> DeepSeekUsageSnapshot = {
            try await DeepSeekUsageFetcher.fetchUsage(apiKey: $0, platformToken: $1, includeOptionalUsage: $2)
        }
        var deepSeekWeb: @Sendable (String, Bool) async throws -> DeepSeekUsageSnapshot = {
            try await DeepSeekUsageFetcher.fetchPlatformUsage(platformToken: $0, includeOptionalUsage: $1)
        }
    }
    private static let chromium: [Browser] = [.chrome, .chromeBeta, .chromeCanary, .edge, .edgeBeta, .edgeCanary,
        .brave, .braveBeta, .braveNightly, .vivaldi, .arc, .arcBeta, .arcCanary, .dia, .chatgptAtlas, .chromium, .helium]

    static func fetch(_ descriptor: ProviderDescriptor, context: ProviderFetchContext,
                      dependencies: Dependencies = Dependencies()) async throws -> ProviderFetchResult {
        guard automaticAllowed else { return try await descriptor.fetch(context: context) }
        switch descriptor.id {
        case .deepseek: return try await deepSeek(context, dependencies)
        case .minimax:
            if context.settings?.minimax?.cookieSource != .auto { return try await descriptor.fetch(context: context) }
            return try await miniMax(context, dependencies)
        case .factory:
            if context.settings?.factory?.cookieSource != .auto { return try await descriptor.fetch(context: context) }
            return try await factory(context, dependencies)
        default: return try await descriptor.fetch(context: context)
        }
    }
    private static func result(_ usage: CodexBarCore.UsageSnapshot, _ id: String, _ source: String) -> ProviderFetchResult {
        ProviderFetchResult(usage: usage, credits: nil, dashboard: nil, sourceLabel: source,
            strategyID: id + "." + source, strategyKind: source == "api" ? .apiToken : .web)
    }
    private static func liveProfiles(_ browsers: [Browser], _ detection: BrowserDetection) throws -> [Profile] {
        var profiles: [Profile] = []
        let roots = ChromiumProfileLocator.roots(for: browsers.browsersWithProfileData(using: detection),
            homeDirectories: BrowserCookieClient.defaultHomeDirectories())
        guard roots.count <= 64 else { throw LocalStorageReadError.limit }
        for root in roots {
            do { profiles += try BoundedLocalStorageReader.profileDirectories(root: root.url).map { Profile(browser: root.browser, url: $0) } }
            catch LocalStorageReadError.missing { continue }
        }
        guard profiles.count <= 64 else { throw LocalStorageReadError.limit }; return profiles
    }
    private static func liveCookies(_ store: BrowserCookieStore, _ domains: [String]) throws -> [BrowserCookieRecord] {
        // Same opt-in/access policy as the upstream adapter, including no interactive fallback during polling.
        guard BrowserCookieAccessGate.shouldAttempt(store.browser) else { throw LocalStorageReadError.missing }
        do {
            let read = { try BrowserCookieClient().records(matching: BrowserCookieQuery(domains: domains), in: store) }
            let records = try ProviderInteractionContext.current == .background
                ? BrowserCookieKeychainAccessGate.withUserInteractionDisallowed(read) : read()
            guard records.count <= 256, records.reduce(0, { $0 + $1.name.utf8.count + $1.value.utf8.count + $1.domain.utf8.count + $1.path.utf8.count }) <= 65536 else { throw LocalStorageReadError.limit }
            return records
        } catch { BrowserCookieAccessGate.recordIfNeeded(error); throw error }
    }
    private static func read(_ provider: String, profile: Profile, origin: String, cn: Bool = false,
                             dependencies: Dependencies) throws -> SafeBrowserSessionCredential? {
        let keys = provider == "deepseek" ? SafeBrowserSessionCredentials.deepSeekKeys
            : provider == "factory" ? SafeBrowserSessionCredentials.factoryKeys : SafeBrowserSessionCredentials.miniMaxKeys
        return try SafeBrowserSessionCredentials.credential(provider: provider, profileID: profile.id, profileURL: profile.url,
            origin: origin, values: dependencies.storage(profile.url, origin, keys), cn: cn)
    }
    private static func unchanged(_ credential: SafeBrowserSessionCredential, provider: String, profile: Profile,
                                  cn: Bool = false, dependencies: Dependencies) throws {
        try Task.checkCancellation()
        guard try read(provider, profile: profile, origin: credential.origin, cn: cn, dependencies: dependencies) == credential else {
            throw LocalStorageReadError.changed
        }
    }
    private static func deepSeek(_ context: ProviderFetchContext, _ dependencies: Dependencies) async throws -> ProviderFetchResult {
        guard [.auto, .api, .web].contains(context.sourceMode) else { throw DeepSeekUsageError.missingCredentials }
        let api = DeepSeekSettingsReader.apiKey(environment: context.env)
        let apiMode = context.sourceMode == .api || (context.sourceMode == .auto && api != nil)
        let expectedScope = DeepSeekSettingsReader.profileScope(selectedTokenAccountID: context.selectedTokenAccountID, apiKey: api)
        let scopeMatches = expectedScope != nil && expectedScope == DeepSeekSettingsReader.profileScope(environment: context.env)
        let manual = DeepSeekSettingsReader.platformToken(environment: context.env)
        if let manual, scopeMatches || api == nil {
            if apiMode {
                guard let api else { throw DeepSeekUsageError.missingCredentials }
                return result(try await dependencies.deepSeekAPI(api, manual, context.includeOptionalUsage).toUsageSnapshot(), "deepseek", "api")
            }
            return result(try await dependencies.deepSeekWeb(manual, context.includeOptionalUsage).toUsageSnapshot(), "deepseek", "web")
        }
        // API mode never borrows an unscoped browser account for optional details.
        if apiMode && (!context.includeOptionalUsage || !scopeMatches) {
            guard let api else { throw DeepSeekUsageError.missingCredentials }
            return result(try await dependencies.deepSeekAPI(api, nil, false).toUsageSnapshot(), "deepseek", "api")
        }
        if api != nil && !scopeMatches { throw ProviderFetchClassifiedError(kind: .missingCredential, message: "Select the DeepSeek browser profile for this API account.") }
        let selectedID = scopeMatches ? DeepSeekSettingsReader.profileID(environment: context.env) : nil
        let profiles: [Profile]
        do { profiles = try dependencies.profiles([.chrome], context.browserDetection) }
        catch { try Task.checkCancellation(); if apiMode, let api { return result(try await dependencies.deepSeekAPI(api, nil, false).toUsageSnapshot(), "deepseek", "api") }; throw error }
        var candidates: [(Profile, SafeBrowserSessionCredential)] = []
        for profile in profiles where selectedID == nil || profile.id == selectedID {
            do { if let credential = try read("deepseek", profile: profile, origin: "https://platform.deepseek.com", dependencies: dependencies) { candidates.append((profile, credential)) } }
            catch LocalStorageReadError.missing { continue }
            catch { try Task.checkCancellation(); if apiMode, let api { return result(try await dependencies.deepSeekAPI(api, nil, false).toUsageSnapshot(), "deepseek", "api") }; throw error }
        }
        guard candidates.count == 1, let (profile, credential) = candidates.first, let token = credential.token else {
            if apiMode, let api { return result(try await dependencies.deepSeekAPI(api, nil, false).toUsageSnapshot(), "deepseek", "api") }
            throw ProviderFetchClassifiedError(kind: .missingCredential, message: "Choose one current DeepSeek Chrome profile in provider settings.")
        }
        do {
            let usage = apiMode ? try await dependencies.deepSeekAPI(api!, token, context.includeOptionalUsage)
                : try await dependencies.deepSeekWeb(token, context.includeOptionalUsage)
            try unchanged(credential, provider: "deepseek", profile: profile, dependencies: dependencies)
            let bound = DeepSeekUsageSnapshot(hasBalance: usage.hasBalance, isAvailable: usage.isAvailable, currency: usage.currency,
                totalBalance: usage.totalBalance, grantedBalance: usage.grantedBalance, toppedUpBalance: usage.toppedUpBalance,
                usageSummary: usage.usageSummary, detailedUsageState: usage.detailedUsageState,
                platformProfiles: [DeepSeekPlatformProfile(id: profile.id, name: profile.label)],
                platformBalanceOwner: apiMode ? usage.platformBalanceOwner : DeepSeekPlatformBalanceOwner(profileID: profile.id, token: token), updatedAt: usage.updatedAt)
            return result(bound.toUsageSnapshot(), "deepseek", apiMode ? "api" : "web")
        } catch {
            try unchanged(credential, provider: "deepseek", profile: profile, dependencies: dependencies); throw error
        }
    }

    private static func miniMax(_ context: ProviderFetchContext, _ dependencies: Dependencies) async throws -> ProviderFetchResult {
        let region = context.settings?.minimax?.apiRegion ?? .global
        let cn = region == .chinaMainland
        let api = MiniMaxAPISettingsReader.apiToken(environment: context.env)
        if context.sourceMode == .api || (context.sourceMode == .auto && api != nil && MiniMaxAPISettingsReader.apiKeyKind(token: api) != .standard) {
            guard let api else { throw MiniMaxAPISettingsError.missingToken }
            do { return result(try await MiniMaxUsageFetcher.fetchUsage(apiToken: api, region: region, session: dependencies.transport).toUsageSnapshot(), "minimax", "api") }
            catch {
                try Task.checkCancellation()
                guard context.sourceMode == .auto, isMiniMaxFallback(error) else { throw error }
            }
        }
        guard context.sourceMode == .web || context.sourceMode == .auto else { throw MiniMaxSettingsError.missingCookie }
        let domain = cn ? "minimaxi.com" : "minimax.io"
        var lastError: Error = MiniMaxSettingsError.missingCookie
        // An old cache contains no verifiable profile identity. It remains a cookie-only alternative;
        // no localStorage bearer/group is ever attached to a display label.
        if let cached = await cacheRead(), !cached.isEmpty {
            do {
                let usage = try await MiniMaxUsageFetcher.fetchUsage(cookieHeader: cached, region: region,
                    environment: context.env, includeBillingHistory: context.includeOptionalUsage, session: dependencies.transport)
                guard await cacheRead() == cached else { throw LocalStorageReadError.changed }
                return result(usage.toUsageSnapshot(), "minimax", "web")
            }
            catch { try Task.checkCancellation(); if !isMiniMaxFallback(error) { throw error }; lastError = error }
        }
        let profiles = try dependencies.profiles(chromium, context.browserDetection)
        for profile in profiles {
            let stores = dependencies.stores(profile.browser).filter { $0.profile.id == profile.url.path }
            guard stores.count <= 4 else { throw LocalStorageReadError.limit }
            for store in stores {
                // Profile ID alone is insufficient when a crafted store names a different backing database.
                guard let database = store.databaseURL,
                      database == profile.url.appendingPathComponent("Cookies") || database == profile.url.appendingPathComponent("Network/Cookies") else { throw LocalStorageReadError.invalid }
                let records: [BrowserCookieRecord]
                do { records = try dependencies.cookies(store, [domain]) }
                catch LocalStorageReadError.missing { continue }
                let plan = URL(string: "https://platform.\(domain)/user-center/payment/coding-plan?cycle_type=3")!
                let header = try cookieHeader(records, for: plan)
                guard !header.isEmpty else { continue }
                let cookieValues = try cookieValues(header)
                guard let hertz = cookieValues["HERTZ-SESSION"], !hertz.isEmpty else { continue }
                let cookieGroup = try ["minimax_group_id_v2", "group_id", "groupid"].compactMap { cookieValues[$0] }.reduce(nil as String?) { try SafeBrowserSessionCredentials.merge($0, SafeBrowserSessionCredentials.identifier($1)) }
                func currentCredentials() throws -> [SafeBrowserSessionCredential] {
                    var values: [SafeBrowserSessionCredential] = []
                    for origin in SafeBrowserSessionCredentials.origins("minimax", cn: cn) {
                        do { if let value = try read("minimax", profile: profile, origin: origin, cn: cn, dependencies: dependencies) { values.append(value) } }
                        catch LocalStorageReadError.missing { continue }
                    }
                    return values
                }
                let credentials = try currentCredentials()
                func validateCurrent() throws {
                    try Task.checkCancellation()
                    guard try currentCredentials() == credentials,
                        try cookieFingerprint(dependencies.cookies(store, [domain])) == cookieFingerprint(records)
                    else { throw LocalStorageReadError.changed }
                }
                // Multiple current origins may repeat one credential, but conflicting accounts are not candidates to guess among.
                let tuples = Set(credentials.map { [$0.token ?? "", $0.groupID ?? ""].joined(separator: "\0") })
                guard tuples.count <= 1 else { throw LocalStorageReadError.invalid }
                let credential = credentials.first
                let group = try SafeBrowserSessionCredentials.merge(cookieGroup, credential?.groupID)
                if let token = credential?.token, token != hertz {
                    guard let storedGroup = credential?.groupID, let cookieGroup, storedGroup == cookieGroup else { throw LocalStorageReadError.invalid }
                }
                if let storedGroup = credential?.groupID, credential?.token != hertz {
                    guard cookieGroup == storedGroup else { throw LocalStorageReadError.invalid }
                }
                let scoped = CookieTransport(base: dependencies.transport, records: records, allowedHosts: Set(["platform." + domain, "api." + domain, "www." + domain, domain]), requiredHertz: hertz, requiredGroup: credential?.token != hertz ? credential?.groupID : nil, expectedGroup: group)
                do {
                    let usage = try await MiniMaxUsageFetcher.fetchUsage(cookieHeader: header, authorizationToken: credential?.token,
                        groupID: group, region: region, environment: context.env, includeBillingHistory: context.includeOptionalUsage, session: scoped)
                    try validateCurrent()
                    return result(usage.toUsageSnapshot(), "minimax", "web")
                } catch { try validateCurrent(); if !isMiniMaxFallback(error) { throw error }; lastError = error }
            }
        }
        // Safari/Firefox cookie-only paths are safe alternatives; they are not paired with Chromium storage.
        for browser in [Browser.safari, .firefox] {
            for store in dependencies.stores(browser) {
                let records = try dependencies.cookies(store, [domain]), plan = URL(string: "https://platform.\(domain)/user-center/payment/coding-plan")!
                let header = try cookieHeader(records, for: plan); if header.isEmpty { continue }
                do {
                    let scoped = CookieTransport(base: dependencies.transport, records: records, allowedHosts: Set(["platform." + domain, "api." + domain, "www." + domain, domain]))
                    let usage = try await MiniMaxUsageFetcher.fetchUsage(cookieHeader: header, region: region, environment: context.env, includeBillingHistory: context.includeOptionalUsage, session: scoped)
                    guard try cookieFingerprint(dependencies.cookies(store, [domain])) == cookieFingerprint(records) else { throw LocalStorageReadError.changed }
                    return result(usage.toUsageSnapshot(), "minimax", "web")
                } catch { try Task.checkCancellation(); if !isMiniMaxFallback(error) { throw error }; lastError = error }
            }
        }
        throw lastError
    }
    private static func isMiniMaxFallback(_ error: Error) -> Bool {
        if case MiniMaxUsageError.invalidCredentials = error { return true }
        if case MiniMaxUsageError.parseFailed = error { return true }
        if case let MiniMaxUsageError.apiError(message) = error { return message.contains("HTTP 404") }
        return false
    }

    private struct FactoryRotation: Codable {
        let version: Int
        let seed: String
        let access: String
        let refresh: String
    }
    private static func factory(_ context: ProviderFetchContext, _ dependencies: Dependencies) async throws -> ProviderFetchResult {
        let probe = FactoryStatusProbe(browserDetection: context.browserDetection, transport: dependencies.transport)
        let api = FactorySettingsReader.apiKey(environment: context.env)
        if context.sourceMode == .api || ((context.sourceMode == .auto || context.sourceMode == .cli) && api != nil) {
            guard let api else { throw FactoryStatusProbeError.missingAPIKey }
            do { return result(try await probe.fetch(apiKey: api).toUsageSnapshot(), "factory", "api") }
            catch { try Task.checkCancellation(); guard context.sourceMode != .api, factoryAuthFailure(error) else { throw error } }
        }
        guard [.auto, .web, .cli].contains(context.sourceMode) else { throw FactoryStatusProbeError.noSessionCookie }
        var lastError: Error = FactoryStatusProbeError.noSessionCookie
        let cache = await cacheRead()
        // Existing explicit session cookies are usable without any localStorage pairing.
        if let cached = cache, !cached.isEmpty, !cached.hasPrefix("CodeRimFactory1:") {
            do {
                let usage = try await probe.fetch(cookieHeaderOverride: cached)
                guard await cacheRead() == cache else { throw LocalStorageReadError.changed }
                return result(usage.toUsageSnapshot(), "factory", "web")
            }
            catch { try Task.checkCancellation(); guard await cacheRead() == cache else { throw LocalStorageReadError.changed }; guard factoryAuthFailure(error) else { throw error }; lastError = error }
        }
        // Retain previously explicit Factory login sessions, without writing to CodexBar's session store.
        let storedCookies = await dependencies.storedFactoryCookies()
        if !storedCookies.isEmpty {
            guard storedCookies.count <= 256 else { throw LocalStorageReadError.limit }
            let records = storedCookies.map { BrowserCookieRecord(domain: $0.domain, name: $0.name, path: $0.path,
                value: $0.value, expires: $0.expiresDate, isSecure: $0.isSecure, isHTTPOnly: $0.isHTTPOnly) }
            let header = try factoryCookieHeader(records)
            if !header.isEmpty {
                do {
                    let scoped = CookieTransport(base: dependencies.transport, records: records,
                        allowedHosts: ["app.factory.ai", "auth.factory.ai", "api.factory.ai"], deriveCookieBearer: true)
                    let usage = try await FactoryStatusProbe(browserDetection: context.browserDetection, transport: scoped).fetch(cookieHeaderOverride: header)
                    guard await dependencies.storedFactoryCookies() == storedCookies else { throw LocalStorageReadError.changed }
                    return result(usage.toUsageSnapshot(), "factory", "web")
                } catch {
                    try Task.checkCancellation()
                    guard await dependencies.storedFactoryCookies() == storedCookies else { throw LocalStorageReadError.changed }
                    guard factoryAuthFailure(error) else { throw error }; lastError = error
                }
            }
        }
        if let storedBearer = await dependencies.storedFactoryBearer(), !storedBearer.isEmpty {
            do {
                let usage = try await probe.fetch(cookieHeaderOverride: "Authorization: Bearer " + SafeBrowserSessionCredentials.token(storedBearer))
                guard await dependencies.storedFactoryBearer() == storedBearer else { throw LocalStorageReadError.changed }
                return result(usage.toUsageSnapshot(), "factory", "web")
            }
            catch { try Task.checkCancellation(); guard await dependencies.storedFactoryBearer() == storedBearer else { throw LocalStorageReadError.changed }; guard factoryAuthFailure(error) else { throw error }; lastError = error }
        }
        if let storedRefresh = await dependencies.storedFactoryRefresh(), !storedRefresh.isEmpty {
            let seed = "stored:" + SafeBrowserSessionCredential(profileID: "stored", profileURL: URL(fileURLWithPath: "/"), origin: "https://app.factory.ai", token: nil, refreshToken: storedRefresh, groupID: nil).scope
            let rotated = rotation(cache, seed: seed)
            if let rotated {
                do {
                    let usage = try await probe.fetch(cookieHeaderOverride: "Authorization: Bearer " + rotated.access)
                    guard await dependencies.storedFactoryRefresh() == storedRefresh else { throw LocalStorageReadError.changed }
                    return result(usage.toUsageSnapshot(), "factory", "web")
                } catch {
                    try Task.checkCancellation()
                    guard await dependencies.storedFactoryRefresh() == storedRefresh else { throw LocalStorageReadError.changed }
                    guard factoryAuthFailure(error) else { throw error }
                }
            }
            let token = try SafeBrowserSessionCredentials.token(rotated?.refresh ?? storedRefresh)
            do {
                let refreshed = try await factoryRefresh(refresh: token, group: nil, transport: dependencies.transport)
                guard await dependencies.storedFactoryRefresh() == storedRefresh else { throw LocalStorageReadError.changed }
                let rotation = FactoryRotation(version: 1, seed: seed, access: refreshed.access, refresh: refreshed.refresh ?? token)
                guard await cacheCommit(cache, try encode(rotation)) else { throw CancellationError() }
                let usage = try await probe.fetch(cookieHeaderOverride: "Authorization: Bearer " + refreshed.access)
                guard await dependencies.storedFactoryRefresh() == storedRefresh else { throw LocalStorageReadError.changed }
                return result(usage.toUsageSnapshot(), "factory", "web")
            } catch FactoryStatusProbeError.noSessionCookie { guard await dependencies.storedFactoryRefresh() == storedRefresh else { throw LocalStorageReadError.changed }; lastError = FactoryStatusProbeError.noSessionCookie }
        }
        let profiles = try dependencies.profiles([.chrome], context.browserDetection)
        for profile in profiles {
            for origin in SafeBrowserSessionCredentials.origins("factory") {
                let credential: SafeBrowserSessionCredential
                do { guard let value = try read("factory", profile: profile, origin: origin, dependencies: dependencies) else { continue }; credential = value }
                catch LocalStorageReadError.missing { continue }
                if let usage = try await factoryCredential(credential, cache: cache, probe: probe, dependencies: dependencies,
                    validate: { try unchanged(credential, provider: "factory", profile: profile, dependencies: dependencies) }) { return usage }
            }
        }
        do {
            let safari = try dependencies.safariFactory()
            for credential in safari {
                if let usage = try await factoryCredential(credential, cache: cache, probe: probe, dependencies: dependencies,
                    validate: { guard try dependencies.safariFactory() == safari else { throw LocalStorageReadError.changed } }) { return usage }
            }
        } catch LocalStorageReadError.missing { /* No modern Safari storage: preserve cookie alternatives below. */ }
        // Browser cookies retain the existing Safari/Chromium/Firefox alternative; no raw importer is invoked.
        for browser in [Browser.safari, .chrome, .firefox] {
            for store in dependencies.stores(browser) {
                let records = try dependencies.cookies(store, ["factory.ai"])
                let header = try factoryCookieHeader(records)
                if header.isEmpty { continue }
                do {
                    let scoped = CookieTransport(base: dependencies.transport, records: records,
                        allowedHosts: ["app.factory.ai", "auth.factory.ai", "api.factory.ai"], deriveCookieBearer: true)
                    let scopedProbe = FactoryStatusProbe(browserDetection: context.browserDetection, transport: scoped)
                    let usage = try await scopedProbe.fetch(cookieHeaderOverride: header)
                    guard try cookieFingerprint(dependencies.cookies(store, ["factory.ai"])) == cookieFingerprint(records) else { throw LocalStorageReadError.changed }
                    return result(usage.toUsageSnapshot(), "factory", "web")
                } catch { try Task.checkCancellation(); guard try cookieFingerprint(dependencies.cookies(store, ["factory.ai"])) == cookieFingerprint(records) else { throw LocalStorageReadError.changed }; guard factoryAuthFailure(error) else { throw error }; lastError = error }
            }
        }
        for browser in [Browser.safari, .chrome, .firefox] {
            for store in dependencies.stores(browser) {
                let records = try dependencies.cookies(store, ["workos.com"])
                let url = URL(string: "https://api.workos.com/user_management/authenticate")!
                let header = try cookieHeader(records, for: url); if header.isEmpty { continue }
                do {
                    let seed = SafeBrowserSessionCredential(profileID: store.profile.id, profileURL: store.databaseURL ?? URL(fileURLWithPath: store.profile.id),
                        origin: "https://api.workos.com", token: header, refreshToken: nil, groupID: nil).scope
                    let rotated = rotation(cache, seed: seed)
                    if let rotated {
                        do {
                            let usage = try await probe.fetch(cookieHeaderOverride: "Authorization: Bearer " + rotated.access)
                            guard try cookieFingerprint(dependencies.cookies(store, ["workos.com"])) == cookieFingerprint(records) else { throw LocalStorageReadError.changed }
                            return result(usage.toUsageSnapshot(), "factory", "web")
                        } catch {
                            try Task.checkCancellation()
                            guard try cookieFingerprint(dependencies.cookies(store, ["workos.com"])) == cookieFingerprint(records) else { throw LocalStorageReadError.changed }
                            guard factoryAuthFailure(error) else { throw error }
                        }
                    }
                    let refreshed = try await factoryRefresh(refresh: rotated?.refresh, group: nil, cookies: header, transport: dependencies.transport)
                    guard try cookieFingerprint(dependencies.cookies(store, ["workos.com"])) == cookieFingerprint(records) else { throw LocalStorageReadError.changed }
                    if let refresh = refreshed.refresh ?? rotated?.refresh {
                        let next = FactoryRotation(version: 1, seed: seed, access: refreshed.access, refresh: refresh)
                        guard await cacheCommit(cache, try encode(next)) else { throw CancellationError() }
                    }
                    let usage = try await probe.fetch(cookieHeaderOverride: "Authorization: Bearer " + refreshed.access)
                    guard try cookieFingerprint(dependencies.cookies(store, ["workos.com"])) == cookieFingerprint(records) else { throw LocalStorageReadError.changed }
                    return result(usage.toUsageSnapshot(), "factory", "web")
                } catch FactoryStatusProbeError.noSessionCookie { guard try cookieFingerprint(dependencies.cookies(store, ["workos.com"])) == cookieFingerprint(records) else { throw LocalStorageReadError.changed }; lastError = FactoryStatusProbeError.noSessionCookie }
            }
        }
        throw lastError
    }
    private static func factoryCookieHeader(_ records: [BrowserCookieRecord]) throws -> String {
        for host in ["app.factory.ai", "auth.factory.ai", "api.factory.ai"] {
            let header = try cookieHeader(records, for: URL(string: "https://" + host + "/api/app/auth/me")!)
            if !header.isEmpty { return header }
        }
        return ""
    }
    private static func factoryAuthFailure(_ error: Error) -> Bool {
        guard let error = error as? FactoryStatusProbeError else { return false }
        switch error {
        case .notLoggedIn, .unauthorizedAPIKey, .noSessionCookie: return true
        case .networkError(let message): return message.hasPrefix("HTTP 403 Forbidden")
        default: return false
        }
    }
    private static func encode(_ rotation: FactoryRotation) throws -> String {
        let encoded = "CodeRimFactory1:" + (try JSONEncoder().encode(rotation)).base64EncodedString()
        guard encoded.utf8.count <= 65536 else { throw LocalStorageReadError.limit }; return encoded
    }
    private static func rotation(_ cache: String?, seed: String) -> FactoryRotation? {
        guard let cache, cache.utf8.count <= 65536, cache.hasPrefix("CodeRimFactory1:"),
              let data = Data(base64Encoded: String(cache.dropFirst("CodeRimFactory1:".count))),
              let text = String(data: data, encoding: .utf8), (try? StorageJSON.parse(text)) != nil,
              let value = try? JSONDecoder().decode(FactoryRotation.self, from: data),
              value.version == 1, value.seed == seed,
              (try? SafeBrowserSessionCredentials.token(value.access)) != nil,
              (try? SafeBrowserSessionCredentials.token(value.refresh)) != nil else { return nil }
        return value
    }
    private static func factoryCredential(_ credential: SafeBrowserSessionCredential, cache: String?, probe: FactoryStatusProbe,
        dependencies: Dependencies, validate: () throws -> Void) async throws -> ProviderFetchResult? {
        let rotated = rotation(cache, seed: credential.scope)
        let access = rotated?.access ?? credential.token, refresh = rotated?.refresh ?? credential.refreshToken
        if let access {
            do {
                let usage = try await probe.fetch(cookieHeaderOverride: "Authorization: Bearer " + access)
                try validate(); return result(usage.toUsageSnapshot(), "factory", "web")
            } catch { try Task.checkCancellation(); try validate(); guard factoryAuthFailure(error) else { throw error }; if refresh == nil { return nil } }
        }
        guard let refresh else { return nil }
        let refreshed = try await factoryRefresh(refresh: refresh, group: credential.groupID, transport: dependencies.transport)
        try validate()
        if let access = credential.token, let before = try SafeBrowserSessionCredentials.jwtClaims(access),
           let after = try SafeBrowserSessionCredentials.jwtClaims(refreshed.access),
           let subject = before["sub"], let nextSubject = after["sub"], subject != nextSubject { throw LocalStorageReadError.changed }
        let next = FactoryRotation(version: 1, seed: credential.scope, access: refreshed.access, refresh: refreshed.refresh ?? refresh)
        guard await cacheCommit(cache, try encode(next)) else { throw CancellationError() }
        let usage = try await probe.fetch(cookieHeaderOverride: "Authorization: Bearer " + refreshed.access)
        try validate(); return result(usage.toUsageSnapshot(), "factory", "web")
    }
    private static func cookieFingerprint(_ records: [BrowserCookieRecord]) -> String {
        records.map { [$0.domain, $0.path, $0.name, $0.value, String(describing: $0.scope), String($0.isSecure), String($0.expires?.timeIntervalSince1970 ?? -1)].joined(separator: "\0") }.sorted().joined(separator: "\n")
    }
    static func factoryRefresh(refresh: String?, group: String?, cookies: String? = nil, transport: any ProviderHTTPTransport) async throws -> (access: String, refresh: String?) {
        let refresh = try refresh.map(SafeBrowserSessionCredentials.token)
        guard refresh != nil || cookies?.isEmpty == false else { throw FactoryStatusProbeError.noSessionCookie }
        for client in ["client_01HXRMBQ9BJ3E7QSTQ9X2PHVB7", "client_01HNM792M5G5G1A2THWPXKFMXB"] {
            try Task.checkCancellation()
            var request = URLRequest(url: URL(string: "https://api.workos.com/user_management/authenticate")!)
            request.httpMethod = "POST"; request.timeoutInterval = 15
            request.setValue("application/json", forHTTPHeaderField: "Content-Type"); request.setValue("application/json", forHTTPHeaderField: "Accept")
            var body: [String: Any] = ["client_id": client, "grant_type": "refresh_token"]
            if let refresh { body["refresh_token"] = refresh }
            else { body["useCookie"] = true; request.setValue(cookies, forHTTPHeaderField: "Cookie") }
            if let group { body["organization_id"] = try SafeBrowserSessionCredentials.identifier(group) }
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await transport.data(for: request); try Task.checkCancellation()
            guard data.count <= 65536, let response = response as? HTTPURLResponse, response.url == request.url else { throw LocalStorageReadError.invalid }
            if [400, 401, 403].contains(response.statusCode) { continue }
            guard response.statusCode == 200 else { throw ProviderFetchClassifiedError(kind: .networkFailure, message: "Factory refresh could not complete.") }
            guard let text = String(data: data, encoding: .utf8), case let .object(fields) = try StorageJSON.parse(text),
                  case let .string(access)? = fields["access_token"] else { throw LocalStorageReadError.invalid }
            let next: String?
            if let value = fields["refresh_token"], value != .null {
                guard case let .string(text) = value else { throw LocalStorageReadError.invalid }; next = try SafeBrowserSessionCredentials.token(text)
            } else { next = nil }
            if let group, let value = fields["organization_id"], value != .null,
               try SafeBrowserSessionCredentials.group(value) != group { throw LocalStorageReadError.changed }
            let validAccess = try SafeBrowserSessionCredentials.token(access)
            if let group, let claims = try SafeBrowserSessionCredentials.jwtClaims(validAccess), let value = claims["org_id"],
               try SafeBrowserSessionCredentials.group(value) != group { throw LocalStorageReadError.changed }
            return (validAccess, next)
        }
        throw FactoryStatusProbeError.noSessionCookie
    }
    static func cookieHeader(_ records: [BrowserCookieRecord], for url: URL, now: Date = Date()) throws -> String {
        guard let host = url.host, url.scheme == "https" else { throw LocalStorageReadError.invalid }
        var values: [String: String] = [:]
        let requestPath = url.path.isEmpty ? "/" : url.path
        for record in records {
            let domain = record.domain.hasPrefix(".") ? String(record.domain.dropFirst()) : record.domain
            guard domain == host || (record.scope == .domain && host.hasSuffix("." + domain)),
                  record.expires == nil || record.expires! > now,
                  !record.path.isEmpty, requestPath == record.path || (requestPath.hasPrefix(record.path) && (record.path.hasSuffix("/") || requestPath.dropFirst(record.path.count).first == "/")) else { continue }
            guard !record.name.isEmpty, !record.name.contains("="), !record.name.contains(";"),
                  record.name.utf8.allSatisfy({ (33...126).contains($0) }),
                  record.value.utf8.allSatisfy({ (32...126).contains($0) && $0 != 59 }),
                  values[record.name] == nil || values[record.name] == record.value else { throw LocalStorageReadError.invalid }
            values[record.name] = record.value
        }
        let header = values.keys.sorted().map { $0 + "=" + values[$0]! }.joined(separator: "; ")
        guard header.utf8.count <= 65536 else { throw LocalStorageReadError.limit }; return header
    }
    private static func cookieValues(_ header: String) throws -> [String: String] {
        var values: [String: String] = [:]
        for item in header.split(separator: ";") {
            let pair = item.trimmingCharacters(in: .whitespaces).split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard pair.count == 2, values[String(pair[0])] == nil else { throw LocalStorageReadError.invalid }; values[String(pair[0])] = String(pair[1])
        }; return values
    }
    struct CookieTransport: ProviderHTTPTransport {
        let base: any ProviderHTTPTransport
        let records: [BrowserCookieRecord]
        let allowedHosts: Set<String>
        var requiredHertz: String? = nil
        var requiredGroup: String? = nil
        var expectedGroup: String? = nil
        var deriveCookieBearer = false
        func data(for request: URLRequest) async throws -> (Data, URLResponse) {
            guard let url = request.url, url.scheme == "https", url.port == nil || url.port == 443,
                  url.user == nil, url.password == nil, let host = url.host, allowedHosts.contains(host) else { throw LocalStorageReadError.unsupported }
            let header = try cookieHeader(records, for: url)
            if let requiredHertz {
                let values = try cookieValues(header)
                guard values["HERTZ-SESSION"] == requiredHertz else { throw LocalStorageReadError.changed }
                let group = try ["minimax_group_id_v2", "group_id", "groupid"].compactMap { values[$0] }
                    .reduce(nil as String?) { try SafeBrowserSessionCredentials.merge($0, SafeBrowserSessionCredentials.identifier($1)) }
                if let requiredGroup, group != requiredGroup { throw LocalStorageReadError.changed }
                if let expectedGroup, let group, group != expectedGroup { throw LocalStorageReadError.changed }
            }
            var scoped = request; scoped.setValue(header, forHTTPHeaderField: "Cookie")
            if deriveCookieBearer {
                let token = try cookieValues(header)["access-token"]
                scoped.setValue(token.map { "Bearer " + $0 }, forHTTPHeaderField: "Authorization")
            }
            return try await base.data(for: scoped)
        }
    }
    private struct StorageHTTPTransport: ProviderHTTPTransport {
        func data(for request: URLRequest) async throws -> (Data, URLResponse) {
            let config = URLSessionConfiguration.ephemeral
            config.httpCookieStorage = nil; config.httpShouldSetCookies = false; config.urlCache = nil
            config.timeoutIntervalForRequest = 15; config.timeoutIntervalForResource = 20
            let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
            try Task.checkCancellation()
            let result = try await BoundedHTTP.data(for: request, on: session, maximumBytes: 4 * 1024 * 1024, rejectRedirects: true)
            try Task.checkCancellation(); return result
        }
    }
}
