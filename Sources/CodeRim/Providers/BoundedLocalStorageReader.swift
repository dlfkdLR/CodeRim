import Foundation
import CryptoKit
import Darwin

/// A current-state reader, never a LevelDB recovery engine or a search of historical token bytes.
/// Only canonical unpartitioned HTTPS keys from a stable, bounded snapshot are returned.
enum LocalStorageReadError: Error, Equatable {
    case missing, invalid, unsupported, limit, changed
}

enum BoundedLocalStorageReader {
    static func read(directory: URL, origin: String, keys: [String],
                     afterCapture: (() throws -> Void)? = nil) throws -> [String: String] {
        let reader = LocalStorageLevelDB()
        try reader.check()
        guard let url = URL(string: origin), url.scheme == "https", url.user == nil, url.password == nil,
              url.port == nil || url.port == 443, let host = url.host, !host.isEmpty,
              origin == "https://" + host, host.utf8.allSatisfy({ $0 > 32 && $0 < 127 }),
              !keys.isEmpty, keys.count <= 64, Set(keys).count == keys.count,
              keys.allSatisfy({ !$0.isEmpty && $0.utf8.count <= 256 && $0.utf8.allSatisfy({ (32...126).contains($0) }) })
        else { throw LocalStorageReadError.unsupported }
        let root = try DirectoryLease(directory)
        defer { root.close() }
        let initial = try root.inventory()
        var snapshots: [String: [UInt8]] = [:]
        var files: [String: FileLease] = [:]
        defer { for file in files.values { file.close() } }
        var total = 0
        func capture(_ name: String, maximum: Int) throws -> [UInt8] {
            try reader.check()
            guard initial.contains(name) else { throw LocalStorageReadError.missing }
            let file = try root.file(name)
            files[name] = file
            let bytes = try file.read(maximum: maximum, check: reader.check)
            total += bytes.count
            try reader.limit(total <= LocalStorageLevelDB.maximumBytes)
            snapshots[name] = bytes
            return bytes
        }
        let current = try capture("CURRENT", maximum: 128)
        guard current.count >= 11, current.last == 10,
              current.dropLast().allSatisfy({ (32...126).contains($0) }),
              let manifest = String(bytes: current.dropLast(), encoding: .ascii),
              manifest.hasPrefix("MANIFEST-"), number(String(manifest.dropFirst(9))) != nil
        else { throw LocalStorageReadError.invalid }
        let version = try reader.manifest(capture(manifest, maximum: 8 * 1024 * 1024))
        var tables: [UInt64: [UInt8]] = [:]
        for table in version.tables.keys {
            let names = initial.filter { tableNumber($0) == table }
            guard names.count == 1, let name = names.first else { throw LocalStorageReadError.invalid }
            tables[table] = try capture(name, maximum: LocalStorageLevelDB.maximumFile)
        }
        var eligible: [UInt64: String] = [:]
        for name in initial where name.hasSuffix(".log") {
            guard let n = number(String(name.dropLast(4))), n >= version.log || n == version.previousLog else { continue }
            guard eligible.updateValue(name, forKey: n) == nil else { throw LocalStorageReadError.invalid }
        }
        guard !eligible.isEmpty else { throw LocalStorageReadError.missing }
        try reader.limit(eligible.count + tables.count + 2 <= 128)
        let logs = try eligible.keys.sorted().map { try capture(eligible[$0]!, maximum: LocalStorageLevelDB.maximumFile) }
        try afterCapture?()
        let prefix = Array(("_" + origin + "\0").utf8)
        func encoded(_ key: String, latin: Bool) -> [UInt8] {
            prefix + [latin ? 1 : 0] + (latin ? Array(key.utf8) : key.utf16.flatMap { [UInt8($0 & 255), UInt8($0 >> 8)] })
        }
        let versionKey = Array("VERSION".utf8)
        let values = try reader.read(version: version, tables: tables, logs: logs,
            keys: [versionKey] + keys.map { encoded($0, latin: true) },
            unsupportedKeys: keys.map { encoded($0, latin: false) })
        guard values[versionKey] == Array("1".utf8) else { throw LocalStorageReadError.unsupported }
        var result: [String: String] = [:], returned = 0
        for key in keys {
            guard let bytes = values[encoded(key, latin: true)] else { continue }
            returned += bytes.count
            try reader.limit(returned <= 65536)
            guard let marker = bytes.first else { throw LocalStorageReadError.invalid }
            if marker == 1 {
                result[key] = String(String.UnicodeScalarView(bytes.dropFirst().map { UnicodeScalar($0) }))
            } else if marker == 0, bytes.count % 2 == 1,
                      let text = String(data: Data(bytes.dropFirst()), encoding: .utf16LittleEndian) {
                result[key] = text
            } else { throw LocalStorageReadError.invalid }
        }
        guard initial == (try root.inventory()) else { throw LocalStorageReadError.changed }
        for (name, bytes) in snapshots {
            try reader.check()
            guard let original = files[name] else { throw LocalStorageReadError.changed }
            try original.assertUnchanged()
            let file = try root.file(name)
            defer { file.close() }
            guard file.identity == original.identity else { throw LocalStorageReadError.changed }
            let verification = try file.read(maximum: bytes.count, check: reader.check)
            guard SHA256.hash(data: verification) == SHA256.hash(data: bytes) else { throw LocalStorageReadError.changed }
        }
        try root.assertUnchanged()
        try reader.check()
        return result
    }
    static func profileDirectories(root: URL) throws -> [URL] {
        try Task.checkCancellation()
        let lease = try DirectoryLease(root); defer { lease.close() }
        let names = try lease.inventory().filter { $0 == "Default" || $0.hasPrefix("Profile ") || $0.hasPrefix("user-") }.sorted()
        guard names.count <= 64 else { throw LocalStorageReadError.limit }
        var result: [URL] = []
        for name in names {
            let url = root.appendingPathComponent(name)
            let profile = try DirectoryLease(url); profile.close(); result.append(url)
        }
        try lease.assertUnchanged(); return result
    }
    /// Runs a decoder against private bytes while retaining/revalidating every original file descriptor.
    /// Optional WAL/SHM absence is part of the snapshot; newly appearing files invalidate the read.
    static func withFiles<T>(directory: URL, required: [String], optional: [String] = [], maximum: Int,
                             body: ([String: Data]) throws -> T) throws -> T {
        let reader = LocalStorageLevelDB(), root = try DirectoryLease(directory)
        defer { root.close() }
        let names = try root.inventory()
        var files: [String: FileLease] = [:], bytes: [String: Data] = [:], total = 0
        defer { for file in files.values { file.close() } }
        for name in required + optional {
            try reader.check()
            if !names.contains(name) { if required.contains(name) { throw LocalStorageReadError.missing }; continue }
            let file = try root.file(name); files[name] = file
            let content = Data(try file.read(maximum: maximum, check: reader.check))
            total += content.count; try reader.limit(total <= maximum); bytes[name] = content
        }
        let result = try body(bytes)
        guard try root.inventory() == names else { throw LocalStorageReadError.changed }
        for (name, file) in files {
            try file.assertUnchanged()
            let fresh = try root.file(name); defer { fresh.close() }
            guard fresh.identity == file.identity, let original = bytes[name],
                  SHA256.hash(data: try fresh.read(maximum: original.count, check: reader.check)) == SHA256.hash(data: original)
            else { throw LocalStorageReadError.changed }
        }
        try root.assertUnchanged(); try reader.check(); return result
    }
    private static func number(_ text: String) -> UInt64? {
        guard (1...20).contains(text.utf8.count), text.utf8.allSatisfy({ (48...57).contains($0) }),
              let value = UInt64(text), value > 0 else { return nil }; return value
    }
    private static func tableNumber(_ name: String) -> UInt64? {
        (name.hasSuffix(".ldb") || name.hasSuffix(".sst")) ? number(String(name.dropLast(4))) : nil
    }

