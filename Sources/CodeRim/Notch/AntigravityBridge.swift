import Darwin
import Foundation

/// Asks Antigravity's own language server for the Gemini quota, instead of
/// asking Google directly.
///
/// Google refuses a third-party client: `retrieveUserQuotaSummary` on
/// `cloudcode-pa` answers 403 for a personal account, because the API judges
/// *which client* is asking. Antigravity's own window has the same problem and
/// solves it the same way — it calls the language server running on this
/// machine, which already holds the credential and the client identity. So
/// this is the door Antigravity itself uses; it works only while Antigravity
/// (the IDE or the `agy` CLI) is running, which is honest — the figure comes
/// from Antigravity.
///
/// Ported from the MIT-licensed Codenotch (`AntigravityBridge`), trimmed to the
/// bridge path (no Google fallback, no counted-requests fallback).
enum AntigravityBridge {
    /// Where the language server is listening, and the token it demands.
    struct Endpoint: Equatable, Sendable {
        /// It opens more than one port and only one serves this RPC; which is
        /// not advertised, so all are tried.
        let listeners: [LocalTCPPeer.Address]
        let process: LocalTCPPeer.ProcessIdentity
        /// Nil for the CLI, which serves this RPC to anything on loopback.
        let csrfToken: String?
    }

    /// Antigravity is built on Codeium's stack, and the header still says so.
    static let csrfHeader = "x-codeium-csrf-token"

    private static let service =
        "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary"

    // MARK: - Finding it

    static func discover(processTable: String? = nil,
                         listeningAddresses: ((pid_t) -> [LocalTCPPeer.Address])? = nil,
                         processIdentity: (pid_t) -> LocalTCPPeer.ProcessIdentity? = LocalTCPPeer.ProcessIdentity.read) -> Endpoint? {
        // Only this user's processes. The line below reads another process's
        // command line to lift its `--csrf_token`, which is the one thing that
        // makes this RPC answer — so the search has to stop at the boundary
        // where that stops being our own credential. `-A` crossed it: on a
        // shared Mac it would find a second account's language server and read
        // that person's quota. `-x` keeps processes with no controlling
        // terminal, which is every GUI-launched one.
        let table = processTable ?? run("/bin/ps", ["-xo", "pid,command", "-U", "\(getuid())"])
        let lines = table.split(separator: "\n")

        // The IDE's language server carries a token.
        if let line = lines.first(where: {
            $0.contains("language_server") && $0.contains("--csrf_token")
        }),
           let token = value(of: "--csrf_token", in: String(line)),
           let endpoint = endpoint(for: line, token: token, listeners: listeningAddresses,
                                   identity: processIdentity, injectedTable: processTable != nil) {
            return endpoint
        }

        // The `agy` CLI serves the same RPC and asks for no token.
        if let line = lines.first(where: isCLI),
           let endpoint = endpoint(for: line, token: nil, listeners: listeningAddresses,
                                   identity: processIdentity, injectedTable: processTable != nil) {
            return endpoint
        }
        return nil
    }

    /// The CLI runs as plain `agy`, so match the executable's own name — "agy"
    /// is three letters and turns up inside real words and paths.
    static func isCLI(_ line: Substring) -> Bool {
        let fields = line.trimmingCharacters(in: .whitespaces).split(separator: " ")
        guard fields.count >= 2 else { return false }
        return URL(fileURLWithPath: String(fields[1])).lastPathComponent == "agy"
    }

    private static func endpoint(
        for line: Substring, token: String?, listeners: ((pid_t) -> [LocalTCPPeer.Address])?,
        identity: (pid_t) -> LocalTCPPeer.ProcessIdentity?, injectedTable: Bool
    ) -> Endpoint? {
        let command = line.trimmingCharacters(in: .whitespaces)
        guard let pid = pid_t(command.split(separator: " ").first ?? ""),
              let process = identity(pid), process.pid == pid, process.userID == getuid() else { return nil }
        // Capture lifetime before re-reading arguments: a PID recycled since
        // the original ps scan must not inherit that scan's credential.
        if !injectedTable {
            let current = run("/bin/ps", ["-p", "\(pid)", "-o", "pid=,command="])
                .trimmingCharacters(in: .whitespacesAndNewlines)
            guard current == command else { return nil }
        }
        let addresses = listeners?(pid) ?? LocalTCPPeer.listeners(of: process)
        guard !addresses.isEmpty, addresses.count <= 32,
              addresses.allSatisfy({ $0.isLoopback && (1...65535).contains($0.port) }),
              identity(pid) == process else { return nil }
        return Endpoint(listeners: addresses, process: process, csrfToken: token)
    }

