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
        let current = try await Task.detached { try load() }.value
        guard current.token == credentials.token else { throw NotchProviderError.needsAuth }
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
        hostsURL(environment: ProcessInfo.processInfo.environment, home: NSHomeDirectory())
    }

    static func hostsURL(environment: [String: String], home: String) -> URL {
        func absolute(_ value: String?) -> String? {
            guard let path = nonEmpty(value), path.hasPrefix("/") else { return nil }
            return path
        }
        let directory = absolute(environment["GH_CONFIG_DIR"])
            ?? absolute(environment["XDG_CONFIG_HOME"]).map { $0 + "/gh" }
            ?? home + "/.config/gh"
        return URL(fileURLWithPath: directory).appendingPathComponent("hosts.yml")
    }

    static func load() throws -> GitHubCopilotCredentials {
        let environment = ProcessInfo.processInfo.environment
        let hosts = CredentialFileReader.text(at: hostsURL(environment: environment, home: NSHomeDirectory()))
        return try load(environment: environment, hosts: hosts, command: ghToken)
    }

    /// Injectable inputs keep credential discovery testable without touching a
    /// real token or starting GitHub CLI.
    static func load(environment: [String: String],
                     hosts: String?,
                     command: () -> String?) throws -> GitHubCopilotCredentials {
        let parsed = parseHosts(hosts)
        if let raw = configuredEnvironmentToken(environment) {
            guard let token = nonEmpty(raw) else { throw NotchProviderError.needsAuth }
            return GitHubCopilotCredentials(token: token, username: nil,
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
        account(environment: ProcessInfo.processInfo.environment, hosts: CredentialFileReader.text(at: hostsURL))
    }

    static func account(environment: [String: String], hosts: String?) -> ProviderAccount? {
        if let raw = configuredEnvironmentToken(environment) {
            guard nonEmpty(raw) != nil else { return nil }
            return ProviderAccount(label: nil, plan: nil, source: "GitHub",
                                   manageURL: URL(string: "https://github.com/settings/copilot"))
        }
        let parsed = parseHosts(hosts)
        guard parsed.username != nil || parsed.token != nil else { return nil }
        return ProviderAccount(
            label: parsed.username,
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
        guard let text, text.utf8.count <= 262_144 else { return (nil, nil) }
        var active = false
        var seenHost = false
        var stack: [(indent: Int, key: String, container: Bool)] = []
        var childIndents: [String: Int] = [:]
        var paths: Set<String> = []
        var values: [String: String] = [:]
        for line in text.components(separatedBy: .newlines) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty || trimmed.hasPrefix("#") { continue }
            let indent = line.prefix { $0 == " " || $0 == "\t" }.count
            guard let colon = trimmed.firstIndex(of: ":") else {
                if active { return (nil, nil) }
                continue
            }
            let key = String(trimmed[..<colon])
            let raw = String(trimmed[trimmed.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
            if indent == 0 {
                active = key == "github.com"
                stack.removeAll()
                if active {
                    guard !seenHost, raw.isEmpty || raw.hasPrefix("#") else { return (nil, nil) }
                    seenHost = true
                }
                continue
            }
            guard active else { continue }
            guard !line.prefix(indent).contains("\t"), !key.isEmpty,
                  key.utf8.allSatisfy({ (65...90).contains($0) || (97...122).contains($0)
                      || (48...57).contains($0) || $0 == 45 || $0 == 95 || $0 == 46 })
            else { return (nil, nil) }
            while let last = stack.last, last.indent >= indent { stack.removeLast() }
            if let last = stack.last, !last.container { return (nil, nil) }
            let parent = stack.map(\.key).joined(separator: "/")
            if let expected = childIndents[parent], expected != indent { return (nil, nil) }
            childIndents[parent] = indent
            let path = parent.isEmpty ? key : parent + "/" + key
            guard paths.insert(path).inserted else { return (nil, nil) }
            if raw.isEmpty || raw.hasPrefix("#") {
                stack.append((indent, key, true))
                continue
            }
            guard let value = yamlScalar(raw) else { return (nil, nil) }
            values[path] = value
            stack.append((indent, key, false))
        }
        let username = nonEmpty(values["user"])
        let root = nonEmpty(values["oauth_token"])
        let selected = username.flatMap { nonEmpty(values["users/" + $0 + "/oauth_token"]) }
        if let root, let selected, root != selected { return (nil, nil) }
        return (username, root ?? selected)
    }

    private static func yamlScalar(_ raw: String) -> String? {
        if raw.hasPrefix("\"") {
            return (try? JSONSerialization.jsonObject(with: Data(raw.utf8), options: .fragmentsAllowed)) as? String
        }
        if raw.hasPrefix("'") {
            guard raw.count >= 2, raw.hasSuffix("'") else { return nil }
            return String(raw.dropFirst().dropLast()).replacingOccurrences(of: "''", with: "'")
        }
        guard let first = raw.first, !"!&*[{|>".contains(first) else { return nil }
        let value = raw.range(of: " #").map { String(raw[..<$0.lowerBound]) } ?? raw
        return value.trimmingCharacters(in: .whitespaces)
    }

    private static func configuredEnvironmentToken(_ environment: [String: String]) -> String? {
        for key in ["GH_TOKEN", "GITHUB_TOKEN"] {
            if let raw = environment[key]?.trimmingCharacters(in: .whitespacesAndNewlines), !raw.isEmpty { return raw }
        }
        return nil
    }

    private static func nonEmpty(_ value: String?) -> String? {
        guard let value else { return nil }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty || trimmed.utf8.count > 65_536
            || trimmed.rangeOfCharacter(from: .controlCharacters) != nil ? nil : trimmed
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
            let fraction = max(0, consumed / entitlement)
            guard fraction.isFinite else { return nil }
            return LimitWindow(id: id, label: label(for: id),
                               usedFraction: fraction, resetsAt: reset,
                               duration: monthlyDuration(endingAt: reset))
        }
        if let remaining, remaining >= 0, used == nil,
           let count = Int(exactly: remaining.rounded()) {
            return remaining == 0 && entitlement == 0 ? nil
                : LimitWindow(id: id, label: label(for: id),
                              remaining: count, resetsAt: reset)
        }
        if let used, used >= 0, let count = Int(exactly: used.rounded()) {
            return LimitWindow(id: id, label: label(for: id),
                               used: count, resetsAt: reset)
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
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue.isFinite else { return nil }
        return number.doubleValue
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
