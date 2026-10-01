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
    private let session: URLSession
    private let redirectDelegate = MobileNoRedirect()

    public static func validatedEndpoint(_ text: String) throws -> URL {
        guard let c = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
              c.scheme == "https", let host = c.host, !host.isEmpty,
              c.user == nil, c.password == nil, c.query == nil, c.fragment == nil,
              c.path.isEmpty || c.path == "/", let url = c.url else { throw MobileRelayError.invalidServer }
        return url
    }
    public init(endpoint: URL) throws {
        self.endpoint = try Self.validatedEndpoint(endpoint.absoluteString)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 15
        configuration.timeoutIntervalForResource = 20
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        session = URLSession(configuration: configuration)
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
