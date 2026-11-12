import Foundation

public enum MobileRelayError: LocalizedError, Sendable {
    case invalidServer, invalidResponse, rejected(Int), tooLarge, keychain, expired
    public var errorDescription: String? {
        switch self {
        case .invalidServer: "Check the relay server’s HTTPS address."
        case .invalidResponse: "The server response could not be read."
        case .rejected(401), .expired: "This connection or QR code has expired. Connect again."
        case .rejected(503): "The CodeRim relay is busy today, so new connections are paused until 00:00 UTC."
        case .rejected(403): "This request is not allowed on this device."
        case .rejected(429): "Too many requests. Try again shortly."
        case .rejected: "The relay server could not process the request."
        case .tooLarge: "The data exceeds the allowed size."
        case .keychain: "Could not save the connection details to Keychain."
        }
    }
}

/// Authentication never follows a redirect, including redirects to another HTTPS origin.
private final class MobileNoRedirect: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

public protocol MobileRelayServing: Sendable {
    var endpoint: URL { get }
    func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(
        _ method: String, _ path: String, token: String?, body: Body
    ) async throws -> Response
    func get<Response: Decodable & Sendable>(_ path: String, token: String) async throws -> Response
}

public final class MobileRelayClient: MobileRelayServing, Sendable {
    public let endpoint: URL
    // LiveActivityIntent creates a client for each tap. Reuse the app's ordinary connection so
    // opening a picker does not pay for a new DNS/TLS connection on every action.
    private static let sharedSession = URLSession(configuration: configuration(pairingPoll: false))
    private let session: URLSession
    private let pairingSession: URLSession
    private let redirectDelegate = MobileNoRedirect()

    public static func validatedEndpoint(_ text: String) throws -> URL {
        guard let c = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
              c.scheme == "https", let host = c.host, !host.isEmpty,
              c.user == nil, c.password == nil, c.query == nil, c.fragment == nil,
              c.path.isEmpty || c.path == "/" else { throw MobileRelayError.invalidServer }
        // One spelling per origin: `https://Relay.example.com:443/` and `https://relay.example.com` are the same
        // server, so a QR code from a Mac and one from a Windows PC never look like a change of server.
        var origin = URLComponents()
        origin.scheme = "https"; origin.host = host.lowercased(); origin.port = c.port == 443 ? nil : c.port
        guard let url = origin.url else { throw MobileRelayError.invalidServer }
        return url
    }
    /// Whether two addresses name the same relay, including addresses saved before they were normalized.
    public static func sameOrigin(_ a: URL, _ b: URL) -> Bool {
        guard let a = try? validatedEndpoint(a.absoluteString), let b = try? validatedEndpoint(b.absoluteString) else { return a == b }
        return a == b
    }
    public convenience init(endpoint: URL) throws {
        try self.init(endpoint: endpoint, protocolClasses: nil)
    }
    init(endpoint: URL, protocolClasses: [AnyClass]?) throws {
        self.endpoint = try Self.validatedEndpoint(endpoint.absoluteString)
        let ordinary = Self.configuration(pairingPoll: false)
        let pairing = Self.configuration(pairingPoll: true)
        if let protocolClasses { ordinary.protocolClasses = protocolClasses; pairing.protocolClasses = protocolClasses }
        session = protocolClasses == nil ? Self.sharedSession : URLSession(configuration: ordinary)
        pairingSession = URLSession(configuration: pairing)
    }
    static func configuration(pairingPoll: Bool) -> URLSessionConfiguration {
        let configuration = URLSessionConfiguration.ephemeral
        // The relay waits 20 seconds before returning an empty pairing poll.
        configuration.timeoutIntervalForRequest = pairingPoll ? 30 : 15
        configuration.timeoutIntervalForResource = pairingPoll ? 35 : 20
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        return configuration
    }
    public func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(
        _ method: String, _ path: String, token: String? = nil, body: Body
    ) async throws -> Response {
        let data = try JSONEncoder().encode(body)
        guard data.count <= 262_144 else { throw MobileRelayError.tooLarge }
        return try await request(method, path, token: token, body: data)
    }
    public func get<Response: Decodable & Sendable>(_ path: String, token: String) async throws -> Response {
        try await request("GET", path, token: token, body: nil)
    }
    private func request<Response: Decodable & Sendable>(_ method: String, _ path: String, token: String?, body: Data?) async throws -> Response {
        guard path.hasPrefix("/v1/"), !path.contains("?"), !path.contains("..") else { throw MobileRelayError.invalidServer }
        var request = URLRequest(url: endpoint.appendingPathComponent(String(path.dropFirst())))
        let pairingPoll = method == "POST" && path == "/v1/pairing/poll"
        let session = pairingPoll ? pairingSession : session
        request.timeoutInterval = pairingPoll ? 30 : 15
        request.httpMethod = method; request.httpBody = body
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        let (bytes, response) = try await session.bytes(for: request, delegate: redirectDelegate)
        guard let http = response as? HTTPURLResponse else { throw MobileRelayError.invalidResponse }
        guard http.statusCode == 200 else { throw MobileRelayError.rejected(http.statusCode) }
        var data = Data()
        for try await byte in bytes {
            guard data.count < 524_288 else { throw MobileRelayError.tooLarge }
            data.append(byte)
        }
        do { return try JSONDecoder().decode(Response.self, from: data) }
        catch { throw MobileRelayError.invalidResponse }
    }
}
