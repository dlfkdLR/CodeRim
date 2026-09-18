import Foundation
import os

/// A chunk-based response reader that cancels before buffering beyond the ceiling.
/// Uses the caller's session so its TLS policy, ephemeral storage and timeouts apply.
enum BoundedHTTP {
    /// Generous by design: the largest of these responses is a few kilobytes,
    /// so this only ever trips on something that has gone wrong.
    static let defaultMaximumBytes = 4 * 1024 * 1024

    static func data(
        for request: URLRequest,
        on session: URLSession,
        maximumBytes: Int = defaultMaximumBytes
    ) async throws -> (Data, URLResponse) {
        guard maximumBytes >= 0 else { throw NotchProviderError.responseTooLarge }
        let reader = ResponseSizeLimit(maximumBytes: maximumBytes)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                let task = session.dataTask(with: request)
                task.delegate = reader
                reader.start(task, continuation: continuation)
            }
        } onCancel: { reader.cancel() }
    }

    /// Headers a caller sets by hand, which URLSession copies onto a redirect
    /// target. A hand-set `Cookie` demonstrably survives a cross-host redirect
    /// — `testARealCrossHostRedirectArrivesWithoutTheCredential` fails without
    /// this list — and the vendor-specific token headers are not special-cased
    /// by anything. `Authorization` is included regardless of what Foundation
    /// may already do with it: this is the layer that knows these are borrowed
    /// credentials, so it is the layer that should not rely on a guess.
    static let credentialHeaders = ["Authorization", "Cookie", "X-XAI-Token-Auth",
                                    "x-codeium-csrf-token"]

    /// Follow a redirect, but never carry a borrowed credential to a host the
    /// caller did not choose. Same-host redirects (an added trailing slash, an
    /// http→https upgrade) keep the headers and behave exactly as before;
    /// anything that changes host travels without them, so a redirect cannot
    /// turn into credential exfiltration. Stripping rather than refusing keeps
    /// a provider that legitimately redirects working — and no provider here
    /// currently redirects across hosts, so nothing that works today changes.
    static func redirect(from original: URLRequest, to proposed: URLRequest) -> URLRequest {
        guard original.url?.host?.lowercased() != proposed.url?.host?.lowercased() else {
            return proposed
        }
        var stripped = proposed
        for header in credentialHeaders { stripped.setValue(nil, forHTTPHeaderField: header) }
        return stripped
    }
}

/// Per-task delegate; it never creates or invalidates the caller's session.
private final class ResponseSizeLimit: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private struct State {
        var task: URLSessionDataTask?
        var continuation: CheckedContinuation<(Data, URLResponse), Error>?
        var response: URLResponse?
        var data = Data()
        var cancelled = false
    }
    private let maximumBytes: Int
    private let state = OSAllocatedUnfairLock(initialState: State())

    init(maximumBytes: Int) { self.maximumBytes = maximumBytes }

    func start(_ task: URLSessionDataTask,
               continuation: CheckedContinuation<(Data, URLResponse), Error>) {
        let cancelled = state.withLock { value in
            guard !value.cancelled else { return true }
            value.task = task
            value.continuation = continuation
            return false
        }
        if cancelled {
            task.cancel()
            continuation.resume(throwing: CancellationError())
        } else { task.resume() }
    }

    func cancel() {
        state.withLock { $0.cancelled = true }
        finish(error: CancellationError(), cancelTask: true)
    }

    private func finish(error: Error? = nil, cancelTask: Bool = false) {
        let result = state.withLock { value -> (URLSessionDataTask?, CheckedContinuation<(Data, URLResponse), Error>?, Data, URLResponse?) in
            let result = (value.task, value.continuation, value.data, value.response)
            value.task = nil; value.continuation = nil; value.data = Data(); value.response = nil
            return result
        }
        if cancelTask { result.0?.cancel() }
        guard let continuation = result.1 else { return }
        if let error { continuation.resume(throwing: error) }
        else if let response = result.3 { continuation.resume(returning: (result.2, response)) }
        else { continuation.resume(throwing: URLError(.badServerResponse)) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest) async -> URLRequest? {
        guard let original = task.originalRequest else { return request }
        return BoundedHTTP.redirect(from: original, to: request)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse) async -> URLSession.ResponseDisposition {
        guard response.expectedContentLength <= Int64(maximumBytes) else {
            finish(error: NotchProviderError.responseTooLarge, cancelTask: true)
            return .cancel
        }
        return state.withLock { value in
            guard value.continuation != nil else { return .cancel }
            value.response = response
            return .allow
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        let oversized = state.withLock { value in
            guard value.continuation != nil else { return false }
            guard data.count <= maximumBytes - value.data.count else { return true }
            value.data.append(data)
            return false
        }
        if oversized { finish(error: NotchProviderError.responseTooLarge, cancelTask: true) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        finish(error: error)
    }
}
