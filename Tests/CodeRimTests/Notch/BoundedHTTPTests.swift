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

    /// A body exactly at the ceiling is legitimate and must not be refused.
    func testABodyAtTheCeilingIsAccepted() async throws {
        let payload = Data(repeating: 0x61, count: 4_096)
        server.respond(with: payload, declaringLength: true)

        let (data, _) = try await BoundedHTTP.data(
            for: URLRequest(url: server.url), on: .shared, maximumBytes: 4_096
        )
        XCTAssertEqual(data.count, 4_096)
    }
}

/// A minimal loopback HTTP server, so these assertions are about real URLSession
/// behaviour rather than a stubbed protocol.
private final class LocalHTTPServer: @unchecked Sendable {
    private let listener: FileHandle
    private let port: UInt16
    private var payload = Data()
    private var declaresLength = true
    private var running = true

    var url: URL { URL(string: "http://127.0.0.1:\(port)/")! }

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

    func respond(with payload: Data, declaringLength: Bool) {
        self.payload = payload
        declaresLength = declaringLength
    }

    func stop() {
        running = false
        try? listener.close()
    }

    private func accept(on socketDescriptor: Int32) {
        Thread.detachNewThread { [self] in
            while running {
                let client = Darwin.accept(socketDescriptor, nil, nil)
                guard client >= 0 else { return }
                var request = [UInt8](repeating: 0, count: 2_048)
                _ = Darwin.read(client, &request, request.count)

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
                close(client)
            }
        }
    }

    enum Failure: Error { case socket, bind }
}
