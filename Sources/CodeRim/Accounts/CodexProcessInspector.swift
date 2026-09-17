import Darwin
import Foundation

struct CodexProcessMetadata: Equatable {
    let userID: uid_t
    let status: UInt32
    let command: String
}

/// Device and inode of the active login file, so a concurrent holder is matched
/// by identity rather than by a path that a rename could have moved.
struct CodexFileIdentity: Hashable {
    let device: UInt64
    let inode: UInt64
}

protocol CodexProcessInspecting {
    func processIDs() throws -> [pid_t]
    func metadata(for pid: pid_t) -> CodexProcessMetadata?
    func executablePath(for pid: pid_t) -> String?
    func isAlive(_ pid: pid_t) -> Bool
    /// Whether the process currently has the identified regular file open.
    /// `nil` means the open files could not be read.
    func holdsFile(_ pid: pid_t, identity: CodexFileIdentity) -> Bool?
}

/// Reads process identity only: never arguments, environment, or credentials.
struct SystemCodexProcessInspector: CodexProcessInspecting {
    func processIDs() throws -> [pid_t] {
        let count = proc_listallpids(nil, 0)
        guard count > 0 else { throw AccountSwitchError.processInspectionFailed }
        var pids = [pid_t](repeating: 0, count: Int(count) + 1024)
        let actual = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size))
        guard actual > 0, actual < pids.count else { throw AccountSwitchError.processInspectionFailed }
        return Array(pids.prefix(Int(actual)).filter { $0 > 0 })
    }

    func metadata(for pid: pid_t) -> CodexProcessMetadata? {
        var info = proc_bsdinfo()
        if proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, Int32(MemoryLayout<proc_bsdinfo>.size)) == MemoryLayout<proc_bsdinfo>.size {
            let name = withUnsafeBytes(of: info.pbi_name) { Self.command(in: $0) }
            let fallback = withUnsafeBytes(of: info.pbi_comm) { Self.command(in: $0) }
            return CodexProcessMetadata(userID: info.pbi_uid, status: info.pbi_status,
                                        command: name.isEmpty ? fallback : name)
        }
        // macOS can deny PROC_PIDTBSDINFO for a root-owned terminal login even
        // when kill(pid, 0) succeeds. The kernel process table still identifies
        // its owner and command; signal permission alone does not identify Codex.
        return kernelMetadata(for: pid)
    }

    func kernelMetadata(for pid: pid_t) -> CodexProcessMetadata? {
        var query: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.size
        guard sysctl(&query, u_int(query.count), &info, &size, nil, 0) == 0,
              size == MemoryLayout<kinfo_proc>.size, info.kp_proc.p_pid == pid else { return nil }
        let command = withUnsafeBytes(of: info.kp_proc.p_comm) { Self.command(in: $0) }
        return CodexProcessMetadata(userID: info.kp_eproc.e_ucred.cr_uid,
                                    status: UInt32(info.kp_proc.p_stat), command: command)
    }

    func executablePath(for pid: pid_t) -> String? {
        var bytes = [UInt8](repeating: 0, count: 4 * Int(MAXPATHLEN))
        guard proc_pidpath(pid, &bytes, UInt32(bytes.count)) > 0,
              let path = String(bytes: bytes.prefix(while: { $0 != 0 }), encoding: .utf8),
              path.hasPrefix("/") else { return nil }
        return path
    }

    func isAlive(_ pid: pid_t) -> Bool {
        if kill(pid, 0) == 0 { return true }
        return errno != ESRCH // A permission error is not proof of exit.
    }

    func holdsFile(_ pid: pid_t, identity: CodexFileIdentity) -> Bool? {
        let listBytes = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, nil, 0)
        guard listBytes > 0 else { return nil }
        let stride = MemoryLayout<proc_fdinfo>.stride
        let capacity = Int(listBytes) / stride + 32
        var fds = [proc_fdinfo](repeating: proc_fdinfo(), count: capacity)
        let filled = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, &fds, Int32(capacity * stride))
        guard filled > 0 else { return nil }
        let entries = min(capacity, Int(filled) / stride)
        for index in 0..<entries where fds[index].proc_fdtype == UInt32(PROX_FDTYPE_VNODE) {
            var info = vnode_fdinfowithpath()
            let read = proc_pidfdinfo(pid, fds[index].proc_fd, PROC_PIDFDVNODEPATHINFO,
                                     &info, Int32(MemoryLayout<vnode_fdinfowithpath>.size))
            guard read == Int32(MemoryLayout<vnode_fdinfowithpath>.size) else { continue }
            let stat = info.pvip.vip_vi.vi_stat
            if UInt64(stat.vst_dev) == identity.device, stat.vst_ino == identity.inode { return true }
        }
        return false
    }

    private static func command(in bytes: UnsafeRawBufferPointer) -> String {
        String(bytes: bytes.prefix(while: { $0 != 0 }), encoding: .utf8) ?? ""
    }
}

