import XCTest
@testable import CodeRim

final class CodexPendingQuestionTests: XCTestCase {
    private let started = Date(timeIntervalSince1970: 1_800_000_000)

    private func stamp(_ offset: TimeInterval) -> String {
        ISO8601DateFormatter().string(from: started.addingTimeInterval(offset))
    }

    private func call(_ id: String, at offset: TimeInterval, name: String = "request_user_input_async") -> String {
        let arguments = #"{\"questions\":[{\"title\":\"Delete it?\",\"options\":[\"Yes\",\"No\"]}]}"#
        return #"{"timestamp":"\#(stamp(offset))","type":"response_item","payload":{"type":"function_call","name":"\#(name)","arguments":"\#(arguments)","call_id":"\#(id)"}}"#
    }

    private func output(_ id: String, at offset: TimeInterval) -> String {
        #"{"timestamp":"\#(stamp(offset))","type":"response_item","payload":{"type":"function_call_output","call_id":"\#(id)","output":"ok"}}"#
    }

    private func reply(_ id: String, at offset: TimeInterval) -> String {
        #"{"timestamp":"\#(stamp(offset))","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<send_user_message_question_reply> [\"request_user_input_async\",\"\#(id)\",0] </send_user_message_question_reply>"}]}}"#
    }

    private func pending(_ lines: [String]) -> String? {
        CodexPendingQuestion.pending(in: Data((lines.joined(separator: "\n") + "\n").utf8), since: started)
    }

    func testAnUnansweredQuestionIsPendingWithItsText() {
        XCTAssertEqual(pending([call("c1", at: 5)]), "Delete it?")
        XCTAssertEqual(pending([call("c1", at: 5, name: "request_user_input")]), "Delete it?")
    }

    func testAReplyAnswersIt() {
        XCTAssertNil(pending([call("c1", at: 5), reply("c1", at: 9)]))
    }

    func testThePlainFormIsAnsweredByItsOutputButTheAsyncFormIsNot() {
        XCTAssertNil(pending([call("c1", at: 5, name: "request_user_input"), output("c1", at: 9)]))
        XCTAssertEqual(pending([call("c1", at: 5), output("c1", at: 6)]), "Delete it?",
                       "accepted is not an answer")
        XCTAssertNil(pending([call("c1", at: 5), output("c1", at: 6), reply("c1", at: 20)]))
    }

    func testOnlyTheUnansweredOneOfTwoCounts() {
        XCTAssertEqual(pending([call("c1", at: 5), call("c2", at: 6), reply("c1", at: 9)]), "Delete it?")
        XCTAssertNil(pending([call("c1", at: 5), call("c2", at: 6), reply("c1", at: 9), reply("c2", at: 10)]))
    }

    func testAQuestionFromAnEarlierTurnIsIgnored() {
        XCTAssertNil(pending([call("old", at: -600)]))
    }

    func testTheCallRecordedAgainAsAnEventDoesNotAnswerIt() {
        let event = #"{"timestamp":"\#(stamp(6))","type":"event_msg","payload":{"type":"item_completed","item":{"call_id":"c1"}}}"#
        XCTAssertEqual(pending([call("c1", at: 5), event]), "Delete it?")
    }

    func testOrdinaryWorkIsNotAQuestion() {
        let work = #"{"timestamp":"\#(stamp(3))","type":"response_item","payload":{"type":"function_call","name":"shell","call_id":"x"}}"#
        XCTAssertNil(pending([work]))
    }
}