    private struct Identity: Equatable {
        let device: dev_t, inode: ino_t, size: off_t, modified: timespec, changed: timespec
        init(_ value: stat) { device = value.st_dev; inode = value.st_ino; size = value.st_size; modified = value.st_mtimespec; changed = value.st_ctimespec }
        static func == (a: Self, b: Self) -> Bool {
            a.device == b.device && a.inode == b.inode && a.size == b.size
                && a.modified.tv_sec == b.modified.tv_sec && a.modified.tv_nsec == b.modified.tv_nsec
                && a.changed.tv_sec == b.changed.tv_sec && a.changed.tv_nsec == b.changed.tv_nsec
        }
    }
    private class FileLease {
        let fd: Int32, identity: Identity
        init(_ fd: Int32, directory: Bool = false) throws {
            self.fd = fd
            var info = stat()
            guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == (directory ? S_IFDIR : S_IFREG), info.st_size >= 0 else {
                Darwin.close(fd); throw LocalStorageReadError.unsupported
            }
            var filesystem = statfs()
            guard fstatfs(fd, &filesystem) == 0, filesystem.f_flags & UInt32(MNT_LOCAL) != 0 else {
                Darwin.close(fd); throw LocalStorageReadError.unsupported
            }
            identity = Identity(info)
        }
        func close() { Darwin.close(fd) }
        func assertUnchanged() throws {
            var now = stat()
            guard fstat(fd, &now) == 0, Identity(now) == identity else { throw LocalStorageReadError.changed }
        }
        func read(maximum: Int, check: () throws -> Void) throws -> [UInt8] {
            guard identity.size <= maximum else { throw LocalStorageReadError.limit }
            var bytes = [UInt8](repeating: 0, count: Int(identity.size)), at = 0
            while at < bytes.count {
                try check()
                let amount = min(16384, bytes.count - at)
                let count = bytes.withUnsafeMutableBytes { Darwin.pread(fd, $0.baseAddress!.advanced(by: at), amount, off_t(at)) }
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw LocalStorageReadError.changed }; at += count
            }
            var tail: UInt8 = 0
            guard pread(fd, &tail, 1, off_t(at)) == 0 else { throw LocalStorageReadError.changed }
            try assertUnchanged(); try check(); return bytes
        }
    }
    private final class DirectoryLease: FileLease {
        let url: URL
        init(_ url: URL) throws {
            guard url.isFileURL, url.path.hasPrefix("/"), !url.path.contains("\0") else { throw LocalStorageReadError.unsupported }
            self.url = url
            var descriptor = Darwin.open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC)
            guard descriptor >= 0 else { throw LocalStorageReadError.missing }
            for part in url.path.split(separator: "/") {
                guard part != ".", part != ".." else { Darwin.close(descriptor); throw LocalStorageReadError.unsupported }
                let next = openat(descriptor, String(part), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                Darwin.close(descriptor)
                guard next >= 0 else { throw errno == ENOENT ? LocalStorageReadError.missing : LocalStorageReadError.unsupported }
                descriptor = next
            }
            try super.init(descriptor, directory: true)
        }
        func inventory() throws -> Set<String> {
            // Enumerate through the held directory descriptor, not a replaceable absolute path.
            let copy = dup(fd)
            guard copy >= 0 else { throw LocalStorageReadError.changed }
            guard let dir = fdopendir(copy) else { Darwin.close(copy); throw LocalStorageReadError.changed }
            defer { closedir(dir) }; rewinddir(dir)
            var names = Set<String>()
            while true {
                errno = 0
                guard let entry = readdir(dir) else {
                    guard errno == 0 else { throw LocalStorageReadError.changed }; break
                }
                let name = withUnsafePointer(to: &entry.pointee.d_name) { ptr in
                    ptr.withMemoryRebound(to: CChar.self, capacity: Int(entry.pointee.d_namlen) + 1) { String(validatingCString: $0) }
                }
                guard let name else { throw LocalStorageReadError.unsupported }
                if name == "." || name == ".." { continue }
                guard names.insert(name).inserted else { throw LocalStorageReadError.invalid }
                guard names.count <= 512 else { throw LocalStorageReadError.limit }
            }
            return names
        }
        func file(_ name: String) throws -> FileLease {
            guard !name.contains("/"), !name.contains("\0"), name != ".", name != ".." else { throw LocalStorageReadError.invalid }
            let file = openat(fd, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
            guard file >= 0 else { throw errno == ENOENT ? LocalStorageReadError.missing : LocalStorageReadError.unsupported }
            return try FileLease(file)
        }
        override func assertUnchanged() throws {
            try super.assertUnchanged()
            let current = try DirectoryLease(url); defer { current.close() }
            guard current.identity == identity else { throw LocalStorageReadError.changed }
        }
    }
}

