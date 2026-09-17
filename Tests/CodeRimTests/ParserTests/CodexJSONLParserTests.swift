import Foundation
import XCTest
@testable import CodeRim

final class CodexJSONLParserTests: XCTestCase {
    private let parser = CodexJSONLParser()

    func testParsesObservedTokenEventWithoutDoubleCountingCachedInput() throws {
        let line = #"{"timestamp":"2026-08-27T01:02:03.456Z","type":"event_msg","ordinal":42,"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1200,"cached_input_tokens":800,"output_tokens":300,"total_tokens":1500},"last_token_usage":{"input_tokens":200,"cached_input_tokens":150,"output_tokens":50,"total_tokens":250}}}}"#

        guard case let .token(event) = parser.parse(Data(line.utf8)) else {
            return XCTFail("Expected token event")
        }

        XCTAssertEqual(event.ordinal, 42)
        XCTAssertEqual(event.lastUsage, TokenUsage(inputTokens: 200, cachedInputTokens: 150, outputTokens: 50))
        XCTAssertEqual(event.lastUsage?.totalTokens, 250)
        XCTAssertEqual(event.cumulativeUsage?.totalTokens, 1_500)
    }

    func testUnknownEventIsIgnored() {
        let line = #"{"timestamp":"2026-08-27T01:02:03Z","type":"event_msg","payload":{"type":"unknown_future_event","new_field":true}}"#
        XCTAssertEqual(parser.parse(Data(line.utf8)), .ignored)
    }

    func testMalformedAndOversizedLinesFailSafely() {
        XCTAssertEqual(parser.parse(Data(#"{"type": "#.utf8)), .malformed("invalid JSON"))
        XCTAssertEqual(
            parser.parse(Data(repeating: 65, count: CodexJSONLParser.maximumLineBytes + 1)),
            .malformed("line exceeds the 1 MiB safety limit")
        )
    }

    func testRejectsImpossibleCachedInput() {
        let line = #"{"timestamp":"2026-08-27T01:02:03Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"cached_input_tokens":11,"output_tokens":2}}}}"#
        XCTAssertEqual(parser.parse(Data(line.utf8)), .malformed("token event has no supported usage object"))
    }

    func testRejectsBooleanFractionalAndUnboundedTokenComponents() {
        for input in ["true", "1.5", "1000.0", "999999999999.99999999", "1000000000001"] {
            let line = "{\"timestamp\":\"2026-08-27T01:02:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":\(input),\"cached_input_tokens\":0,\"output_tokens\":2}}}}"
            XCTAssertEqual(
                parser.parse(Data(line.utf8)),
                .malformed("token event has no supported usage object")
            )
        }
    }

    func testTokenEventWithoutInfoIsMalformed() {
        let line = #"{"timestamp":"2026-08-27T01:02:03Z","type":"event_msg","payload":{"type":"token_count"}}"#
        XCTAssertEqual(parser.parse(Data(line.utf8)), .malformed("token event is missing usage info"))
    }

    func testParsesSessionMetadataWithoutConversationContent() {
        let line = #"{"timestamp":"2026-08-27T01:02:03Z","type":"session_meta","payload":{"id":"session-1","cwd":"/tmp/project","model":"gpt-example","unrelated":"ignored"}}"#
        let occurredAt = try! Date.ISO8601FormatStyle().parse("2026-08-27T01:02:03Z")
        XCTAssertEqual(
            parser.parse(Data(line.utf8)),
            .sessionMetadata(
                SessionMetadata(
                    id: "session-1",
                    model: "gpt-example",
                    workingDirectory: "/tmp/project",
                    occurredAt: occurredAt
                )
            )
        )
    }

    func testParsesTaskStartBoundaryWithoutConversationContent() throws {
        let line = #"{"timestamp":"2026-08-27T01:02:03.456Z","type":"event_msg","ordinal":10,"payload":{"type":"task_started","turn_id":"ignored","started_at":1787792523}}"#
        let occurredAt = try Date.ISO8601FormatStyle(includingFractionalSeconds: true)
            .parse("2026-08-27T01:02:03.456Z")

        XCTAssertEqual(
            parser.parse(Data(line.utf8)),
            .taskStarted(
                TaskStartedMetadata(
                    occurredAt: occurredAt,
                    startedAt: Date(timeIntervalSince1970: 1_787_792_523),
                    ordinal: 10
                )
            )
        )
    }

    func testParsesTurnContextModelAndWorkingDirectory() {
        let line = #"{"timestamp":"2026-08-27T01:02:03Z","type":"turn_context","payload":{"model":"gpt-example","cwd":"/tmp/project","content":"ignored"}}"#
        XCTAssertEqual(
            parser.parse(Data(line.utf8)),
            .turnContext(TurnContextMetadata(model: "gpt-example", workingDirectory: "/tmp/project"))
        )
    }

    func testCountsOnlyUserInputImagesWithoutRetainingTheirContent() {
        let line = #"{"timestamp":"2026-08-27T01:02:03Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"private"},{"type":"input_image","image_url":"data:image/png;base64,private"},{"type":"input_image","image_url":"file:///private/image.png"}]}}"#
        let occurredAt = try! Date.ISO8601FormatStyle().parse("2026-08-27T01:02:03Z")
        XCTAssertEqual(
            parser.parse(Data(line.utf8)),
            .imageAttachments(ImageAttachmentMetadata(occurredAt: occurredAt, count: 2))
        )

        let missingTimestamp = #"{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_image","image_url":"private"}]}}"#
        XCTAssertEqual(
            parser.parse(Data(missingTimestamp.utf8)),
            .malformed("image metadata is missing a valid timestamp")
        )

        let assistant = #"{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"input_image","image_url":"ignored"}]}}"#
        XCTAssertEqual(parser.parse(Data(assistant.utf8)), .ignored)
    }
}
