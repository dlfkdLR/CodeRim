import Foundation
import Network

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
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        let listener = try NWListener(using: parameters)
        self.listener = listener
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            connection.start(queue: self.queue)
            self.receive(connection, accumulated: Data())
        }
        return try await withCheckedThrowingContinuation { continuation in
            lock.lock(); readyContinuation = continuation; lock.unlock()
            listener.stateUpdateHandler = { [weak self, weak listener] state in
                switch state {
                case .ready:
                    guard let port = listener?.port else {
                        let error = POSIXError(.EADDRNOTAVAIL)
                        self?.finishReady(.failure(error)); self?.finish(.failure(error))
                        listener?.cancel()
                        return
                    }
                    self?.finishReady(.success(URL(string: "http://127.0.0.1:\(port)/callback")!))
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
}
