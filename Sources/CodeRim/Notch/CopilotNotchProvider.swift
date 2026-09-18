import Foundation

/// Reads GitHub Copilot quotas from the endpoint its editors use. The token is
/// borrowed from an existing `GH_TOKEN` / `GITHUB_TOKEN`, from GitHub CLI's
/// `hosts.yml`, or from `gh auth token` — never stored by CodeRim. The ring
/// only appears once one of those turns up a token, so a machine without
/// GitHub CLI never sees a Copilot cell.
///
/// Ported from the MIT-licensed Codenotch (`GitHubCopilotProvider`).
@MainActor
final class CopilotNotchProvider: NotchProvider {
    let id = "copilot"
    let displayName = "GitHub Copilot"
    let glyph: ProviderGlyph = .copilot
    var isVisibleWhenAbsent: Bool { false }

    private let endpoint = URL(string: "https://api.github.com/copilot_internal/user")!
    private let session: URLSession
    private let loadCredentials: @Sendable () throws -> GitHubCopilotCredentials

    init(session: URLSession = ProviderSession.shared,
         loadCredentials: (@Sendable () throws -> GitHubCopilotCredentials)? = nil) {
        self.session = session
        self.loadCredentials = loadCredentials ?? { try GitHubCopilotCredentials.load() }
    }

    var signInRoute: SignInRoute {
        .guidance("Sign in with GitHub CLI using `gh auth login`, then enable GitHub Copilot.")
    }

    func account() -> ProviderAccount? {
        GitHubCopilotCredentials.account()
    }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        // Credential discovery reads files and can start `gh`; keep that off
        // the main actor.
        let load = loadCredentials
        let credentials = try await Task.detached { try load() }.value

        var request = URLRequest(url: endpoint)
        request.setValue("Bearer \(credentials.token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        request.setValue("CodeRim", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 15

        let (data, response) = try await BoundedHTTP.data(for: request, on: session)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 401 || status == 403 { throw NotchProviderError.needsAuth }
        if status == 429 {
            let retry = (response as? HTTPURLResponse)?
                .value(forHTTPHeaderField: "Retry-After").flatMap(TimeInterval.init) ?? 60
            throw NotchProviderError.rateLimited(retryAfter: retry)
        }
        guard (200..<300).contains(status) else {
            throw NotchProviderError.badResponse(status: status)
        }

        let windows = try GitHubCopilotUsage.windows(from: data)
        return ProviderSnapshot(
            id: id,
            displayName: displayName,
            glyph: glyph,
            fidelity: .official,
            status: .ok,
            windows: windows,
            headlineID: windows.contains { $0.id == "premium_interactions" }
                ? "premium_interactions" : windows.first?.id
        )
    }
}

struct GitHubCopilotCredentials: Sendable {
    let token: String
    let username: String?
    let source: String

    static var hostsURL: URL {
        URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".config/gh/hosts.yml")
    }

    static func load() throws -> GitHubCopilotCredentials {
        let environment = ProcessInfo.processInfo.environment
        let hosts = CredentialFileReader.text(at: hostsURL)
        return try load(environment: environment, hosts: hosts, command: ghToken)
    }

    /// Injectable inputs keep credential discovery testable without touching a
    /// real token or starting GitHub CLI.
    static func load(environment: [String: String],
                     hosts: String?,
                     command: () -> String?) throws -> GitHubCopilotCredentials {
        let parsed = parseHosts(hosts)
        if let token = nonEmpty(environment["GH_TOKEN"] ?? environment["GITHUB_TOKEN"]) {
            return GitHubCopilotCredentials(token: token, username: parsed.username,
                                            source: "GitHub")
        }
        if let token = parsed.token {
            return GitHubCopilotCredentials(token: token, username: parsed.username,
                                            source: "GitHub CLI")
        }
        if let token = nonEmpty(command()) {
            return GitHubCopilotCredentials(token: token, username: parsed.username,
                                            source: "GitHub CLI")
        }
        throw NotchProviderError.needsAuth
    }

    static func account() -> ProviderAccount? {
        guard let hosts = CredentialFileReader.text(at: hostsURL),
              let username = parseHosts(hosts).username
        else { return nil }
        return ProviderAccount(
            label: username,
            plan: nil,
            source: "GitHub",
            manageURL: URL(string: "https://github.com/settings/copilot")
        )
    }

    private static func ghToken() -> String? {
        let candidates = [
            "/opt/homebrew/bin/gh",
            "/usr/local/bin/gh",
            "/usr/bin/gh"
        ]
        guard let executable = candidates.first(where: {
            FileManager.default.isExecutableFile(atPath: $0)
        }) else { return nil }

        // Bounded: `gh` can block on a locked Keychain, and this runs on every
        // poll. A token is a few dozen bytes, so the ceiling only trips on a
        // `gh` that has gone wrong.
        let inherited = ProcessInfo.processInfo.environment
        let allowed = ["HOME", "TMPDIR", "USER", "LOGNAME", "LANG", "LC_ALL", "LC_CTYPE",
                       "__CF_USER_TEXT_ENCODING", "XDG_CONFIG_HOME", "GH_CONFIG_DIR",
                       "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
                       "http_proxy", "https_proxy", "no_proxy"]
        var environment = allowed.reduce(into: [String: String]()) { $0[$1] = inherited[$1] }
        environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
        guard let result = try? BoundedProcess.runSynchronously(
            executable: URL(fileURLWithPath: executable),
            arguments: ["auth", "token", "--hostname", "github.com"],
            environment: environment, timeout: .seconds(10), maximumOutputBytes: 65_536
        ), result.status == 0 else { return nil }
        return String(data: result.output, encoding: .utf8).flatMap(nonEmpty)
    }

    private static func parseHosts(_ text: String?) -> (username: String?, token: String?) {
        guard let text else { return (nil, nil) }
        let lines = text.components(separatedBy: .newlines)
        guard let start = lines.firstIndex(where: {
            $0.trimmingCharacters(in: .whitespaces) == "github.com:"
        }) else { return (nil, nil) }

        var username: String?
        var token: String?
        for line in lines.dropFirst(start + 1) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if !line.hasPrefix(" ") && !line.hasPrefix("\t") { break }
            if let value = yamlValue(trimmed, key: "user") { username = value }
            if let value = yamlValue(trimmed, key: "oauth_token") { token = value }
        }
        return (username, token)
    }

    private static func yamlValue(_ line: String, key: String) -> String? {
        let prefix = "\(key):"
        guard line.hasPrefix(prefix) else { return nil }
        let value = String(line.dropFirst(prefix.count))
            .trimmingCharacters(in: .whitespaces)
        return nonEmpty(value)?.trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
    }

    private static func nonEmpty(_ value: String?) -> String? {
        guard let value else { return nil }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}