private final class LocalStorageLevelDB {
    static let maximumFile = 32 * 1024 * 1024, maximumBytes = 64 * 1024 * 1024
    static let maximumSequence = (UInt64(1) << 56) - 1
    let started = ContinuousClock.now
    var expanded = 0, records = 0
    struct Table { let level: Int; let number, size: UInt64; let smallest, largest: [UInt8] }
    struct Version { let log, previousLog, sequence: UInt64; let tables: [UInt64: Table] }
    struct Value { let sequence: UInt64; let type: UInt8; let bytes: [UInt8] }
    var wanted = Set<[UInt8]>(), unsupported = Set<[UInt8]>(), values: [[UInt8]: Value] = [:]
    func check() throws { try Task.checkCancellation(); try limit(started.duration(to: .now) < .seconds(5)) }
    func limit(_ condition: Bool) throws { if !condition { throw LocalStorageReadError.limit } }
    func record() throws { try check(); records += 1; try limit(records <= 200000) }
    func expand(_ size: Int) throws { expanded += size; try limit(expanded <= Self.maximumBytes) }
    static let crcTable: [UInt32] = (0..<256).map { n in
        var c = UInt32(n)
        for _ in 0..<8 { c = (c >> 1) ^ (c & 1 == 0 ? 0 : 0x82f63b78) }; return c
    }
    func crc(_ bytes: [UInt8], type: UInt8, first: Bool) throws -> UInt32 {
        var value = UInt32.max
        func byte(_ b: UInt8) { value = Self.crcTable[Int((value ^ UInt32(b)) & 255)] ^ (value >> 8) }
        if first { byte(type) }
        for (i, b) in bytes.enumerated() { if i & 16383 == 0 { try check() }; byte(b) }
        if !first { byte(type) }; value = ~value
        return ((value >> 15) | (value << 17)) &+ 0xa282ead8
    }
    final class Cursor {
        let bytes: [UInt8]; var at = 0
        init(_ bytes: [UInt8]) { self.bytes = bytes }
        var end: Bool { at == bytes.count }
        func byte() throws -> UInt8 { guard at < bytes.count else { throw LocalStorageReadError.invalid }; defer { at += 1 }; return bytes[at] }
        func varint() throws -> UInt64 {
            var value: UInt64 = 0
            for i in 0..<10 {
                let b = try byte()
                guard i != 9 || b <= 1 else { throw LocalStorageReadError.invalid }
                value |= UInt64(b & 127) << (i * 7)
                if b < 128 { guard i == 0 || b != 0 else { throw LocalStorageReadError.invalid }; return value }
            }; throw LocalStorageReadError.invalid
        }
        func length(_ maximum: Int) throws -> Int { let v = try varint(); guard v <= maximum else { throw LocalStorageReadError.limit }; return Int(v) }
        func take(_ count: Int) throws -> [UInt8] {
            guard count >= 0, count <= bytes.count - at else { throw LocalStorageReadError.invalid }
            defer { at += count }; return Array(bytes[at..<at + count])
        }
        func slice(_ maximum: Int) throws -> [UInt8] { try take(length(maximum)) }
    }
    static func little(_ bytes: [UInt8], _ at: Int, _ count: Int) throws -> UInt64 {
        guard at >= 0, count <= bytes.count - at else { throw LocalStorageReadError.invalid }
        var value: UInt64 = 0
        for n in 0..<count { value |= UInt64(bytes[at + n]) << (n * 8) }; return value
    }
    func log(_ data: [UInt8]) throws -> [[UInt8]] {
        var result: [[UInt8]] = [], fragment: [UInt8]?
        for block in stride(from: 0, to: data.count, by: 32768) {
            let end = min(block + 32768, data.count); var at = block
            while at < end {
                try check()
                if end - at < 7 { guard data[at..<end].allSatisfy({ $0 == 0 }) else { throw LocalStorageReadError.invalid }; break }
                let checksum = try Self.little(data, at, 4), length = Int(try Self.little(data, at + 4, 2)), type = data[at + 6]; at += 7
                if checksum == 0 && length == 0 && type == 0 {
                    guard data[at..<end].allSatisfy({ $0 == 0 }) else { throw LocalStorageReadError.invalid }; break
                }
                guard length <= end - at, (1...4).contains(type) else { throw LocalStorageReadError.invalid }
                let bytes = Array(data[at..<at + length]); at += length
                guard try crc(bytes, type: type, first: true) == checksum else { throw LocalStorageReadError.invalid }
                if type == 1 { guard fragment == nil else { throw LocalStorageReadError.invalid }; try expand(bytes.count); result.append(bytes) }
                else if type == 2 { guard fragment == nil else { throw LocalStorageReadError.invalid }; fragment = bytes }
                else {
                    guard fragment != nil else { throw LocalStorageReadError.invalid }
                    try limit(fragment!.count + bytes.count <= 8 * 1024 * 1024); fragment!.append(contentsOf: bytes)
                    if type == 4 { try expand(fragment!.count); result.append(fragment!); fragment = nil }
                }
                try limit(result.count <= 200000)
            }
        }
        guard fragment == nil else { throw LocalStorageReadError.invalid }; try check(); return result
    }
    func manifest(_ data: [UInt8]) throws -> Version {
        var tables: [UInt64: Table] = [:], logNumber: UInt64?, sequence: UInt64?, next: UInt64?, previous: UInt64 = 0, comparator = false
        for entry in try log(data) {
            let input = Cursor(entry); var scalars = Set<UInt64>(), removed: [(Int, UInt64)] = [], added: [Table] = []
            while !input.end {
                try record(); let tag = try input.varint()
                if [1, 2, 3, 4, 9].contains(tag), !scalars.insert(tag).inserted { throw LocalStorageReadError.invalid }
                switch tag {
                case 1:
                    guard try input.slice(128) == Array("leveldb.BytewiseComparator".utf8) else { throw LocalStorageReadError.unsupported }; comparator = true
                case 2: logNumber = try input.varint()
                case 3: next = try input.varint()
                case 4: sequence = try input.varint(); guard sequence! <= Self.maximumSequence else { throw LocalStorageReadError.invalid }
                case 9: previous = try input.varint()
                case 5: _ = try level(input); _ = try internalKey(input.slice(8192))
                case 6: removed.append((try level(input), try input.varint()))
                case 7:
                    let l = try level(input), n = try input.varint(), size = try input.varint(), small = try input.slice(8192), large = try input.slice(8192)
                    guard n > 0, size >= 48, try compare(small, large) <= 0 else { throw LocalStorageReadError.invalid }
                    try limit(size <= Self.maximumFile); added.append(Table(level: l, number: n, size: size, smallest: small, largest: large))
                default: throw LocalStorageReadError.unsupported
                }
            }
            for (level, n) in removed { if let t = tables[n], t.level != level { throw LocalStorageReadError.invalid }; tables.removeValue(forKey: n) }
            for table in added { guard tables.updateValue(table, forKey: table.number) == nil else { throw LocalStorageReadError.invalid } }
            try limit(tables.count <= 128)
        }
        guard comparator, let logNumber, logNumber > 0, let sequence, let next, next > logNumber, previous < next,
              tables.keys.allSatisfy({ $0 < next }) else { throw LocalStorageReadError.unsupported }
        return Version(log: logNumber, previousLog: previous, sequence: sequence, tables: tables)
    }
    func level(_ input: Cursor) throws -> Int { let l = try input.varint(); guard l <= 6 else { throw LocalStorageReadError.unsupported }; return Int(l) }
    func internalKey(_ key: [UInt8]) throws -> (sequence: UInt64, type: UInt8) {
        guard key.count >= 8 else { throw LocalStorageReadError.invalid }
        let tag = try Self.little(key, key.count - 8, 8), type = UInt8(tag & 255)
        guard type <= 1 else { throw LocalStorageReadError.unsupported }; return (tag >> 8, type)
    }
    func compare(_ a: [UInt8], _ b: [UInt8]) throws -> Int {
        _ = try internalKey(a); _ = try internalKey(b)
        let x = a.dropLast(8), y = b.dropLast(8)
        if x != y { return x.lexicographicallyPrecedes(y) ? -1 : 1 }
        let u = try Self.little(a, a.count - 8, 8), v = try Self.little(b, b.count - 8, 8)
        return u == v ? 0 : (u > v ? -1 : 1)
    }
    func read(version: Version, tables: [UInt64: [UInt8]], logs: [[UInt8]], keys: [[UInt8]], unsupportedKeys: [[UInt8]]) throws -> [[UInt8]: [UInt8]] {
        wanted = Set(keys); unsupported = Set(unsupportedKeys); values = [:]
        for table in version.tables.values {
            try check(); guard let bytes = tables[table.number], bytes.count == table.size else { throw LocalStorageReadError.invalid }
            try tableData(table, bytes: bytes, sequence: version.sequence)
        }
        for bytes in logs { for entry in try log(bytes) { try batch(entry) } }
        try check(); return values.filter { $0.value.type == 1 }.mapValues(\.bytes)
    }
    func apply(key: [UInt8], value: [UInt8], sequence: UInt64, type: UInt8) throws {
        try record(); try limit(key.count <= 8192 && value.count <= 1024 * 1024)
        guard sequence <= Self.maximumSequence, type <= 1, type != 0 || value.isEmpty else { throw LocalStorageReadError.invalid }
        if unsupported.contains(key) { throw LocalStorageReadError.unsupported }
        guard wanted.contains(key) else { return }
        if let old = values[key] {
            if old.sequence > sequence { return }
            if old.sequence == sequence { guard old.type == type, old.bytes == value else { throw LocalStorageReadError.invalid }; return }
        }
        values[key] = Value(sequence: sequence, type: type, bytes: value)
    }
    func batch(_ data: [UInt8]) throws {
        guard data.count >= 12 else { throw LocalStorageReadError.invalid }
        var sequence = try Self.little(data, 0, 8); let count = try Self.little(data, 8, 4)
        try limit(count <= 200000)
        guard sequence <= Self.maximumSequence, count == 0 || count - 1 <= Self.maximumSequence - sequence else { throw LocalStorageReadError.invalid }
        let input = Cursor(data); input.at = 12
        for _ in 0..<count {
            try check(); let type = try input.byte()
            guard type <= 1 else { throw LocalStorageReadError.unsupported }
            let key = try input.slice(8192), value = type == 1 ? try input.slice(1024 * 1024) : []
            try apply(key: key, value: value, sequence: sequence, type: type); sequence += 1
        }
        guard input.end else { throw LocalStorageReadError.invalid }
    }
    struct Handle { let offset, size: Int; var end: Int { offset + size + 5 } }
    func handle(_ input: Cursor, end: Int) throws -> Handle {
        let offset = try input.varint(), size = try input.varint(); try limit(size <= 8 * 1024 * 1024)
        guard offset <= end, size + 5 <= UInt64(end) - offset else { throw LocalStorageReadError.invalid }
        return Handle(offset: Int(offset), size: Int(size))
    }
    func block(_ table: [UInt8], handle: Handle) throws -> [UInt8] {
        try check(); let payload = Array(table[handle.offset..<handle.offset + handle.size]), type = table[handle.offset + handle.size]
        guard try crc(payload, type: type, first: false) == Self.little(table, handle.offset + handle.size + 1, 4) else { throw LocalStorageReadError.invalid }
        let bytes: [UInt8]
        if type == 0 { bytes = payload } else if type == 1 { bytes = try snappy(payload) } else { throw LocalStorageReadError.unsupported }
        try expand(bytes.count); return bytes
    }
    func tableData(_ info: Table, bytes: [UInt8], sequence: UInt64) throws {
        let end = bytes.count - 48
        guard end >= 0, try Self.little(bytes, bytes.count - 8, 8) == 0xdb4775248b80fb57 else { throw LocalStorageReadError.invalid }
        let footer = Cursor(Array(bytes[end..<end + 40])), meta = try handle(footer, end: end), index = try handle(footer, end: end)
        guard footer.bytes.dropFirst(footer.at).allSatisfy({ $0 == 0 }) else { throw LocalStorageReadError.invalid }
        var used: [Handle] = []
        func reserve(_ h: Handle) throws {
            guard !used.contains(where: { h.offset < $0.end && $0.offset < h.end }) else { throw LocalStorageReadError.invalid }
            used.append(h); try limit(used.count <= 65536)
        }
        try reserve(meta); try reserve(index); _ = try entries(block(bytes, handle: meta))
        var previous: [UInt8]?, previousIndex: [UInt8]?
        for row in try entries(block(bytes, handle: index)) {
            _ = try internalKey(row.0)
            if let previousIndex, try compare(previousIndex, row.0) >= 0 { throw LocalStorageReadError.invalid }; previousIndex = row.0
            let input = Cursor(row.1), h = try handle(input, end: end)
            guard input.end else { throw LocalStorageReadError.invalid }; try reserve(h)
            let rows = try entries(block(bytes, handle: h)); guard !rows.isEmpty else { throw LocalStorageReadError.invalid }
            for entry in rows {
                let tag = try internalKey(entry.0)
                guard tag.sequence <= sequence else { throw LocalStorageReadError.invalid }
                if let previous, try compare(previous, entry.0) >= 0 { throw LocalStorageReadError.invalid }
                guard try compare(entry.0, info.smallest) >= 0, try compare(entry.0, info.largest) <= 0, try compare(entry.0, row.0) <= 0 else { throw LocalStorageReadError.invalid }
                try apply(key: Array(entry.0.dropLast(8)), value: entry.1, sequence: tag.sequence, type: tag.type); previous = entry.0
            }
        }
        guard previous != nil else { throw LocalStorageReadError.invalid }
    }
    func entries(_ bytes: [UInt8]) throws -> [([UInt8], [UInt8])] {
        guard bytes.count >= 8 else { throw LocalStorageReadError.invalid }
        let count = try Self.little(bytes, bytes.count - 4, 4)
        guard count > 0, count <= (bytes.count - 4) / 4 else { throw LocalStorageReadError.invalid }
        let end = bytes.count - 4 - Int(count) * 4
        var restarts = Set<Int>(), previousRestart = -1
        for n in 0..<Int(count) {
            try check(); let value = try Self.little(bytes, end + n * 4, 4)
            guard value <= end, Int(value) > previousRestart else { throw LocalStorageReadError.invalid }
            previousRestart = Int(value); restarts.insert(Int(value))
        }
        guard restarts.contains(0) else { throw LocalStorageReadError.invalid }
        if end == 0 && count == 1 { return [] }
        let input = Cursor(Array(bytes[0..<end])); var result: [([UInt8], [UInt8])] = [], previous: [UInt8] = []
        while !input.end {
            try record(); let at = input.at, shared = try input.length(8192), remaining = try input.length(8192), length = try input.length(1024 * 1024)
            guard shared <= previous.count, shared + remaining <= 8192 else { throw LocalStorageReadError.invalid }
            if restarts.remove(at) != nil, shared != 0 { throw LocalStorageReadError.invalid }
            let key = Array(previous.prefix(shared)) + (try input.take(remaining)), value = try input.take(length)
            result.append((key, value)); previous = key
        }
        guard restarts.isEmpty else { throw LocalStorageReadError.invalid }; return result
    }
    func snappy(_ bytes: [UInt8]) throws -> [UInt8] {
        let input = Cursor(bytes), expected = try input.length(8 * 1024 * 1024)
        var output = [UInt8](repeating: 0, count: expected), at = 0
        while !input.end {
            try check(); let tag = try input.byte(), type = tag & 3
            var length: Int, offset: UInt64
            if type == 0 {
                length = Int(tag >> 2)
                if length < 60 { length += 1 } else {
                    let extra = length - 59; var raw: UInt64 = 0
                    for n in 0..<extra { raw |= UInt64(try input.byte()) << (8 * n) }
                    try limit(raw < 8 * 1024 * 1024); length = Int(raw) + 1
                }
                guard length <= expected - at else { throw LocalStorageReadError.invalid }
                output.replaceSubrange(at..<at + length, with: try input.take(length)); at += length; continue
            }
            if type == 1 { length = 4 + Int((tag >> 2) & 7); offset = UInt64(tag & 224) << 3 | UInt64(try input.byte()) }
            else {
                length = 1 + Int(tag >> 2); offset = UInt64(try input.byte()); offset |= UInt64(try input.byte()) << 8
                if type == 3 { offset |= UInt64(try input.byte()) << 16; offset |= UInt64(try input.byte()) << 24 }
            }
            guard offset > 0, offset <= at, length <= expected - at else { throw LocalStorageReadError.invalid }
            for _ in 0..<length { output[at] = output[at - Int(offset)]; at += 1 }
        }
        guard at == expected else { throw LocalStorageReadError.invalid }; return output
    }
}
