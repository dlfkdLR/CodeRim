import XCTest
@testable import CodeRim

final class GeminiSignInRouteTests: XCTestCase {
    func testGeminiSignsInWithGoogleThroughTheInstalledCLI() throws {
        let bin = FileManager.default.temporaryDirectory.appendingPathComponent("gemini-route-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: bin) }
        let cli = bin.appendingPathComponent("gemini")
        try "#!/bin/sh\n".write(to: cli, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: cli.path)
        guard case .guided(let guided) = ExtendedProviderCatalog.geminiSignInRoute(displayName: "Gemini", searchPath: [bin.path]) else {
            return XCTFail("Expected a guided sign-in")
        }
        XCTAssertEqual(guided.action, .terminal(command: cli.path))
        XCTAssertFalse(guided.opensSettings)
        XCTAssertTrue(guided.note.contains("Google"))
    }

    func testGeminiPointsToTheInstallPageWithoutTheCLI() {
        let empty = FileManager.default.temporaryDirectory.appendingPathComponent("no-gemini-\(UUID().uuidString)")
        guard case .guided(let guided) = ExtendedProviderCatalog.geminiSignInRoute(
            displayName: "Gemini", home: empty, searchPath: [empty.path]) else { return XCTFail("Expected guidance") }
        if FileManager.default.isExecutableFile(atPath: "/usr/local/bin/gemini") || FileManager.default.isExecutableFile(atPath: "/opt/homebrew/bin/gemini") { return }
        guard case .browser(let url) = guided.action else { return XCTFail("Expected the install page") }
        XCTAssertEqual(url.host, "github.com")
    }

    func testAnAPIKeyConfigurationIsCalledOutBeforeSigningIn() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("gemini-home-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: home.appendingPathComponent(".gemini"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: home) }
        XCTAssertFalse(ExtendedProviderCatalog.usesKeyAuthentication(home: home))
        try #"{"security":{"auth":{"selectedType":"gemini-api-key"}}}"#.write(
            to: home.appendingPathComponent(".gemini/settings.json"), atomically: true, encoding: .utf8)
        XCTAssertTrue(ExtendedProviderCatalog.usesKeyAuthentication(home: home))
        try #"{"security":{"auth":{"selectedType":"oauth-personal"}}}"#.write(
            to: home.appendingPathComponent(".gemini/settings.json"), atomically: true, encoding: .utf8)
        XCTAssertFalse(ExtendedProviderCatalog.usesKeyAuthentication(home: home))
    }
}

final class GuidedSignInHintTests: XCTestCase {
    func testTheHintPrintsAsStepsAndWaitsForReturn() throws {
        let block = SignInLauncher.hintBlock("1. Type /auth and press Enter.\n2. Choose Sign in with Google; it's easy.\n'; rm -rf ~ #")
        XCTAssertTrue(block.contains("DO THIS IN THIS WINDOW"))
        XCTAssertTrue(block.contains("Press Return"))
        XCTAssertFalse(block.contains("; rm"))
        // The block must be valid shell that prints the steps and then proceeds on Return.
        let script = block.replacingOccurrences(of: "read -r '?Press Return to start. '", with: "true")
        let process = Process(); let out = Pipe()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh"); process.arguments = ["-c", script]; process.standardOutput = out
        try process.run(); process.waitUntilExit()
        let text = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        XCTAssertEqual(process.terminationStatus, 0)
        XCTAssertTrue(text.contains("1. Type /auth and press Enter."))
        XCTAssertEqual(SignInLauncher.hintBlock(""), "")
    }
}
