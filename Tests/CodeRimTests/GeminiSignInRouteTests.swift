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
}
