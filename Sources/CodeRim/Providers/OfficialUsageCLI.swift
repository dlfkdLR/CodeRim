#if CODERIM_RUST_ACCOUNTING
import CodexBarCore
import Foundation

enum OfficialUsageCLI {
    /// These CLIs manage their own sign-in. They need the user's home and locale,
    /// not the environment of every other provider running in the desktop process.
    static func environment() -> [String: String] {
        let names = ["PATH", "HOME", "LANG", "LC_ALL", "LC_CTYPE", "TZ"]
        var result: [String: String] = [:]
        for name in names {
            if let value = getenv(name), value.pointee != 0 { result[name] = String(cString: value) }
        }
        let home = result["HOME"] ?? FileManager.default.homeDirectoryForCurrentUser.path
        result["HOME"] = home
        var paths = (result["PATH"] ?? "").split(separator: ":").map(String.init)
        paths += LoginShellPathCache.shared.current ?? []
        paths += [home + "/.local/bin", home + "/.npm-global/bin", "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"]
        var seen: Set<String> = []
        result["PATH"] = paths.filter { $0.hasPrefix("/") && seen.insert($0).inserted }.joined(separator: ":")
        return result
    }

    static func fetch(_ descriptor: ProviderDescriptor, strategy: String, context: ProviderFetchContext) async throws -> ProviderFetchResult {
        if descriptor.id == .augment { return try await auggie(environment: context.env) }
        guard descriptor.id == .alibabatokenplan, let strategy = await descriptor.fetchPlan.pipeline.resolveStrategies(context)
            .first(where: { $0.id == strategy && $0.kind == .cli }) else { throw AuggieCLIError.noOutput }
        return try await strategy.fetch(context)
    }

    private static func auggie(environment: [String: String]) async throws -> ProviderFetchResult {
        let binary = (environment["PATH"] ?? "").split(separator: ":").lazy
            .map { URL(fileURLWithPath: String($0), isDirectory: true).appendingPathComponent("auggie").path }
            .first { FileManager.default.isExecutableFile(atPath: $0) }
        guard let binary else { throw SubprocessRunnerError.binaryNotFound("auggie") }
        let result: SubprocessResult
        do {
            result = try await SubprocessRunner.run(binary: binary, arguments: ["account", "status"], environment: environment,
                timeout: 15, maxOutputBytes: 65_536, label: "auggie-account-status")
        } catch SubprocessRunnerError.nonZeroExit {
            // Command stderr can contain private account diagnostics. Never display it in the notch.
            throw AuggieCLIError.noOutput
        }
        guard !result.stdout.isEmpty else { throw AuggieCLIError.noOutput }
        if result.stdout.contains("Authentication failed") || result.stdout.contains("auggie login") {
            throw AuggieCLIError.notAuthenticated
        }
        let usage = try parseAuggie(result.stdout).toUsageSnapshot()
        return ProviderFetchResult(usage: usage, credits: nil, dashboard: nil,
            sourceLabel: "cli", strategyID: "augment.cli", strategyKind: .cli)
    }