enum GitHubCopilotUsage {
    private static let order = ["premium_interactions", "chat", "completions"]

    static func windows(from data: Data) throws -> [LimitWindow] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let quotas = root["quota_snapshots"] as? [String: Any]
        else { throw NotchProviderError.badResponse(status: 0) }

        let keys = order + quotas.keys.filter { !order.contains($0) }.sorted()
        let windows = keys.compactMap { key -> LimitWindow? in
            guard let quota = quotas[key] as? [String: Any] else { return nil }
            return window(id: key, quota: quota, root: root)
        }
        guard !windows.isEmpty else {
            throw NotchProviderError.nothingMetered("GitHub Copilot reported no metered quotas")
        }
        return windows
    }

    private static func window(id: String, quota: [String: Any], root: [String: Any]) -> LimitWindow? {
        if (quota["unlimited"] as? Bool) == true { return nil }

        let entitlement = number(quota["entitlement"])
        let remaining = number(quota["remaining"])
        let used = number(quota["used"])
        // Per-quota reset first (older shape); then the root fields. As of
        // late 2026 the endpoint sends `quota_reset_at: 0` per quota (a
        // placeholder) and states the real date once at the root — as a full
        // ISO `quota_reset_date_utc` and a bare `yyyy-MM-dd` `quota_reset_date`.
        let reset = date(quota["reset_date"] ?? quota["reset_at"] ?? quota["resets_at"])
            ?? date(root["quota_reset_date_utc"])
            ?? date(root["quota_reset_date"])

        if entitlement == 0 { return nil }
        if let entitlement, entitlement > 0 {
            let consumed = used ?? max(0, entitlement - (remaining ?? entitlement))
            return LimitWindow(id: id, label: label(for: id),
                               usedFraction: max(0, consumed / entitlement), resetsAt: reset,
                               duration: monthlyDuration(endingAt: reset))
        }
        if let remaining, remaining >= 0, used == nil {
            return remaining == 0 && entitlement == 0 ? nil
                : LimitWindow(id: id, label: label(for: id),
                              remaining: Int(remaining.rounded()), resetsAt: reset)
        }
        if let used, used >= 0 {
            return LimitWindow(id: id, label: label(for: id),
                               used: Int(used.rounded()), resetsAt: reset)
        }
        return nil
    }

    /// Copilot allowances reset at midnight UTC on the first of each month.
    private static func monthlyDuration(endingAt reset: Date?) -> TimeInterval? {
        guard let reset else { return nil }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let parts = calendar.dateComponents([.day, .hour, .minute, .second], from: reset)
        guard parts.day == 1, parts.hour == 0, parts.minute == 0, parts.second == 0,
              let previousMonth = calendar.date(byAdding: .second, value: -1, to: reset)
        else { return nil }
        return calendar.dateInterval(of: .month, for: previousMonth)?.duration
    }

    private static func number(_ value: Any?) -> Double? {
        (value as? NSNumber)?.doubleValue
    }

    private static func date(_ value: Any?) -> Date? {
        if let seconds = number(value) {
            return Date(timeIntervalSince1970: seconds > 10_000_000_000 ? seconds / 1000 : seconds)
        }
        guard let text = value as? String else { return nil }
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        if let date = fractional.date(from: text) ?? plain.date(from: text) { return date }

        // `quota_reset_date` is a bare calendar day; read it as UTC midnight,
        // which is when Copilot's monthly allowance actually rolls.
        let dateOnly = DateFormatter()
        dateOnly.locale = Locale(identifier: "en_US_POSIX")
        dateOnly.timeZone = TimeZone(secondsFromGMT: 0)
        dateOnly.dateFormat = "yyyy-MM-dd"
        return dateOnly.date(from: text)
    }

    private static func label(for id: String) -> String {
        switch id {
        case "premium_interactions": return "Premium requests"
        case "chat":                return "Chat requests"
        case "completions":         return "Completions"
        default:
            return id.replacingOccurrences(of: "_", with: " ").capitalized
        }
    }
}
