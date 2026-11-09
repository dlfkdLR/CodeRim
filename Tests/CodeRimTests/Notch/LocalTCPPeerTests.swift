import Darwin
import XCTest
@testable import CodeRim

final class LocalTCPPeerTests: XCTestCase {
    private let listener = LocalTCPPeer.Address(host: "127.0.0.1", port: 63881)
    private let client = LocalTCPPeer.Address(host: "127.0.0.1", port: 49152)

    private func connected(_ local: LocalTCPPeer.Address, _ remote: LocalTCPPeer.Address) -> LocalTCPPeer.Socket {
        .init(local: local, remote: remote, state: TSI_S_ESTABLISHED)
    }

    func testOnlyTheExactReversedEstablishedTupleAuthorizesThePeer() {
        XCTAssertTrue(LocalTCPPeer.matches(listener, client: [connected(client, listener)],
                                            server: [connected(listener, client)]))
        XCTAssertFalse(LocalTCPPeer.matches(listener, client: [connected(client, listener)], server: []))
        XCTAssertFalse(LocalTCPPeer.matches(listener, client: [connected(client, listener)],
            server: [connected(listener, .init(host: client.host, port: client.port + 1))]))
        XCTAssertFalse(LocalTCPPeer.matches(listener, client: [connected(client, listener)],
            server: [.init(local: listener, remote: client, state: TSI_S_LISTEN)]))
    }

    func testSamePortOnAnotherAddressOrAnotherConnectionDoesNotAuthorize() {
        let ipv6 = LocalTCPPeer.Address(host: "::1", port: listener.port)
        XCTAssertFalse(LocalTCPPeer.matches(ipv6, client: [connected(client, listener)],
                                             server: [connected(ipv6, client)]))
        let second = LocalTCPPeer.Address(host: client.host, port: client.port + 1)
        XCTAssertFalse(LocalTCPPeer.matches(listener,
            client: [connected(client, listener), connected(second, listener)],
            server: [connected(listener, client)]))
        XCTAssertFalse(LocalTCPPeer.matches(listener, client: [], server: [connected(listener, client)]))
    }

    func testLoopbackRequiresALiteralLocalAddress() {
        for host in ["localhost", "0.0.0.0", "::", "192.168.1.1", "127.0.0.1.attacker.test"] {
            XCTAssertFalse(LocalTCPPeer.Address(host: host, port: 1).isLoopback, host)
        }
        for host in ["127.0.0.1", "127.0.0.2", "::1"] {
            XCTAssertTrue(LocalTCPPeer.Address(host: host, port: 1).isLoopback, host)
        }
    }

    func testMissingRecycledOrWrongUserProcessCannotAuthorize() throws {
        let identity = try XCTUnwrap(LocalTCPPeer.ProcessIdentity.read(getpid()))
        XCTAssertTrue(identity.isCurrent)
        XCTAssertFalse(LocalTCPPeer.ProcessIdentity(pid: identity.pid, userID: identity.userID,
            startedSeconds: identity.startedSeconds + 1, startedMicroseconds: identity.startedMicroseconds).isCurrent)
        XCTAssertFalse(LocalTCPPeer.ProcessIdentity(pid: identity.pid, userID: identity.userID + 1,
            startedSeconds: identity.startedSeconds, startedMicroseconds: identity.startedMicroseconds).isCurrent)
        XCTAssertNil(LocalTCPPeer.ProcessIdentity.read(-1))
        XCTAssertFalse(LocalTCPPeer.verify(listener, process: identity))
    }

    func testKernelInventoryBindsARealConnectedSocket() throws {
        let server = socket(AF_INET, SOCK_STREAM, 0)
        XCTAssertGreaterThanOrEqual(server, 0)
        defer { close(server) }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        XCTAssertEqual(withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(server, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }, 0)
        XCTAssertEqual(listen(server, 1), 0)
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        XCTAssertEqual(withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(server, $0, &length) }
        }, 0)
        let endpoint = LocalTCPPeer.Address(host: "127.0.0.1", port: Int(UInt16(bigEndian: address.sin_port)))
        let identity = try XCTUnwrap(LocalTCPPeer.ProcessIdentity.read(getpid()))
        XCTAssertTrue(LocalTCPPeer.listeners(of: identity).contains(endpoint))
        let outgoing = socket(AF_INET, SOCK_STREAM, 0)
        XCTAssertGreaterThanOrEqual(outgoing, 0)
        defer { close(outgoing) }
        XCTAssertEqual(withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(outgoing, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }, 0)
        let incoming = accept(server, nil, nil)
        XCTAssertGreaterThanOrEqual(incoming, 0)
        XCTAssertTrue(LocalTCPPeer.verify(endpoint, process: identity))
        close(incoming)
        XCTAssertFalse(LocalTCPPeer.verify(endpoint, process: identity), "a stale listener alone is insufficient")
    }
}
