import Foundation
import XCTest
@testable import CodeRim

/// The bound has to hold against a server that declares an enormous body *and*
/// against one that declares nothing and just keeps sending — those fail in
/// different places, and only one of them can be caught from the headers.
final class BoundedHTTPTests: XCTestCase {
    private var server: LocalHTTPServer!

    override func setUp() async throws {
        server = try LocalHTTPServer()
    }

    override func tearDown() async throws {
        server.stop()
        server = nil
    }

    func testAnOrdinaryResponseIsReturnedUnchanged() async throws {
        let payload = Data(repeating: UInt8(ascii: "a"), count: 4_096)
        server.respond(with: payload, declaringLength: true)

        let (data, response) = try await BoundedHTTP.data(
            for: URLRequest(url: server.url), on: .shared, maximumBytes: 1_048_576
        )
        XCTAssertEqual(data, payload)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
    }

    /// The cheap case: the server says how big it is, so nothing is downloaded.
    func testADeclaredOversizeBodyIsRefused() async throws {
        server.respond(with: Data(repeating: 0x61, count: 256_000), declaringLength: true)

        do {
            _ = try await BoundedHTTP.data(
                for: URLRequest(url: server.url), on: .shared, maximumBytes: 4_096
            )
            XCTFail("an oversize response was accepted")
        } catch NotchProviderError.responseTooLarge {
            // expected
        }
    }

    /// The case headers cannot catch: no declared length at all.
    func testAnUndeclaredOversizeBodyIsStillRefused() async throws {
        server.respond(with: Data(repeating: 0x61, count: 256_000), declaringLength: false)

        do {
            _ = try await BoundedHTTP.data(
                for: URLRequest(url: server.url), on: .shared, maximumBytes: 4_096
            )
            XCTFail("an oversize response was accepted")
        } catch NotchProviderError.responseTooLarge {
            // expected
        }
    }

    func testUndeclaredOversizeBodyIsCancelledBeforeEOF() async throws {
        server.respond(with: Data(repeating: 0x61, count: 65_536), declaringLength: false, holdOpen: true)
        let start = ContinuousClock.now
        do {
            _ = try await BoundedHTTP.data(for: URLRequest(url: server.url), on: .shared, maximumBytes: 4_096)
            XCTFail("oversize body was accepted")
        } catch NotchProviderError.responseTooLarge {}
        XCTAssertLessThan(start.duration(to: .now), .seconds(2), "Must reject before the server's delayed EOF")
    }

