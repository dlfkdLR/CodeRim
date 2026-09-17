import Foundation
import os

/// `URLSession.data(for:)` with a ceiling on the response body.
///
/// `data(for:)` will happily hold whatever a server sends, and the notch's
/// providers read nine third-party endpoints on a timer. A vendor incident that
/// returns a very large body should fail that one ring, not grow the app's
/// memory until macOS kills it — the subprocess and file paths are already
/// bounded this way (`BoundedProcess`, `ClaudeProfileFile`), and this closes the
/// same gap on the network side.
///
/// Two checks, because either alone leaves a hole:
///
/// - A per-task delegate refuses the body as soon as the headers declare a
///   length over the ceiling, so nothing large is ever downloaded.
/// - A size check on what actually arrived catches a response that declared no
///   length, or lied about it.
///
/// Deliberately *not* built on `URLSession.bytes(for:)`. Its `AsyncSequence` is
/// per-byte, and measured against a local server it cost 367 ms for a 32 KB
/// body where `data(for:)` cost 3.8 ms — a 96× regression on every poll, which
/// is a far worse outcome than the case it guards against.
enum BoundedHTTP {
    /// Generous by design: the largest of these responses is a few kilobytes,
    /// so this only ever trips on something that has gone wrong.
    static let defaultMaximumBytes = 4 * 1024 * 1024

    static func data(
        for request: URLRequest,
        on session: URLSession,
        maximumBytes: Int = defaultMaximumBytes
    ) async throws -> (Data, URLResponse) {
        let limit = ResponseSizeLimit(maximumBytes: maximumBytes)
        let (data, response): (Data, URLResponse)
        do {
            (data, response) = try await session.data(for: request, delegate: limit)
        } catch {
            // The delegate cancels by refusing the body, which surfaces here as
            // a cancellation. Report why rather than as a generic failure.
            if limit.didRefuse { throw NotchProviderError.responseTooLarge }
            throw error
        }
        guard data.count <= maximumBytes else { throw NotchProviderError.responseTooLarge }
        return (data, response)
    }
}

/// Refuses a response whose declared length is over the ceiling.
///
/// A per-task delegate rather than a session-wide one: it needs no session of
/// its own to own and invalidate, and no continuation to bridge, so it cannot
/// leak a session or strand a caller.
private final class ResponseSizeLimit: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let maximumBytes: Int
    /// Set on URLSession's delegate queue, read by the caller once the task has
    /// finished. `OSAllocatedUnfairLock` rather than `NSLock` because the
    /// delegate method is `async`, where `NSLock` is unavailable.
    private let refused = OSAllocatedUnfairLock(initialState: false)

    init(maximumBytes: Int) {
        self.maximumBytes = maximumBytes
    }

    var didRefuse: Bool { refused.withLock { $0 } }

    func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive response: URLResponse
    ) async -> URLSession.ResponseDisposition {
        // `expectedContentLength` is `NSURLResponseUnknownLength` (-1) when the
        // server declares none, which must not read as "enormous" or as "fine".
        guard response.expectedContentLength > Int64(maximumBytes) else { return .allow }
        refused.withLock { $0 = true }
        return .cancel
    }
}
