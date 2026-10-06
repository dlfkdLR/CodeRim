import AppKit
import CodexBarCore
import Darwin
import Foundation
import Network

/// A sign-in CodeRim carries out itself, adapted from CodexBar's login flows (MIT): it needs
/// no command-line tool, and what it returns is kept in this app's own Keychain item for the
/// provider, where the upstream reader picks it up.
enum InAppSignIn: Equatable {
    /// GitHub's device flow: a short code approved on github.com yields a Copilot token.
    case githubDevice
    /// Google account consent for Antigravity through a local loopback redirect.
    case antigravityGoogle
}

@MainActor
enum InAppSignInRunner {
    enum Outcome: Equatable {
        case signedIn
        case failed(String)
    }

    /// Runs the sign-in. `update` receives what the user has to do right now, for the waiting UI.
    static func run(_ kind: InAppSignIn, update: @escaping @MainActor (String) -> Void) async -> Outcome {
        switch kind {
        case .githubDevice:
            let flow = CopilotDeviceFlow()
            return await gitHubDevice(
                requestCode: {
                    let code = try await flow.requestDeviceCode()
                    return (code.userCode, code.deviceCode, URL(string: code.verificationURLToOpen), code.interval)
                },
                poll: { device, interval in try await flow.pollForToken(deviceCode: device, interval: interval) },
                save: { token in try store(for: .copilot) { $0.provider.apiKey = token } },
                open: { NSWorkspace.shared.open($0) },
                update: update)
        case .antigravityGoogle:
            return await antigravityGoogle(client: { AntigravityOAuthConfig.resolvedClient() }, update: update)
        }
    }

    // MARK: GitHub device flow

    static func gitHubDevice(
        requestCode: () async throws -> (user: String, device: String, verify: URL?, interval: Int),
        poll: (String, Int) async throws -> String,
        save: (String) throws -> Void,
        open: (URL) -> Void,
        update: @escaping @MainActor (String) -> Void) async -> Outcome {
        do {
            let code = try await requestCode()
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(code.user, forType: .string)
            update("Enter the code \(code.user) on GitHub (it is already copied), approve CodeRim, and it connects on its own.")
            if let url = code.verify { open(url) }
            let token = try await poll(code.device, code.interval)
            try save(token)
            return .signedIn
        } catch is CancellationError {
            return .failed("Sign-in was cancelled.")
        } catch {
            return .failed("GitHub sign-in did not finish: \(error.localizedDescription) Choose Try again for a new code.")
        }
    }

    // MARK: Antigravity Google sign-in

    static func antigravityGoogle(client resolveClient: () -> AntigravityOAuthClient?,
                                  open: (URL) -> Bool = { NSWorkspace.shared.open($0) },
                                  timeout: Duration = .seconds(300),
                                  update: @escaping @MainActor (String) -> Void) async -> Outcome {
        guard let client = resolveClient() else {
            return .failed("Install the Antigravity app first — CodeRim signs in with the same Google sign-in it uses. Then choose Try again.")
        }
        let state = UUID().uuidString.replacingOccurrences(of: "-", with: "")
        let server = OAuthLoopbackServer(state: state)
        do {
            let callbackURL = try await server.start()
            var components = URLComponents(url: AntigravityOAuthConfig.authURL, resolvingAgainstBaseURL: false)!
            components.queryItems = [
                URLQueryItem(name: "client_id", value: client.clientID),
                URLQueryItem(name: "redirect_uri", value: callbackURL.absoluteString),
                URLQueryItem(name: "response_type", value: "code"),
                URLQueryItem(name: "scope", value: AntigravityOAuthConfig.scopes.joined(separator: " ")),
                URLQueryItem(name: "access_type", value: "offline"),
                URLQueryItem(name: "prompt", value: "select_account consent"),
                URLQueryItem(name: "state", value: state),
            ]
            guard let authURL = components.url, open(authURL) else {
                server.stop(); return .failed("The Google sign-in page could not be opened.")
            }
            update("Choose your Google account in the browser and allow access. CodeRim connects as soon as you finish.")
            let callback = try await withThrowingTaskGroup(of: OAuthCallback.self) { group in
                group.addTask { try await server.waitForCallback() }
                // Ends the wait on timeout and on Cancel alike: the callback wait is a plain
                // continuation that ignores cancellation, and the group cannot return while it hangs.
                group.addTask {
                    try? await Task.sleep(for: timeout)
                    server.cancelCallbackWait(with: CancellationError())
                    throw CancellationError()
                }
                defer { group.cancelAll() }
                return try await group.next()!
            }
            server.stop()
            if let error = callback.error, !error.isEmpty {
                return .failed(error == "access_denied" ? "Google sign-in was cancelled." : "Google sign-in failed: \(error)")
            }
            guard callback.state == state, let code = callback.code, !code.isEmpty else {
                return .failed("Google did not return a sign-in code. Try again.")
            }
            let tokens = try await exchange(code: code, redirect: callbackURL, client: client)
            let credentials = AntigravityOAuthCredentials(
                accessToken: tokens.accessToken, refreshToken: tokens.refreshToken,
                expiryDate: Date().addingTimeInterval(TimeInterval(tokens.expiresIn)), idToken: tokens.idToken,
                email: nil, projectID: nil, clientID: client.clientID, clientSecret: client.clientSecret)
            let value = try AntigravityOAuthCredentialsStore.tokenAccountValue(for: credentials)
            try store(for: .antigravity) {
                $0.environment[AntigravityOAuthCredentialsStore.environmentCredentialsKey] = value
            }
            return .signedIn
        } catch is CancellationError {
            server.stop(); return .failed("Google sign-in timed out or was cancelled.")
        } catch {
            server.stop(); return .failed("Google sign-in failed: \(error.localizedDescription)")
        }
    }

