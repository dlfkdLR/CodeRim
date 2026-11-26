import XCTest
@testable import CodeRim

final class OllamaKeyPolicyTests: XCTestCase {
    func testAnExplicitEnvironmentKeyWinsWithoutReadingTheVault() {
        var reads = 0
        func read() -> String? { reads += 1; return "fixture-stored" }
        XCTAssertEqual(OllamaKeyPolicy.resolve(environmentKey: "  fixture  ", readStored: read), "  fixture  ")
        XCTAssertEqual(OllamaKeyPolicy.resolve(environmentKey: " ", readStored: read), " ")
        XCTAssertEqual(reads, 0)
        XCTAssertEqual(OllamaKeyPolicy.resolve(environmentKey: "", readStored: read), "fixture-stored")
        XCTAssertEqual(OllamaKeyPolicy.resolve(environmentKey: nil, readStored: read), "fixture-stored")
        XCTAssertEqual(reads, 2)
    }

    func testSubmittedReplacementCommitsOnlyAfterStorageSucceeds() {
        var value = "old-fixture", allowWrite = false, writes = 0
        func submit() -> OllamaKeyPolicy.Display {
            OllamaKeyPolicy.edit(.submit, draft: "  새 값  ", write: { input in
                writes += 1
                if allowWrite { value = input }
                return allowWrite
            }, remove: { XCTFail("Unexpected delete"); return false }).display
        }
        XCTAssertEqual(submit(), .saveFailed)
        XCTAssertEqual(value, "old-fixture")
        allowWrite = true
        XCTAssertEqual(submit(), .stored)
        XCTAssertEqual(value, "새 값")
        XCTAssertEqual(writes, 2)
    }

    func testWhitespaceNeverModifiesAnExistingKey() {
        for draft in ["", "  ", "\t\r\n", "\u{a0}", "\u{2003}", "\u{200B}"] {
            XCTAssertFalse(OllamaKeyPolicy.canSubmit(draft))
            for intent in [OllamaKeyPolicy.Intent.submit, .assign] {
                let result = OllamaKeyPolicy.edit(intent, draft: draft,
                    write: { _ in XCTFail("Unexpected write"); return true },
                    remove: { XCTFail("Unexpected remove"); return true })
                XCTAssertFalse(result.succeeded)
                XCTAssertEqual(result.display, .keep)
            }
        }
        XCTAssertTrue(OllamaKeyPolicy.canSubmit("  fixture  "))
    }

    func testRefusedRemovalKeepsTheStoredKeyAndReportsTheFailure() {
        let result = OllamaKeyPolicy.edit(.remove, draft: "pending",
            write: { _ in XCTFail("Unexpected write"); return true }, remove: { false })
        XCTAssertFalse(result.succeeded)
        XCTAssertEqual(result.display, .removeFailed)
    }

    func testEmptySubmitDoesNothingAndExplicitRemoveClearsTheStoredState() {
        var calls = 0
        func edit(_ intent: OllamaKeyPolicy.Intent) -> OllamaKeyPolicy.Display {
            OllamaKeyPolicy.edit(intent, draft: "", write: { _ in XCTFail("Unexpected write"); return false },
                                 remove: { calls += 1; return true }).display
        }
        XCTAssertEqual(edit(.submit), .keep)
        XCTAssertEqual(calls, 0)
        XCTAssertEqual(edit(.remove), .absent)
        XCTAssertEqual(calls, 1)
    }
}