    // CodexBar 51ed16b, AuggieCLIProbe.parse. The public fetch method forwards the ambient
    // environment. Keep its private quota-format parser, omitting its raw-output diagnostic.
    // Copyright (c) 2026 Peter Steinberger. MIT license in Resources/ThirdPartyLicenses/CodexBar.txt.
    static func parseAuggie(_ output: String) throws -> AugmentStatusSnapshot {
        // Legacy output:
        // Max Plan 450,000 credits / month
        // 11,657 remaining · 953,170 / 964,827 credits used
        // 2 days remaining in this billing cycle (ends 1/8/2026)
        //
        // Current output (2026+):
        // 319,054 credits remaining                     Max Plan
        // 450,000 credits / month
        // 9 days remaining in this billing cycle (ends 6/9/2026)

        var maxCredits: Int?
        var remaining: Int?
        var used: Int?
        var total: Int?
        var billingCycleEnd: Date?

        for line in output.split(separator: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)

            if trimmed.contains("credits / month") {
                if let match = trimmed.range(of: #"([\d,]+)\s+credits\s*/\s*month"#, options: .regularExpression) {
                    let numberStr = String(trimmed[match]).replacingOccurrences(of: ",", with: "")
                        .replacingOccurrences(of: " credits", with: "")
                        .replacingOccurrences(of: " / month", with: "")
                    maxCredits = Int(numberStr)
                    total = total ?? Int(numberStr)
                }
            } else if trimmed.contains("Max Plan"), trimmed.contains("credits"), !trimmed.contains("remaining") {
                if let match = trimmed.range(of: #"([\d,]+)\s+credits"#, options: .regularExpression) {
                    let numberStr = String(trimmed[match]).replacingOccurrences(of: ",", with: "")
                        .replacingOccurrences(of: " credits", with: "")
                    maxCredits = Int(numberStr)
                }
            }

            if trimmed.contains("credits remaining"), !trimmed.contains("billing cycle") {
                if let match = trimmed.range(of: #"([\d,]+)\s+credits\s+remaining"#, options: .regularExpression) {
                    let numberStr = String(trimmed[match]).replacingOccurrences(of: ",", with: "")
                        .replacingOccurrences(of: " credits", with: "")
                        .replacingOccurrences(of: " remaining", with: "")
                    remaining = Int(numberStr)
                }
            }

            // Parse "11,657 remaining · 953,170 / 964,827 credits used"
            if trimmed.contains("remaining"), trimmed.contains("credits used") {
                if let remMatch = trimmed.range(of: #"([\d,]+)\s+remaining"#, options: .regularExpression) {
                    let numStr = String(trimmed[remMatch])
                        .replacingOccurrences(of: ",", with: "")
                        .replacingOccurrences(of: " remaining", with: "")
                    remaining = Int(numStr)
                }

                if let usedMatch = trimmed.range(
                    of: #"([\d,]+)\s*/\s*([\d,]+)\s+credits used"#,
                    options: .regularExpression)
                {
                    let parts = String(trimmed[usedMatch])
                        .replacingOccurrences(of: " credits used", with: "")
                        .split(separator: "/")
                    if parts.count == 2 {
                        used = Int(parts[0].replacingOccurrences(of: ",", with: "")
                            .trimmingCharacters(in: .whitespaces))
                        total = Int(parts[1].replacingOccurrences(of: ",", with: "")
                            .trimmingCharacters(in: .whitespaces))
                    }
                }
            }

            if trimmed.contains("billing cycle"), trimmed.contains("ends") {
                if let dateMatch = trimmed.range(of: #"ends\s+([\d/]+)"#, options: .regularExpression) {
                    let dateStr = String(trimmed[dateMatch])
                        .replacingOccurrences(of: "ends", with: "")
                        .trimmingCharacters(in: .whitespaces)

                    let formatter = DateFormatter()
                    formatter.dateFormat = "M/d/yyyy"
                    formatter.locale = Locale(identifier: "en_US_POSIX")
                    formatter.timeZone = TimeZone.current
                    billingCycleEnd = formatter.date(from: dateStr)
                }
            }
        }

        guard let finalRemaining = remaining else {
            throw AuggieCLIError.parseError("Could not extract credits from output")
        }

        let finalTotal = total ?? maxCredits
        guard let finalTotal else {
            throw AuggieCLIError.parseError("Could not extract credits from output")
        }

        let finalUsed = used ?? max(0, finalTotal - finalRemaining)

        return AugmentStatusSnapshot(
            creditsRemaining: Double(finalRemaining),
            creditsUsed: Double(finalUsed),
            creditsLimit: Double(finalTotal),
            billingCycleEnd: billingCycleEnd,
            accountEmail: nil,
            accountPlan: maxCredits.map { "\($0.formatted()) credits/month" },
            rawJSON: nil)
    }
}
#endif
