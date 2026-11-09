import Darwin
import Foundation

/// Kernel identities, not a loopback certificate or a port number, authorize
/// forwarding the language server's token.
enum LocalTCPPeer {
    struct ProcessIdentity: Equatable, Sendable {
        let pid: pid_t
        let userID: uid_t
        let startedSeconds: UInt64
        let startedMicroseconds: UInt64

        static func read(_ pid: pid_t) -> Self? {
            var info = proc_bsdinfo()
            guard pid > 0,
                  proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info,
                               Int32(MemoryLayout<proc_bsdinfo>.size)) == MemoryLayout<proc_bsdinfo>.size,
                  info.pbi_pid == UInt32(pid), info.pbi_uid == getuid(),
                  info.pbi_ruid == getuid(), info.pbi_status != UInt32(SZOMB) else { return nil }
            return Self(pid: pid, userID: info.pbi_uid, startedSeconds: info.pbi_start_tvsec,
                        startedMicroseconds: info.pbi_start_tvusec)
        }

        var isCurrent: Bool { Self.read(pid) == self }
    }

    struct Address: Equatable, Hashable, Sendable {
        let host: String
        let port: Int

        var isLoopback: Bool {
            var ipv4 = in_addr()
            if inet_pton(AF_INET, host, &ipv4) == 1 {
                return UInt32(bigEndian: ipv4.s_addr) >> 24 == 127
            }
            var ipv6 = in6_addr()
            guard inet_pton(AF_INET6, host, &ipv6) == 1 else { return false }
            return withUnsafeBytes(of: ipv6) { Array($0) == Array(repeating: 0, count: 15) + [1] }
        }

        var authority: String { host.contains(":") ? "[\(host)]:\(port)" : "\(host):\(port)" }
    }

    struct Socket: Equatable, Sendable {
        let local: Address
        let remote: Address
        let state: Int32
    }

    static func listeners(of process: ProcessIdentity) -> [Address] {
        guard process.isCurrent, let sockets = sockets(of: process.pid), process.isCurrent else { return [] }
        return Array(Set(sockets.filter { $0.state == TSI_S_LISTEN && $0.local.isLoopback }
            .map(\.local))).sorted { $0.authority < $1.authority }
    }

    static func verify(_ listener: Address, process: ProcessIdentity) -> Bool {
        guard process.isCurrent,
              let client = sockets(of: getpid()), let server = sockets(of: process.pid),
              process.isCurrent else { return false }
        return matches(listener, client: client, server: server)
    }

    /// A fresh session has one connection to this exact endpoint. Refuse an
    /// ambiguous inventory rather than authorize a different concurrent socket.
    static func matches(_ listener: Address, client: [Socket], server: [Socket]) -> Bool {
        guard listener.isLoopback, (1...65535).contains(listener.port) else { return false }
        let outgoing = client.filter {
            $0.state == TSI_S_ESTABLISHED && $0.remote == listener && $0.local.isLoopback
        }
        guard outgoing.count == 1, let connection = outgoing.first else { return false }
        return server.filter {
            $0.state == TSI_S_ESTABLISHED && $0.local == listener && $0.remote == connection.local
        }.count == 1
    }

    static func sockets(of pid: pid_t) -> [Socket]? {
        let stride = MemoryLayout<proc_fdinfo>.stride
        let bytes = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, nil, 0)
        guard bytes >= 0, bytes % Int32(stride) == 0 else { return nil }
        let capacity = Int(bytes) / stride + 64
        guard capacity <= 65536 else { return nil }
        var fds = [proc_fdinfo](repeating: proc_fdinfo(), count: capacity)
        let filled = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, &fds, Int32(capacity * stride))
        guard filled > 0, filled < capacity * stride, filled % Int32(stride) == 0 else { return nil }
        var result: [Socket] = []
        for fd in fds.prefix(Int(filled) / stride) where fd.proc_fdtype == UInt32(PROX_FDTYPE_SOCKET) {
            var info = socket_fdinfo()
            // Any unreadable socket makes uniqueness unprovable; it could be
            // the challenged connection rather than an unrelated descriptor.
            guard proc_pidfdinfo(pid, fd.proc_fd, PROC_PIDFDSOCKETINFO, &info,
                                 Int32(MemoryLayout<socket_fdinfo>.size)) == MemoryLayout<socket_fdinfo>.size else { return nil }
            guard info.psi.soi_kind == SOCKINFO_TCP else { continue }
            let tcp = info.psi.soi_proto.pri_tcp
            let ip = tcp.tcpsi_ini
            let family = ip.insi_vflag & UInt8(INI_IPV4) != 0 ? AF_INET : AF_INET6
            var local = ip.insi_laddr
            var remote = ip.insi_faddr
            let localHost = family == AF_INET
                ? host(&local.ina_46.i46a_addr4, family: family) : host(&local.ina_6, family: family)
            let remoteHost = family == AF_INET
                ? host(&remote.ina_46.i46a_addr4, family: family) : host(&remote.ina_6, family: family)
            guard let localHost, let remoteHost else { continue }
            result.append(Socket(local: Address(host: localHost, port: Int(UInt16(bigEndian: UInt16(truncatingIfNeeded: ip.insi_lport)))),
                                 remote: Address(host: remoteHost, port: Int(UInt16(bigEndian: UInt16(truncatingIfNeeded: ip.insi_fport)))),
                                 state: tcp.tcpsi_state))
        }
        return result
    }
    private static func host<T>(_ address: inout T, family: Int32) -> String? {
        withUnsafePointer(to: &address) { pointer in
            var text = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
            guard inet_ntop(family, pointer, &text, socklen_t(text.count)) != nil else { return nil }
            return String(decoding: text.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }, as: UTF8.self)
        }
    }

}
