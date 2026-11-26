import CryptoKit
import Darwin
import Foundation

let fm = FileManager.default
let args = CommandLine.arguments
let root = URL(fileURLWithPath: args[2], isDirectory: true)
let file = root.appendingPathComponent("공유/Usage Export/snapshot.json")
let epoch = Date(timeIntervalSince1970: 1_790_000_000)

func fixture(_ revision: Int, _ count: Int = 5) -> CompanionSnapshot {
    let providers = (0..<count).map { i in
        CompanionProvider(id: "서비스-\(i)", name: "é · \(i)", localUsage: nil,
            limits: CompanionLimits(state: .ready, updatedAt: epoch, windows: [
                .init(id: "window", name: "대화 \(revision)", durationMinutes: 300,
                      usedPercent: Double(i % 130), resetsAt: epoch.addingTimeInterval(300),
                      usedCount: revision)]), enabled: i.isMultiple(of: 2))
    }
    return CompanionSnapshot(generatedAt: epoch.addingTimeInterval(Double(revision)), providers: providers)
}

func mode(_ url: URL) throws -> Int {
    (try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as! NSNumber).intValue
}

func check(_ value: Bool, _ message: String) {
    if !value { fatalError(message) }
}

func verify(_ value: CompanionSnapshot, _ url: URL) throws -> String {
    let bytes = try Data(contentsOf: url)
    let encoded = try CompanionSnapshotFile.encode(value)
    let read = try CompanionSnapshotFile.read(from: url)
    let permissions = try mode(url)
    check(bytes == encoded && read == value && permissions == 0o600, "snapshot or mode differed")
    return SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
}

func failure(_ url: URL) throws -> String {
    do {
        try CompanionSnapshotFile.write(fixture(0), to: url)
        return "success"
    } catch CompanionSnapshotFile.SnapshotError.invalidFile { return "invalidFile" }
    catch {
        let e = error as NSError
        return "\(e.domain) \(e.code)"
    }
}

switch args[1] {
case "one":
    let value = fixture(Int(args[3])!)
    try CompanionSnapshotFile.write(value, to: file)
    print(try verify(value, file))
case "read":
    print(try verify(fixture(Int(args[3])!), file))
case "writer":
    for i in 0..<400 { try CompanionSnapshotFile.write(fixture(i%7), to: file) }
case "reader":
    let valid = try Set((0..<7).map { try CompanionSnapshotFile.encode(fixture($0)) })
    for _ in 0..<2000 { check(try valid.contains(Data(contentsOf: file)), "partial or unknown bytes") }
    print("2000 complete snapshots")
case "bench":
    let count = Int(args[3])!, loops = Int(args[4])!
    let values = [fixture(1, count), fixture(2, count)]
    try CompanionSnapshotFile.write(values[0], to: file)
    let start = DispatchTime.now().uptimeNanoseconds
    for i in 0..<loops { try CompanionSnapshotFile.write(values[i%2], to: file) }
    let elapsed = DispatchTime.now().uptimeNanoseconds - start
    print(try verify(values[(loops-1)%2], file), Double(elapsed) / Double(loops))
default:
    let old = fixture(1), newer = fixture(2)
    try CompanionSnapshotFile.write(old, to: file)
    let directory = file.deletingLastPathComponent()
    check(try mode(directory) == 0o700, "new directory mode")
    let oldHandle = try FileHandle(forReadingFrom: file)
    try fm.setAttributes([.posixPermissions: 0o400], ofItemAtPath: file.path)
    try CompanionSnapshotFile.write(newer, to: file)
    let oldBytes = try oldHandle.readToEnd()
    try oldHandle.close()
    check(oldBytes == (try CompanionSnapshotFile.encode(old)), "reader's old inode changed")
    print("old and new", try verify(newer, file))
    let link = directory.appendingPathComponent("link")
    try fm.createSymbolicLink(at: link, withDestinationURL: file)
    print("linked file", try failure(link), try verify(newer, file))
    let alias = root.appendingPathComponent("alias")
    try fm.createSymbolicLink(at: alias, withDestinationURL: directory)
    print("linked dir", try failure(alias.appendingPathComponent("other")), try verify(newer, file))
    let targetDirectory = directory.appendingPathComponent("not-a-file")
    try fm.createDirectory(at: targetDirectory, withIntermediateDirectories: false)
    print("directory destination", try failure(targetDirectory))
    print("regular parent", try failure(file.appendingPathComponent("child")))
    print("name too long", try failure(directory.appendingPathComponent(String(repeating:"x",count:270))))
    var broken = old
    broken.providers[0].limits.staleAfterSeconds = .nan
    do {
        try CompanionSnapshotFile.write(broken, to: file)
        fatalError("invalid new snapshot accepted")
    } catch is EncodingError {
        print("retry preserves old", try verify(newer, file))
    }
    try fm.setAttributes([.posixPermissions: 0o500], ofItemAtPath: directory.path)
    let denied = try failure(file)
    try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
    print("denied", denied, try verify(newer, file))
    try fm.setAttributes([.posixPermissions: 0o300], ofItemAtPath: directory.path)
    let searchOnly = try failure(file)
    try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
    print("write-search directory", searchOnly)
    let previous = umask(0o777)
    try CompanionSnapshotFile.write(old, to: file)
    _ = umask(previous)
    print("private mode despite umask", try verify(old,file))
    for i in 0..<96 {
        let value = fixture(i, i%13)
        try CompanionSnapshotFile.write(value, to: file)
        print("roundtrip", i, try verify(value, file))
    }
    let names = try fm.contentsOfDirectory(atPath: directory.path)
    check(Set(names) == ["link","not-a-file","snapshot.json"], "temporary file remained")
}