    private struct Tokens: Decodable {
        let accessToken: String
        let refreshToken: String?
        let expiresIn: Int
        let idToken: String?
        enum CodingKeys: String, CodingKey {
            case accessToken = "access_token", refreshToken = "refresh_token", expiresIn = "expires_in", idToken = "id_token"
        }
    }

    private static func exchange(code: String, redirect: URL, client: AntigravityOAuthClient) async throws -> Tokens {
        var request = URLRequest(url: AntigravityOAuthConfig.tokenURL)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        var allowed = CharacterSet.urlQueryAllowed; allowed.remove(charactersIn: "+&=")
        let form = ["code": code, "client_id": client.clientID, "client_secret": client.clientSecret,
                    "redirect_uri": redirect.absoluteString, "grant_type": "authorization_code"]
        request.httpBody = form.map { "\($0.key)=\($0.value.addingPercentEncoding(withAllowedCharacters: allowed) ?? $0.value)" }
            .joined(separator: "&").data(using: .utf8)
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw NSError(domain: "CodeRim.GoogleSignIn", code: (response as? HTTPURLResponse)?.statusCode ?? -1,
                          userInfo: [NSLocalizedDescriptionKey: "Google refused the sign-in code."])
        }
        return try JSONDecoder().decode(Tokens.self, from: data)
    }

    /// Writes into this app's Keychain item for the provider, turning nothing else on.
    static func store(for id: CodexBarCore.UsageProvider, _ change: (inout ExtendedProviderConfiguration) -> Void) throws {
        let descriptor = ProviderDescriptorRegistry.descriptor(for: id)
        // A refused or unreadable item must not be replaced by a blank one: that would erase the
        // keys the user saved. A missing item already loads as a fresh configuration.
        var configuration = try ExtendedProviderConfigurationStore.load(descriptor, interactive: true)
        change(&configuration)
        try ExtendedProviderConfigurationStore.save(configuration)
    }
}

// MARK: - Loopback redirect (from CodexBar's AntigravityLoginRunner)

struct OAuthCallback: Sendable {
    let code: String?
    let state: String?
    let error: String?
}

final class OAuthLoopbackServer: @unchecked Sendable {
    private let expectedState: String
    private let queue = DispatchQueue(label: "coderim.oauth.loopback")
    private let lock = NSLock()
    private var listener: NWListener?
    private var readyContinuation: CheckedContinuation<URL, Error>?
    private var callbackContinuation: CheckedContinuation<OAuthCallback, Error>?
    private var pending: Result<OAuthCallback, Error>?
    private var completed = false

    init(state: String) { expectedState = state }