enum CodexProcessGate {
    static var defaultAuthFile: URL {
        CodexLoginFile.defaultDirectory.appendingPathComponent("auth.json", isDirectory: false)
    }

    /// A running Codex client only blocks a switch while it actually holds the
    /// login file open. An idle background app-server, a remote session, or an
    /// editor extension that has already released the file is not a blocker;
    /// `CodexLoginFile.replace` still refuses to overwrite a login that changed.
    static func requireStopped(using inspector: any CodexProcessInspecting = SystemCodexProcessInspector(),
                               userID: uid_t = getuid(),
                               authFile: URL = defaultAuthFile) throws {
        let candidates = try runningCodexPIDs(using: inspector, userID: userID)
        guard !candidates.isEmpty, let identity = fileIdentity(of: authFile) else { return }
        for pid in candidates {
            switch inspector.holdsFile(pid, identity: identity) {
            case .some(true):
                throw AccountSwitchError.codexRunning
            case .some(false):
                continue
            case .none:
                if inspector.isAlive(pid) { throw AccountSwitchError.processInspectionFailed }
            }
        }
    }

    static func runningCodexPIDs(using inspector: any CodexProcessInspecting = SystemCodexProcessInspector(),
                                userID: uid_t = getuid()) throws -> [pid_t] {
        var running: [pid_t] = []
        for pid in try inspector.processIDs() where pid > 0 {
            guard let info = inspector.metadata(for: pid) else {
                if inspector.isAlive(pid) { throw AccountSwitchError.processInspectionFailed }
                continue
            }
            guard info.userID == userID, info.status != UInt32(SZOMB) else { continue }
            if let path = inspector.executablePath(for: pid) {
                if CodexProcessIdentity.isCodexExecutable(path: path) { running.append(pid) }
                continue
            }
            // Updated/deleted helper binaries may have no resolvable path. Read
            // identity again in case the process exited or exec'd during lookup.
            guard let current = inspector.metadata(for: pid) else {
                if inspector.isAlive(pid) { throw AccountSwitchError.processInspectionFailed }
                continue
            }
            guard current.userID == userID, current.status != UInt32(SZOMB), inspector.isAlive(pid) else { continue }
            guard !current.command.isEmpty else { throw AccountSwitchError.processInspectionFailed }
            // Kernel command names can be truncated. An unresolved codex-* name
            // remains a blocker; unrelated widgets/browser helpers do not.
            if current.command == "codex" || current.command.hasPrefix("codex-") { running.append(pid) }
        }
        return running
    }

    private static func fileIdentity(of url: URL) -> CodexFileIdentity? {
        var info = stat()
        guard stat(url.path, &info) == 0, info.st_mode & S_IFMT == S_IFREG else { return nil }
        return CodexFileIdentity(device: UInt64(UInt32(bitPattern: info.st_dev)), inode: info.st_ino)
    }
}
