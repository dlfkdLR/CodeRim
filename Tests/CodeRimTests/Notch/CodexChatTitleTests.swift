import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

final class CodexChatTitleTests: XCTestCase {
    private func userMessage(_ text: String) throws -> Data {
        let record: [String: Any] = ["type": "response_item", "payload": [
            "type": "message", "role": "user", "content": [["type": "input_text", "text": text]]]]
        return try JSONSerialization.data(withJSONObject: record) + Data([10])
    }

    func testSeparateAttachmentPreambleDoesNotHideTheUserRequest() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".jsonl")
        defer { try? FileManager.default.removeItem(at: url) }
        let header = "# Files mentioned by the user:\n\n## template.html: /tmp/template.html\n\n## My request:\n"
        var data = try userMessage("# AGENTS.md instructions\n<INSTRUCTIONS>Test instructions</INSTRUCTIONS>")
        data += try userMessage("<environment_context>Fixture</environment_context>")
        data += try userMessage(header)
        data += try userMessage("첨부한 템플릿으로 프로젝트 소개 발표자료를 만들어줘.\n두 번째 줄")
        try data.write(to: url)
        XCTAssertEqual(CodexChatTitle.read(url), "첨부한 템플릿으로 프로젝트 소개 발표자료를 만들어줘.")
        XCTAssertEqual(CodexChatTitle.read(url), "첨부한 템플릿으로 프로젝트 소개 발표자료를 만들어줘.")
        XCTAssertNil(CodexChatTitle.request(in: header))
        XCTAssertEqual(CodexChatTitle.request(in: header + "실제 요청"), "실제 요청")
    }

    func testIncompleteAppendAndRewrittenLogInvalidateTheTitleCache() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".jsonl")
        defer { try? FileManager.default.removeItem(at: url) }
        let data = try userMessage("첫 번째 요청")
        try data.dropLast().write(to: url)
        XCTAssertNil(CodexChatTitle.read(url))
        let handle = try FileHandle(forWritingTo: url)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data([10]))
        try handle.close()
        XCTAssertEqual(CodexChatTitle.read(url), "첫 번째 요청")
        let rewrite = try FileHandle(forWritingTo: url)
        try rewrite.seek(toOffset: 0)
        let changed = try userMessage("이전보다 길게 덮어쓴 다른 요청")
        try rewrite.write(contentsOf: changed)
        try rewrite.truncate(atOffset: UInt64(changed.count))
        try rewrite.close()
        XCTAssertEqual(CodexChatTitle.read(url), "이전보다 길게 덮어쓴 다른 요청")
        try userMessage("바뀐 요청").write(to: url, options: .atomic)
        XCTAssertEqual(CodexChatTitle.read(url), "바뀐 요청")
        let padding = Data(repeating: 32, count: CodexChatTitle.maximumScanBytes + 1) + Data([10])
        try (padding + userMessage("읽기 한도 밖의 요청")).write(to: url, options: .atomic)
        XCTAssertNil(CodexChatTitle.read(url))
    }
}

@MainActor
final class SessionTokenDisplayTests: XCTestCase {
    func testNativeSessionRowsExposeTheMainTotalAndDoNotDuplicateItOnChildren() throws {
        let now = Date()
        let parent = AgentSession(id: "parent", name: "CodeRim", detail: "프로젝트 발표자료",
                                  state: .busy, waitingFor: nil, since: now, codexThreadID: "11111111-1111-1111-1111-111111111111")
        let child = AgentSession(id: "child", name: "CodeRim", detail: "Hooke",
                                 state: .idle, waitingFor: nil, since: now, codexThreadID: "22222222-2222-2222-2222-222222222222",
                                 parentThread: .init(id: "11111111-1111-1111-1111-111111111111", title: parent.detail))
        let summary = try XCTUnwrap(ActivitySummary(sessions: [parent, child]))
        let view = SessionList(summary: summary, now: now, cap: 2, onToggleExpansion: {},
                               tokenTotals: ["parent": 5_329_848, "child": 12_345])
            .frame(width: 340).padding(16).background(Color.black).preferredColorScheme(.dark)
        _ = NSApplication.shared
        let enhance = NSSelectorFromString("accessibilitySetValue:forAttribute:")
        if NSApplication.shared.responds(to: enhance) {
            NSApplication.shared.perform(enhance, with: true as NSNumber, with: "AXEnhancedUserInterface" as NSString)
        }
        let host = NSHostingView(rootView: view)
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: host.fittingSize),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        defer { window.close() }
        for _ in 0..<6 { host.layoutSubtreeIfNeeded() }
        var values: [String: String] = [:]
        func collect(_ element: Any) {
            let object = element as AnyObject
            if let identifier = object.accessibilityIdentifier?(),
               let node = element as? NSObject,
               node.responds(to: NSSelectorFromString("accessibilityValue")),
               let value = node.perform(NSSelectorFromString("accessibilityValue"))?.takeUnretainedValue() as? String {
                values[identifier] = value
            }
            for child in object.accessibilityChildren?() ?? [] { collect(child) }
        }
        collect(host)
        XCTAssertTrue(try XCTUnwrap(values["notch.session.parent"]).contains("tokens"))
        XCTAssertFalse(try XCTUnwrap(values["notch.session.child"]).contains("tokens"))
        if let directory = ProcessInfo.processInfo.environment["CODERIM_CHAT_INFO_CAPTURE_DIR"] {
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            let data = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            try data.write(to: URL(fileURLWithPath: directory).appendingPathComponent("chat-token-row.png"))
        }
    }
}
