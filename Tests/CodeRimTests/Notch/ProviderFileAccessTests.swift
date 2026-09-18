import Foundation
import XCTest
@testable import CodeRim

/// The borrowed-credential files are read on a timer, from paths another tool
/// owns. These pin the two things that made that different from how the
/// account code reads `auth.json`: a symlink was followed, and nothing capped
/// the size.
final class CredentialFileReaderTests: XCTestCase {
    private var scratch: URL!

    override func setUpWithError() throws {
        scratch = FileManager.default.temporaryDirectory
            .appendingPathComponent("CredentialFileReaderTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: scratch)
    }

    func testAnOrdinaryCredentialFileReadsBackWhole() throws {
        let url = scratch.appendingPathComponent("auth.json")
        try Data(#"{"token": "abc"}"#.utf8).write(to: url)

        XCTAssertEqual(CredentialFileReader.jsonObject(at: url)?["token"] as? String, "abc")
        XCTAssertEqual(CredentialFileReader.text(at: url), #"{"token": "abc"}"#)
    }

    /// The path belongs to another tool, so it can become a link to somewhere
    /// else without this app noticing. Reading through it would lift whatever
    /// it points at and send it to a vendor as that provider's token.
    func testASymlinkedCredentialFileIsNotFollowed() throws {
        let secret = scratch.appendingPathComponent("elsewhere.json")
        try Data(#"{"token": "not-yours"}"#.utf8).write(to: secret)
        let link = scratch.appendingPathComponent("auth.json")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: secret)

        XCTAssertNil(CredentialFileReader.data(at: link))
        XCTAssertNil(CredentialFileReader.jsonObject(at: link))
    }

    func testAFileOverTheCeilingIsRefusedRatherThanLoaded() throws {
        let url = scratch.appendingPathComponent("auth.json")
        try Data(repeating: UInt8(ascii: "a"), count: 4_096).write(to: url)

        XCTAssertNil(CredentialFileReader.data(at: url, maximumBytes: 1_024))
        XCTAssertEqual(CredentialFileReader.data(at: url, maximumBytes: 8_192)?.count, 4_096)
    }

    func testAMissingOrUnreadableFileIsSimplyAbsent() {
        XCTAssertNil(CredentialFileReader.data(at: scratch.appendingPathComponent("nothing.json")))
        // A directory is not a credential file, and must not read as an empty one.
        XCTAssertNil(CredentialFileReader.data(at: scratch))
    }

    /// Mode bits are deliberately not checked: these files belong to other
    /// tools, and `hosts.yml` in particular is commonly `0644`. Refusing those
    /// would disable the feature for most users.
    func testAWorldReadableFileWrittenByAnotherToolIsStillRead() throws {
        let url = scratch.appendingPathComponent("hosts.yml")
        FileManager.default.createFile(atPath: url.path, contents: Data("github.com:\n".utf8),
                                       attributes: [.posixPermissions: 0o644])
        XCTAssertEqual(CredentialFileReader.text(at: url), "github.com:\n")
    }
}

/// SQLite URI filenames are not paths. These stores live under the user's home
/// directory, so whatever that directory is named ends up inside the URI.
final class SQLiteURIPathTests: XCTestCase {
    func testAnOrdinaryPathIsUnchanged() {
        XCTAssertEqual(SQLiteStore.uriPath("/Users/a/Library/state.vscdb"),
                       "/Users/a/Library/state.vscdb")
    }

    /// Everything after the first `?` is read as query parameters, so an
    /// unescaped one truncates the path and appends nonsense options.
    func testAQuestionMarkIsEscapedRatherThanStartingTheQuery() {
        XCTAssertEqual(SQLiteStore.uriPath("/Users/who?/state.vscdb"),
                       "/Users/who%3F/state.vscdb")
    }

    func testFragmentAndEscapeCharactersAreEscaped() {
        XCTAssertEqual(SQLiteStore.uriPath("/Users/a#b/100%/state.vscdb"),
                       "/Users/a%23b/100%25/state.vscdb")
    }

    /// A home directory named in Korean must survive byte-for-byte; escaping
    /// per unicode scalar rather than per UTF-8 byte would mangle it.
    func testANonASCIIPathSurvivesUnchanged() {
        let path = "/Users/사용자/Library/state.vscdb"
        XCTAssertEqual(SQLiteStore.uriPath(path), path)
    }
}
