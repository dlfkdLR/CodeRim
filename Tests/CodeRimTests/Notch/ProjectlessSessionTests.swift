import CSQLite
import XCTest
@testable import CodeRim
final class ProjectlessSessionTests: XCTestCase {
    func testGeneratedWorkspaceNamesAreNotProjects() {
        for path in ["/Users/me/Documents/Codex/2026-09-21/unf", "C:\\Users\\me\\Documents\\Codex\\2026-09-21\\new-chat"] {
            XCTAssertTrue(CodexStore.Thread.isProjectlessWorkspace(path), path)
        }
        for path in ["/Users/me/projects/unf", "/Users/me/Documents/Codex/repo", "/Users/me/Documents/Codex/2026-09-21/chat/work/real-project"] {
            XCTAssertFalse(CodexStore.Thread.isProjectlessWorkspace(path), path)
        }
    }
    func testFinishedAndArchivedAncestorsResolveToMainChat() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at:url) }
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path,&db),SQLITE_OK)
        defer { sqlite3_close(db) }
        let main = "11111111-1111-1111-1111-111111111111"
        let middle = "22222222-2222-2222-2222-222222222222"
        let child = "33333333-3333-3333-3333-333333333333"
        let schema = "CREATE TABLE threads(id TEXT, rollout_path TEXT, cwd TEXT, title TEXT, updated_at INTEGER, archived INTEGER, source TEXT)"
        XCTAssertEqual(sqlite3_exec(db,schema,nil,nil,nil),SQLITE_OK)
        for (id,title,parent,archived,time) in [(main,"Main","",1,1),(middle,"Finished",main,1,2),(child,"Live",middle,0,3)] {
            let source = parent.isEmpty ? "{}" : "{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"\(parent)\"}}}"
            let sql = "INSERT INTO threads VALUES ('\(id)','/tmp/none','/Users/me/Documents/Codex/2026-09-21/unf','\(title)',\(time),\(archived),'\(source)')"
            XCTAssertEqual(sqlite3_exec(db,sql,nil,nil,nil),SQLITE_OK)
        }
        let thread = try XCTUnwrap(CodexStore.threads(in:url,limit:1).first)
        XCTAssertEqual(thread.parentThread?.id,main)
        XCTAssertEqual(thread.parentThread?.title,"Main")
        XCTAssertEqual(thread.projectName,"")
    }

}
