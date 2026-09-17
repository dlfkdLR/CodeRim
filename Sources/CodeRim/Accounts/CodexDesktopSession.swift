import AppKit
import Darwin
import Foundation
import Security

enum CodexProcessIdentity {
    static func isCodexExecutable(path: String) -> Bool {
        let name = URL(fileURLWithPath: path).lastPathComponent
        return name == "codex" || (name.hasPrefix("codex-") && name.hasSuffix("-apple-darwin"))
    }
}

/// Resolves only the running, vendor-signed desktop and its direct app-server.
/// The process environment is bounded and never returned or logged; only CODEX_HOME
/// is projected so a shell-only custom home cannot silently switch the wrong login.
struct CodexDesktopSession: Sendable {
    let bundle: URL
    let executable: URL

    @MainActor
    static func running() async throws -> CodexDesktopSession {
        let candidates = NSWorkspace.shared.runningApplications.compactMap { app -> (URL, pid_t)? in
            guard let url = app.bundleURL,
                  ["ChatGPT.app", "Codex.app"].contains(url.lastPathComponent),
                  url.deletingLastPathComponent().path == "/Applications" else { return nil }
            return (url, app.processIdentifier)
        }
        guard candidates.count == 1, let (bundle, pid) = candidates.first else {
            throw AccountSwitchError.openCodexFirst
        }
        return try await Task.detached {
            let executable = bundle.appendingPathComponent("Contents/Resources/codex")
            let session = CodexDesktopSession(bundle: bundle, executable: executable)
            try session.verifySignature()
            let child = try appServerChild(of: pid, executable: executable)
            let declaredHome = try homeOfProcess(child)
            guard let declaredHome, declaredHome.hasPrefix("/"),
                  URL(fileURLWithPath: declaredHome).standardizedFileURL == CodexLoginFile.defaultDirectory.standardizedFileURL else {
                throw AccountSwitchError.unsupportedStorage
            }
            guard try appServerChild(of: pid, executable: executable) == child else { throw AccountSwitchError.openCodexFirst }
            return session
        }.value
    }

    func verifySignature() throws {
        let requirementText = #"anchor apple generic and certificate leaf[subject.OU] = "2DC432GLL2""#
        var requirement: SecRequirement?
        guard SecRequirementCreateWithString(requirementText as CFString, [], &requirement) == errSecSuccess,
              let requirement else { throw AccountSwitchError.unavailable }
        for url in [bundle, executable] {
            var code: SecStaticCode?
            guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code,
                  SecStaticCodeCheckValidityWithErrors(code, SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate), requirement, nil) == errSecSuccess
            else { throw AccountSwitchError.unavailable }
        }
    }

    private static func appServerChild(of pid: pid_t, executable: URL) throws -> pid_t {
        let count = proc_listallpids(nil, 0)
        guard count > 0 else { throw AccountSwitchError.openCodexFirst }
        var pids = [pid_t](repeating: 0, count: Int(count) + 1024)
        let capacity = Int32(pids.count * MemoryLayout<pid_t>.size)
        let actual = proc_listallpids(&pids, capacity)
        guard actual > 0, actual < pids.count else { throw AccountSwitchError.openCodexFirst }
        var children: [pid_t] = []
        for child in pids.prefix(Int(actual)) where child > 0 {
            var info = proc_bsdinfo()
            guard proc_pidinfo(child, PROC_PIDTBSDINFO, 0, &info, Int32(MemoryLayout<proc_bsdinfo>.size)) == MemoryLayout<proc_bsdinfo>.size,
                  info.pbi_ppid == UInt32(pid), info.pbi_uid == getuid() else { continue }
            var path = [UInt8](repeating: 0, count: 4 * Int(MAXPATHLEN))
            guard proc_pidpath(child, &path, UInt32(path.count)) > 0 else { continue }
            let url = URL(fileURLWithPath: String(decoding: path.prefix(while: { $0 != 0 }), as: UTF8.self))
            if url.standardizedFileURL == executable.standardizedFileURL { children.append(child) }
        }
        guard children.count == 1, let child = children.first else { throw AccountSwitchError.openCodexFirst }
        return child
    }

    private static func homeOfProcess(_ pid: pid_t) throws -> String? {
        var query: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&query, u_int(query.count), nil, &size, nil, 0) == 0,
              size >= MemoryLayout<Int32>.size, size <= 1_048_576 else { throw AccountSwitchError.openCodexFirst }
        var bytes = [UInt8](repeating: 0, count: size)
        guard sysctl(&query, u_int(query.count), &bytes, &size, nil, 0) == 0 else { throw AccountSwitchError.openCodexFirst }
        defer { _ = bytes.withUnsafeMutableBytes { $0.initializeMemory(as: UInt8.self, repeating: 0) } }
        return try projectedHome(from: Data(bytes.prefix(size)))
    }

    static func projectedHome(from data: Data) throws -> String? {
        guard data.count >= MemoryLayout<Int32>.size else { throw AccountSwitchError.openCodexFirst }
        let count = data.withUnsafeBytes { $0.loadUnaligned(as: Int32.self) }
        guard count >= 1, count <= 16_384 else { throw AccountSwitchError.openCodexFirst }
        var index = MemoryLayout<Int32>.size
        func readCString() throws -> Data {
            let start = index
            while index < data.count, data[index] != 0 { index += 1 }
            guard index < data.count else { throw AccountSwitchError.openCodexFirst }
            defer { index += 1 }
            return data.subdata(in: start..<index)
        }
        _ = try readCString() // Executable path, followed by alignment padding.
        while index < data.count, data[index] == 0 { index += 1 }
        for _ in 0..<count { _ = try readCString() }
        let prefix = Data("CODEX_HOME=".utf8)
        var codexHome: String?
        var userHome: String?
        while index < data.count {
            let value = try readCString()
            if value.starts(with: prefix) {
                guard codexHome == nil, value.count <= 4_096,
                      let decoded = String(data: value.dropFirst(prefix.count), encoding: .utf8) else {
                    throw AccountSwitchError.unsupportedStorage
                }
                codexHome = decoded
            } else if value.starts(with: Data("HOME=".utf8)) {
                guard userHome == nil, value.count <= 4_096,
                      let decoded = String(data: value.dropFirst(5), encoding: .utf8) else {
                    throw AccountSwitchError.unsupportedStorage
                }
                userHome = decoded
            }
        }
        if let codexHome, !codexHome.isEmpty { return codexHome }
        guard let userHome, userHome.hasPrefix("/") else { throw AccountSwitchError.unsupportedStorage }
        return URL(fileURLWithPath: userHome).appendingPathComponent(".codex").path
    }
}