    static func value(of flag: String, in line: String) -> String? {
        let parts = line.split(separator: " ")
        guard let index = parts.firstIndex(of: Substring(flag)),
              index + 1 < parts.count else { return nil }
        return String(parts[index + 1])
    }

    // MARK: - Asking it

    static func quota(from endpoint: Endpoint) async throws -> [LimitWindow] {
        var lastError: Error?
        for listener in endpoint.listeners {
            do {
                let windows = try await quota(listener: listener, endpoint: endpoint)
                if !windows.isEmpty { return windows }
            } catch {
                lastError = error
            }
        }
        if let lastError { throw lastError }
        return []
    }

    private static func quota(listener: LocalTCPPeer.Address, endpoint: Endpoint) async throws -> [LimitWindow] {
        guard endpoint.process.isCurrent, listener.isLoopback,
              (1...65535).contains(listener.port) else { throw URLError(.secureConnectionFailed) }
        let trust = LocalhostTrust(listener: listener, process: endpoint.process)
        // One session, one endpoint, one TLS handshake: never reuse a prior
        // poll's authenticated connection after a server exits or restarts.
        let configuration = URLSessionConfiguration.ephemeral
        configuration.connectionProxyDictionary = [:]
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        let session = URLSession(configuration: configuration, delegate: trust, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: URL(string: "https://\(listener.authority)\(service)")!)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token = endpoint.csrfToken { request.setValue(token, forHTTPHeaderField: csrfHeader) }
        // `forceRefresh` bypasses the language server's QuotaSummaryCache.
        request.httpBody = Data(#"{"forceRefresh":true}"#.utf8)
        request.timeoutInterval = 10

        let (data, response) = try await BoundedHTTP.data(for: request, on: session, rejectRedirects: true)
        guard trust.verified, endpoint.process.isCurrent else { throw URLError(.secureConnectionFailed) }
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw NotchProviderError.badResponse(
                status: (response as? HTTPURLResponse)?.statusCode ?? 0
            )
        }
        return windows(in: data)
    }

    /// Turns the quota summary into limit windows. The server reports what is
    /// **left**; the notch shows what is spent, so the fraction is inverted
    /// here rather than in the view.
    static func windows(in data: Data) -> [LimitWindow] {
        struct Response: Decodable {
            struct Bucket: Decodable {
                let bucketId: String?
                let displayName: String?
                let remainingFraction: Double?
                let resetTime: String?
                let window: String?
            }
            struct Group: Decodable {
                let displayName: String?
                let buckets: [Bucket]?
            }
            struct Body: Decodable { let groups: [Group]? }
            let response: Body?
        }

        guard let decoded = try? JSONDecoder().decode(Response.self, from: data),
              let groups = decoded.response?.groups
        else { return [] }

        return groups.flatMap { group -> [LimitWindow] in
            (group.buckets ?? []).compactMap { bucket in
                guard let remaining = bucket.remainingFraction,
                      remaining >= 0, remaining <= 1
                else { return nil }
                let id = bucket.bucketId ?? group.displayName ?? "quota"

                var label = bucket.displayName ?? "Usage"
                if label.hasSuffix(" Remaining") { label = String(label.dropLast(" Remaining".count)) }
                let groupLabel = group.displayName ?? ""

                return LimitWindow(
                    id: id,
                    group: groupLabel.isEmpty ? nil : groupLabel,
                    label: label,
                    usedFraction: 1 - remaining,
                    resetsAt: bucket.resetTime.flatMap(parseISO8601),
                    duration: bucket.window == "weekly" ? 7 * 86400 : nil
                )
            }
        }
    }

    static func parseISO8601(_ value: String) -> Date? {
        let withFraction = ISO8601DateFormatter()
        withFraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = withFraction.date(from: value) { return date }
        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        return plain.date(from: value)
    }

    // MARK: - Plumbing

    /// Bound process-table reads so a failed discovery cannot wedge a poll.
    private static func run(_ path: String, _ arguments: [String]) -> String {
        let inherited = ProcessInfo.processInfo.environment
        let environment = ["PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
                           "LANG": inherited["LANG"] ?? "en_US.UTF-8"]
        guard let result = try? BoundedProcess.runSynchronously(
            executable: URL(fileURLWithPath: path), arguments: arguments,
            environment: environment, timeout: .seconds(5), maximumOutputBytes: 4 * 1_024 * 1_024
        ) else { return "" }
        return String(decoding: result.output, as: UTF8.self)
    }
}
