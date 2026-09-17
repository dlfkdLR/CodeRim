import ClaudeBridgeCore
import Foundation

private func fail(_ message: String, code: Int32 = 1) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(code)
}

guard CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--output" else {
    fail("Usage: CodeRimClaudeBridge --output <absolute-path>", code: 2)
}
let output = URL(fileURLWithPath: CommandLine.arguments[2]).standardizedFileURL
guard output.path.hasPrefix("/"), !output.path.contains("\0") else {
    fail("Output path must be absolute.", code: 2)
}
let fileManager = FileManager.default
let applicationSupport = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
    ?? fileManager.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support", isDirectory: true)
guard ClaudeBridgeOutputPath.isAllowed(output, applicationSupportDirectory: applicationSupport) else {
    fail("Output path is outside CodeRim application support.", code: 2)
}

do {
    let input = try FileHandle.standardInput.read(upToCount: ClaudeRateLimitCodec.maximumInputBytes + 1) ?? Data()
    guard input.count <= ClaudeRateLimitCodec.maximumInputBytes else {
        fail("Status-line input is too large.")
    }
    guard let snapshot = try ClaudeRateLimitCodec.parseStatusLineInput(input) else {
        print("Claude limits appear after the first response.")
        exit(0)
    }
    let directory = output.deletingLastPathComponent()
    try fileManager.createDirectory(
        at: directory,
        withIntermediateDirectories: true,
        attributes: [.posixPermissions: 0o700]
    )
    try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
    try ClaudeRateLimitCodec.encode(snapshot).write(to: output, options: .atomic)
    try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: output.path)

    let summaries = [
        snapshot.fiveHour.map { "5h \(Int((100 - $0.usedPercentage).rounded()))% left" },
        snapshot.sevenDay.map { "7d \(Int((100 - $0.usedPercentage).rounded()))% left" }
    ].compactMap { $0 }
    print(summaries.joined(separator: " · "))
} catch {
    fail("Unable to update Claude limits.")
}
