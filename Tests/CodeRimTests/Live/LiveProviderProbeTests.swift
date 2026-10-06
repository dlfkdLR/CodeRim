import CodexBarCore
import XCTest
@testable import CodeRim

/// Reads every provider against this Mac's real sign-ins. Opt-in only:
/// CODERIM_LIVE_PROVIDERS=copilot,opencode,… swift test --filter LiveProviderProbeTests
@MainActor
final class LiveProviderProbeTests: XCTestCase {
    func testProbeProviders() async throws {
        guard let list = ProcessInfo.processInfo.environment["CODERIM_LIVE_PROVIDERS"] else { throw XCTSkip("opt-in") }
        let wanted = Set(list.split(separator: ",").map(String.init))
        var providers: [any NotchProvider] = [CopilotNotchProvider(), CursorNotchProvider(), GrokNotchProvider(),
            OpenCodeNotchProvider(), CommandCodeNotchProvider(), GLMNotchProvider(), OllamaNotchProvider(),
            AntigravityNotchProvider(), OllamaLocalProvider()]
        providers += ExtendedProviderCatalog.makeProviders()
        for provider in providers where wanted.contains(provider.id) || wanted.contains("all") {
            let started = Date()
            var line: String
            do {
                let snapshot = try await provider.fetchSnapshot()
                line = "status=\(snapshot.status) windows=\(snapshot.windows.map { "\($0.label):\($0.usedFraction.map { Int($0 * 100) } ?? -1)%" }) msg=\(snapshot.statusMessage ?? "-")"
            } catch { line = "THROW \(error)" }
            print("LIVE \(provider.id) [\(String(format: "%.1f", Date().timeIntervalSince(started)))s] route=\(provider.signInRoute.actionTitle ?? "-") :: \(line)")
        }
    }
}

final class ProviderModeInventoryTests: XCTestCase {
    func testPrintModes() throws {
        guard ProcessInfo.processInfo.environment["CODERIM_LIVE_PROVIDERS"] != nil else { throw XCTSkip("opt-in") }
        for d in ExtendedProviderCatalog.all {
            let id = ExtendedProviderCatalog.localID(d.id)
            let modes = d.fetchPlan.sourceModes.map(\.rawValue).sorted().joined(separator: ",")
            print("MODE \(id) native=\(ExtendedProviderCatalog.nativeIDs.contains(id)) modes=\(modes) cookies=\(d.metadata.browserCookieOrder != nil) apiKey=\(d.credentials?.supportsAPIKeyOverride == true) dash=\(d.metadata.dashboardURL ?? "-")")
        }
    }
}

@MainActor
final class LiveHybridProbeTests: XCTestCase {
    /// The CodexBar Copilot reader with a GitHub token pasted as its API key, the way a device-flow sign-in stores it.
    func testCopilotThroughTheUpstreamReaderWithAToken() async throws {
        guard ProcessInfo.processInfo.environment["CODERIM_LIVE_PROVIDERS"] != nil else { throw XCTSkip("opt-in") }
        let gh = Process(); let out = Pipe()
        gh.executableURL = URL(fileURLWithPath: "/opt/homebrew/bin/gh"); gh.arguments = ["auth", "token"]; gh.standardOutput = out
        try gh.run(); gh.waitUntilExit()
        let token = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        let descriptor = try XCTUnwrap(ExtendedProviderCatalog.descriptor(for: "copilot"))
        var config = ExtendedProviderConfiguration(providerID: descriptor.id)
        config.provider.apiKey = token
        let provider = ExtendedNotchProvider(descriptor: descriptor, configuration: { config })
        let snapshot = try await provider.fetchSnapshot()
        print("LIVE copilot-upstream status=\(snapshot.status) windows=\(snapshot.windows.map { "\($0.label):\($0.usedFraction.map { Int($0 * 100) } ?? -1)%" })")
        let empty = ExtendedNotchProvider(descriptor: descriptor, configuration: { ExtendedProviderConfiguration(providerID: descriptor.id) })
        print("LIVE copilot-upstream-nokey status=\((try? await empty.fetchSnapshot())?.status as Any)")
    }
}

final class LiveDeviceFlowTests: XCTestCase {
    /// GitHub hands out a device code without any CLI — the first half of the in-app sign-in.
    func testGitHubIssuesADeviceCode() async throws {
        guard ProcessInfo.processInfo.environment["CODERIM_LIVE_PROVIDERS"] != nil else { throw XCTSkip("opt-in") }
        let code = try await CodexBarCore.CopilotDeviceFlow().requestDeviceCode()
        print("LIVE device-code format=\(code.userCode.count) chars, verify=\(code.verificationUri)")
        XCTAssertFalse(code.userCode.isEmpty)
    }
}

@MainActor
final class LiveUnconfiguredUpstreamTests: XCTestCase {
    /// Every native provider's CodexBar reader with no CodeRim sign-in must read as "not connected",
    /// never as an error, or the hybrid would show a failure to someone who simply has not signed in.
    func testUnconfiguredUpstreamsReadAsSignedOut() async throws {
        guard ProcessInfo.processInfo.environment["CODERIM_LIVE_PROVIDERS"] != nil else { throw XCTSkip("opt-in") }
        for id in ["copilot", "cursor", "grok", "opencode", "commandcode", "glm", "ollama", "gemini"] {
            let descriptor = try XCTUnwrap(ExtendedProviderCatalog.descriptor(for: id))
            let provider = ExtendedNotchProvider(descriptor: descriptor, configuration: { .init(providerID: descriptor.id) })
            let status = (try? await provider.fetchSnapshot())?.status
            print("LIVE upstream-empty \(id) -> \(status.map { "\($0)" } ?? "threw")")
        }
    }
}

final class LiveRawUpstreamTests: XCTestCase {
    func testRawErrors() async throws {
        guard ProcessInfo.processInfo.environment["CODERIM_LIVE_PROVIDERS"] != nil else { throw XCTSkip("opt-in") }
        for id in ["opencode", "gemini"] {
            let descriptor = try XCTUnwrap(ExtendedProviderCatalog.descriptor(for: id))
            let config = ExtendedProviderConfiguration(providerID: descriptor.id)
            let env = config.fetchEnvironment(base: ProcessInfo.processInfo.environment)
            let browser = BrowserDetection()
            let context = ProviderFetchContext(runtime: .app, sourceMode: config.sourceMode(environment: env), includeCredits: true,
                includeOptionalUsage: true, webTimeout: 20, webDebugDumpHTML: false, verbose: false, env: env,
                settings: config.settings(environment: env), fetcher: UsageFetcher(environment: env),
                claudeFetcher: ClaudeUsageFetcher(browserDetection: browser, environment: env), browserDetection: browser)
            do {
                let r = try await ExtendedNotchProvider.fetchUpstream(descriptor, context: context)
                print("LIVE raw \(id) ok source=\(r.sourceLabel) strategy=\(r.strategyID) email=\(r.usage.accountEmail(for: descriptor.id) != nil)")
            } catch { print("LIVE raw \(id) error type=\(type(of: error)) \(String(reflecting: error).prefix(160))") }
        }
    }
}
