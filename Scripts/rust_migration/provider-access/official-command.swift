import Foundation
import CodexBarCore

@main enum Fixture {
    static func main() async throws {
        let env = OfficialUsageCLI.environment()
        precondition(Set(env.keys).isSubset(of: ["PATH", "HOME", "LANG", "LC_ALL", "LC_CTYPE", "TZ"]))
        func context(_ env: [String: String]) -> ProviderFetchContext {
            let browser = BrowserDetection()
            return ProviderFetchContext(runtime: .app, sourceMode: .cli, includeCredits: true, includeOptionalUsage: false,
                webTimeout: 1, webDebugDumpHTML: false, verbose: false, env: env, settings: nil,
                fetcher: UsageFetcher(environment: env), claudeFetcher: ClaudeUsageFetcher(browserDetection: browser, environment: env),
                browserDetection: browser)
        }
        for (id, strategy): (UsageProvider, String) in [(.augment, "augment.cli"), (.alibabatokenplan, "alibaba-token-plan.cli")] {
            let result = try await OfficialUsageCLI.fetch(ProviderDescriptorRegistry.descriptor(for: id), strategy: strategy, context: context(env))
            precondition(result.strategyID == strategy && result.strategyKind == .cli)
            precondition(result.usage.primary?.usedPercent == 25)
            if id == .alibabatokenplan { precondition(result.usage.secondary?.usedPercent == 75) }
            print("\(strategy) arguments, scoped child environment, parsed quota passed")
        }
        let descriptor = ProviderDescriptorRegistry.descriptor(for: .augment)
        for mode in ["denied", "empty", "malformed", "failure", "oversized", "missing"] {
            var runEnv = env
            let marker = URL(fileURLWithPath: env["HOME"]!).appendingPathComponent(mode)
            try Data().write(to: marker)
            defer { try? FileManager.default.removeItem(at: marker) }
            if mode == "missing" { runEnv["PATH"] = env["HOME"]! }
            do {
                _ = try await OfficialUsageCLI.fetch(descriptor, strategy: "augment.cli", context: context(runEnv))
                preconditionFailure("Expected a failed synthetic command: " + mode)
            } catch {
                precondition(!error.localizedDescription.contains("synthetic-private-diagnostic"))
                switch (mode, error) {
                case ("denied", AuggieCLIError.notAuthenticated), ("empty", AuggieCLIError.noOutput),
                     ("malformed", AuggieCLIError.parseError), ("failure", AuggieCLIError.noOutput),
                     ("oversized", SubprocessRunnerError.outputTooLarge), ("missing", SubprocessRunnerError.binaryNotFound): break
                default: throw error
                }
            }
        }
        print("Six CLI error outcomes and bounded private diagnostics passed")
    }
}