    func testCallerCancellationDoesNotWaitForEOF() async throws {
        server.respond(with: Data([0x61]), declaringLength: false, holdOpen: true)
        let url = server.url
        let task = Task { try await BoundedHTTP.data(for: URLRequest(url: url), on: .shared) }
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while server.requests.isEmpty, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(5)) }
        let start = ContinuousClock.now
        task.cancel()
        do { _ = try await task.value; XCTFail("cancelled request succeeded") }
        catch { XCTAssertTrue(error is CancellationError || (error as? URLError)?.code == .cancelled) }
        XCTAssertLessThan(start.duration(to: .now), .seconds(2))
    }

    /// A body exactly at the ceiling is legitimate and must not be refused.
    func testABodyAtTheCeilingIsAccepted() async throws {
        let payload = Data(repeating: 0x61, count: 4_096)
        server.respond(with: payload, declaringLength: true)

        let (data, _) = try await BoundedHTTP.data(
            for: URLRequest(url: server.url), on: .shared, maximumBytes: 4_096
        )
        XCTAssertEqual(data.count, 4_096)
    }

    // MARK: Redirects

    /// A redirect that stays on the host the caller chose is the ordinary
    /// case — a trailing slash, an https upgrade — and must not lose the
    /// credential, or every such redirect turns into a spurious 401.
    func testASameHostRedirectKeepsTheBorrowedCredential() {
        var original = URLRequest(url: URL(string: "https://cursor.com/api/usage-summary")!)
        original.setValue("WorkosCursorSessionToken=abc::xyz", forHTTPHeaderField: "Cookie")
        var proposed = original
        proposed.url = URL(string: "https://cursor.com/api/usage-summary/")!

        let followed = BoundedHTTP.redirect(from: original, to: proposed)
        XCTAssertEqual(followed.value(forHTTPHeaderField: "Cookie"),
                       "WorkosCursorSessionToken=abc::xyz")
    }

    /// URLSession copies a hand-set header onto the redirect target, so a
    /// vendor redirect — or a compromised one — would otherwise hand the
    /// session cookie to whichever host the `Location` names.
    func testACrossHostRedirectTravelsWithoutTheCredential() {
        var original = URLRequest(url: URL(string: "https://cursor.com/api/usage-summary")!)
        original.setValue("WorkosCursorSessionToken=abc::xyz", forHTTPHeaderField: "Cookie")
        original.setValue("Bearer grok-token", forHTTPHeaderField: "Authorization")
        original.setValue("xai-grok-cli", forHTTPHeaderField: "X-XAI-Token-Auth")
        original.setValue("application/json", forHTTPHeaderField: "Accept")
        var proposed = original
        proposed.url = URL(string: "https://collector.example.com/catch")!

        let followed = BoundedHTTP.redirect(from: original, to: proposed)
        for header in BoundedHTTP.credentialHeaders {
            XCTAssertNil(followed.value(forHTTPHeaderField: header), header)
        }
        // Only the credentials go. The request itself still happens.
        XCTAssertEqual(followed.value(forHTTPHeaderField: "Accept"), "application/json")
        XCTAssertEqual(followed.url?.host, "collector.example.com")
    }

    /// Host comparison is case-insensitive, so `CURSOR.com` is not treated as
    /// a different host and stripped, nor a lookalike treated as the same one.
    func testHostComparisonIgnoresCase() {
        var original = URLRequest(url: URL(string: "https://cursor.com/a")!)
        original.setValue("token", forHTTPHeaderField: "Authorization")
        var proposed = original
        proposed.url = URL(string: "https://CURSOR.com/b")!

        XCTAssertEqual(BoundedHTTP.redirect(from: original, to: proposed)
            .value(forHTTPHeaderField: "Authorization"), "token")
    }

    /// The rule above only matters if URLSession actually consults the
    /// delegate `BoundedHTTP` installs per task. These two drive a real
    /// redirect through a real session to prove it does.
    func testARealCrossHostRedirectArrivesWithoutTheCredential() async throws {
        server.respond(with: Data("{}".utf8), declaringLength: true)
        server.redirectFirstRequest(to: server.loopbackAliasURL)

        var request = URLRequest(url: server.url)
        request.setValue("WorkosCursorSessionToken=abc::xyz", forHTTPHeaderField: "Cookie")
        _ = try await BoundedHTTP.data(for: request, on: ProviderSession.shared)

        let requests = server.requests
        XCTAssertEqual(requests.count, 2)
        XCTAssertTrue(try XCTUnwrap(requests.first).contains("WorkosCursorSessionToken"))
        XCTAssertFalse(try XCTUnwrap(requests.last).contains("WorkosCursorSessionToken"))
    }

    func testARealCrossPortRedirectArrivesWithoutTheCredential() async throws {
        let destination = try LocalHTTPServer()
        defer { destination.stop() }
        destination.respond(with: Data("{}".utf8), declaringLength: true)
        server.redirectFirstRequest(to: destination.url)
        var request = URLRequest(url: server.url)
        request.setValue("synthetic-cookie", forHTTPHeaderField: "Cookie")
        _ = try await BoundedHTTP.data(for: request, on: ProviderSession.shared)
        XCTAssertEqual(destination.requests.count, 1)
        XCTAssertFalse(try XCTUnwrap(destination.requests.first).contains("synthetic-cookie"))
    }

    func testCredentialsCannotCrossSchemeOrPortBoundaries() {
        var original = URLRequest(url: URL(string: "https://example.invalid/a")!)
        for header in BoundedHTTP.credentialHeaders { original.setValue("synthetic", forHTTPHeaderField: header) }
        for target in ["http://example.invalid/a", "https://example.invalid:8443/a"] {
            var proposed = original
            proposed.url = URL(string: target)!
            let redirected = BoundedHTTP.redirect(from: original, to: proposed)
            for header in BoundedHTTP.credentialHeaders {
                XCTAssertNil(redirected.value(forHTTPHeaderField: header), target)
            }
        }
        var explicitDefault = original
        explicitDefault.url = URL(string: "https://EXAMPLE.invalid:443/b")!
        XCTAssertEqual(BoundedHTTP.redirect(from: original, to: explicitDefault)
            .value(forHTTPHeaderField: "Cookie"), "synthetic")
    }

    func testARealSameHostRedirectStillCarriesTheCredential() async throws {
        server.respond(with: Data("{}".utf8), declaringLength: true)
        server.redirectFirstRequest(to: server.sameHostURL)

        var request = URLRequest(url: server.url)
        request.setValue("WorkosCursorSessionToken=abc::xyz", forHTTPHeaderField: "Cookie")
        _ = try await BoundedHTTP.data(for: request, on: ProviderSession.shared)

        let requests = server.requests
        XCTAssertEqual(requests.count, 2)
        XCTAssertTrue(try XCTUnwrap(requests.last).contains("WorkosCursorSessionToken"))
    }

    /// The session these requests run on keeps nothing on disk and shares no
    /// cookie jar with anything else — a billing response must not outlive the
    /// poll that fetched it.
    func testTheProviderSessionPersistsNothing() {
        let configuration = ProviderSession.shared.configuration
        XCTAssertNil(configuration.urlCache)
        XCTAssertNil(configuration.httpCookieStorage)
        XCTAssertNil(configuration.urlCredentialStorage)
        XCTAssertFalse(configuration.httpShouldSetCookies)
    }
}