    func start() async throws -> URL {
        let port = try Self.freePort()
        guard let endpointPort = NWEndpoint.Port(rawValue: port) else { throw CocoaError(.fileWriteUnknown) }
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: endpointPort)
        let listener = try NWListener(using: parameters)
        self.listener = listener
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            connection.start(queue: self.queue)
            self.receive(connection, accumulated: Data())
        }
        return try await withCheckedThrowingContinuation { continuation in
            lock.lock(); readyContinuation = continuation; lock.unlock()
            listener.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready: self?.finishReady(.success(URL(string: "http://127.0.0.1:\(port)/callback")!))
                case .failed(let error): self?.finishReady(.failure(error)); self?.finish(.failure(error))
                default: break
                }
            }
            listener.start(queue: queue)
        }
    }

    func waitForCallback() async throws -> OAuthCallback {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock(); defer { lock.unlock() }
            if let pending {
                self.pending = nil
                continuation.resume(with: pending)
                return
            }
            callbackContinuation = continuation
        }
    }

    func stop() { listener?.cancel(); listener = nil }

    func cancelCallbackWait(with error: Error) { stop(); finish(.failure(error)) }

    private func receive(_ connection: NWConnection, accumulated: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let error { self.finish(.failure(error)); connection.cancel(); return }
            var buffer = accumulated
            if let data { buffer.append(data) }
            // A redirect is one short request line. Anything this large is not one; answer and
            // close rather than buffering whatever a local process keeps sending.
            if buffer.count > 16_384 {
                connection.send(content: Data("HTTP/1.1 431 Request Header Fields Too Large\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".utf8),
                                completion: .contentProcessed { _ in connection.cancel() })
                return
            }
            if buffer.range(of: Data("\r\n\r\n".utf8)) == nil, !isComplete { self.receive(connection, accumulated: buffer); return }
            let callback = self.parse(buffer)
            // Browsers also ask for /favicon.ico; only the callback path settles the sign-in.
            guard let callback else {
                connection.send(content: Data("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".utf8),
                                completion: .contentProcessed { _ in connection.cancel() })
                return
            }
            let ok = callback.error == nil && callback.code?.isEmpty == false
            let html = """
            <html><body style="font-family:-apple-system,sans-serif;padding:40px;text-align:center">
            <h2>\(ok ? "Signed in" : "Sign-in failed")</h2>
            <p>\(ok ? "You can close this tab and return to CodeRim." : "Close this tab and choose Try again in CodeRim.")</p>
            </body></html>
            """
            let body = Data(html.utf8)
            var response = Data("HTTP/1.1 \(ok ? "200 OK" : "400 Bad Request")\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(body.count)\r\nConnection: close\r\n\r\n".utf8)
            response.append(body)
            connection.send(content: response, completion: .contentProcessed { _ in connection.cancel() })
            self.finish(.success(callback))
        }
    }

    private func parse(_ data: Data) -> OAuthCallback? {
        guard let line = String(data: data, encoding: .utf8)?.components(separatedBy: "\r\n").first else { return nil }
        let parts = line.split(separator: " ")
        guard parts.count >= 2, let url = URL(string: "http://127.0.0.1\(parts[1])"),
              let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              components.path == "/callback" else { return nil }
        let item = { (name: String) in components.queryItems?.first { $0.name == name }?.value }
        guard let state = item("state") else { return OAuthCallback(code: nil, state: nil, error: "missing state") }
        if state != expectedState { return OAuthCallback(code: nil, state: state, error: "state mismatch") }
        return OAuthCallback(code: item("code"), state: item("state"), error: item("error"))
    }

    private func finishReady(_ result: Result<URL, Error>) {
        lock.lock(); let continuation = readyContinuation; readyContinuation = nil; lock.unlock()
        continuation?.resume(with: result)
    }

    private func finish(_ result: Result<OAuthCallback, Error>) {
        lock.lock()
        guard !completed else { lock.unlock(); return }
        completed = true
        let continuation = callbackContinuation
        callbackContinuation = nil
        if continuation == nil { pending = result }
        lock.unlock()
        continuation?.resume(with: result)
    }

    private static func freePort() throws -> UInt16 {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw POSIXError(.EIO) }
        defer { close(fd) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.stride)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = 0
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.stride)) }
        }
        guard bound == 0 else { throw POSIXError(.EADDRINUSE) }
        var name = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.stride)
        let got = withUnsafeMutablePointer(to: &name) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) }
        }
        guard got == 0 else { throw POSIXError(.EIO) }
        return UInt16(bigEndian: name.sin_port)
    }
}
