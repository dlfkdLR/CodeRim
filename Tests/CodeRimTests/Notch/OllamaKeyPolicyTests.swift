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
        XCTAssertEqual(submit(), .keep)
        XCTAssertEqual(value, "old-fixture")
        allowWrite = true
        XCTAssertEqual(submit(), .stored)
        XCTAssertEqual(value, "새 값")
        XCTAssertEqual(writes, 2)
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
