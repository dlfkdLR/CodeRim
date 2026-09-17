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
    struct Endpoint: Equatable {
        /// It opens more than one port and only one serves this RPC; which is
        /// not advertised, so all are tried.
        let ports: [Int]
        /// Nil for the CLI, which serves this RPC to anything on loopback.
        let csrfToken: String?
    }

    /// Antigravity is built on Codeium's stack, and the header still says so.
    static let csrfHeader = "x-codeium-csrf-token"

    private static let service =
        "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary"

    // MARK: - Finding it

    static func discover(processTable: String? = nil,
                         listeningPorts: ((Int) -> [Int])? = nil) -> Endpoint? {
        let table = processTable ?? run("/bin/ps", ["-Ao", "pid,command"])
        let lines = table.split(separator: "\n")

        // The IDE's language server carries a token.
        if let line = lines.first(where: {
            $0.contains("language_server") && $0.contains("--csrf_token")
        }),
           let token = value(of: "--csrf_token", in: String(line)),
           let endpoint = endpoint(for: line, token: token, ports: listeningPorts) {
            return endpoint
        }

        // The `agy` CLI serves the same RPC and asks for no token.
        if let line = lines.first(where: isCLI),
           let endpoint = endpoint(for: line, token: nil, ports: listeningPorts) {
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
        for line: Substring, token: String?, ports listeningPorts: ((Int) -> [Int])?
    ) -> Endpoint? {
        guard let pid = Int(line.trimmingCharacters(in: .whitespaces)
            .split(separator: " ").first ?? "")
        else { return nil }
        let ports = listeningPorts?(pid) ?? self.listeningPorts(ofPID: pid)
        guard !ports.isEmpty else { return nil }
        return Endpoint(ports: ports, csrfToken: token)
    }

    static func value(of flag: String, in line: String) -> String? {
        let parts = line.split(separator: " ")
        guard let index = parts.firstIndex(of: Substring(flag)),
              index + 1 < parts.count else { return nil }
        return String(parts[index + 1])
    }

    static func listeningPorts(ofPID pid: Int) -> [Int] {
        // `-a` ANDs the filters; without it lsof ORs them and returns every
        // listening socket on the machine.
        let output = run("/usr/sbin/lsof", ["-nP", "-a", "-p", "\(pid)", "-iTCP", "-sTCP:LISTEN"])
        return parsePorts(fromLSOF: output)
    }

    static func parsePorts(fromLSOF output: String) -> [Int] {
        output.split(separator: "\n").compactMap { line in
            guard let address = line.split(separator: " ").last(where: { $0.contains(":") }),
                  let port = Int(address.split(separator: ":").last ?? "") else { return nil }
            return port
        }
    }

    // MARK: - Asking it

    static func quota(from endpoint: Endpoint, session: URLSession) async throws -> [LimitWindow] {
        var lastError: Error?
        for port in endpoint.ports {
            do {
                let windows = try await quota(port: port, token: endpoint.csrfToken, session: session)
                if !windows.isEmpty { return windows }
            } catch {
                lastError = error
            }
        }
        if let lastError { throw lastError }
        return []
    }

    private static func quota(port: Int, token: String?,
                              session: URLSession) async throws -> [LimitWindow] {
        var request = URLRequest(url: URL(string: "https://127.0.0.1:\(port)\(service)")!)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token { request.setValue(token, forHTTPHeaderField: csrfHeader) }
        // `forceRefresh` is why this reads as live: the language server keeps a
        // `QuotaSummaryCache` and an empty request is served from it.
        request.httpBody = Data(#"{"forceRefresh":true}"#.utf8)
        request.timeoutInterval = 10

        let (data, response) = try await BoundedHTTP.data(for: request, on: session)
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

    private static func run(_ path: String, _ arguments: [String]) -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        guard (try? process.run()) != nil else { return "" }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return String(decoding: data, as: UTF8.self)
    }
}