/// A minimal loopback HTTP server, so these assertions are about real URLSession
/// behaviour rather than a stubbed protocol.
private final class LocalHTTPServer: @unchecked Sendable {
    private let listener: FileHandle
    private let port: UInt16
    private var payload = Data()
    private var declaresLength = true
    private var holdOpen = false
    private let finishBody = DispatchSemaphore(value: 0)
    private var running = true
    private let lock = NSLock()
    private var pendingRedirect: String?
    private var received: [String] = []

    var url: URL { URL(string: "http://127.0.0.1:\(port)/")! }
    /// Same server, different host *string*. URLSession treats these as
    /// different hosts, which is exactly the case the redirect rule is about.
    var loopbackAliasURL: URL { URL(string: "http://localhost:\(port)/moved")! }
    var sameHostURL: URL { URL(string: "http://127.0.0.1:\(port)/moved")! }

    /// Answer the first request with a 302 to `location`, then serve normally.
    func redirectFirstRequest(to location: URL) {
        lock.withLock { pendingRedirect = location.absoluteString }
    }

    /// The raw request lines the server saw, in order.
    var requests: [String] { lock.withLock { received } }

    init() throws {
        let socketDescriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard socketDescriptor >= 0 else { throw Failure.socket }
        var yes: Int32 = 1
        setsockopt(socketDescriptor, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))

        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        address.sin_port = 0  // let the kernel choose a free port
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(socketDescriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0, listen(socketDescriptor, 8) == 0 else { close(socketDescriptor); throw Failure.bind }

        var actual = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &actual) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(socketDescriptor, $0, &length) }
        }
        guard named == 0 else { close(socketDescriptor); throw Failure.bind }
        port = actual.sin_port.byteSwapped
        listener = FileHandle(fileDescriptor: socketDescriptor, closeOnDealloc: true)
        accept(on: socketDescriptor)
    }

    func respond(with payload: Data, declaringLength: Bool, holdOpen: Bool = false) {
        self.holdOpen = holdOpen
        self.payload = payload
        declaresLength = declaringLength
    }

    func stop() {
        finishBody.signal()
        running = false
        try? listener.close()
    }

    private func accept(on socketDescriptor: Int32) {
        Thread.detachNewThread { [self] in
            while running {
                let client = Darwin.accept(socketDescriptor, nil, nil)
                guard client >= 0 else { return }
                var request = [UInt8](repeating: 0, count: 2_048)
                let read = Darwin.read(client, &request, request.count)
                let text = String(decoding: request.prefix(max(0, read)), as: UTF8.self)
                let redirect: String? = lock.withLock {
                    received.append(text)
                    defer { pendingRedirect = nil }
                    return pendingRedirect
                }

                if let redirect {
                    let response = "HTTP/1.1 302 Found\r\nLocation: \(redirect)\r\nContent-Length: 0\r\n\r\n"
                    _ = Data(response.utf8).withUnsafeBytes {
                        Darwin.write(client, $0.baseAddress, $0.count)
                    }
                    close(client)
                    continue
                }

                var head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                if declaresLength {
                    head += "Content-Length: \(payload.count)\r\n"
                } else {
                    // No length and no chunking: the body ends when the socket does.
                    head += "Connection: close\r\n"
                }
                head += "\r\n"

                var out = Data(head.utf8)
                out.append(payload)
                out.withUnsafeBytes { buffer in
                    var sent = 0
                    while sent < buffer.count {
                        let wrote = Darwin.write(client, buffer.baseAddress!.advanced(by: sent), buffer.count - sent)
                        if wrote <= 0 { break }
                        sent += wrote
                    }
                }
                if holdOpen { _ = finishBody.wait(timeout: .now() + 5) }
                close(client)
            }
        }
    }

    enum Failure: Error { case socket, bind }
}
