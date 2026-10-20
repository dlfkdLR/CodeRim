using System.Text;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;

namespace CodeRim.Core.Tests;

public sealed class CodexJsonlParserTests
{
    [Fact]
    public void ParsesTokenObservationWithoutDoubleCountingCachedInput()
    {
        const string line = """
            {"timestamp":"2026-08-27T01:02:03.456Z","type":"event_msg","ordinal":42,"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1200,"cached_input_tokens":800,"output_tokens":300},"last_token_usage":{"input_tokens":200,"cached_input_tokens":150,"output_tokens":50}}}}
            """;

        var result = CodexJsonlParser.Parse(Encoding.UTF8.GetBytes(line));

        Assert.Equal(ParsedLineKind.Token, result.Kind);
        Assert.Equal(42, result.Token!.Ordinal);
        Assert.Equal(new TokenUsage(200, 150, 50), result.Token.LastUsage);
        Assert.Equal(1_500, result.Token.CumulativeUsage!.Value.TotalTokens);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1.5")]
    [InlineData("1000.0")]
    [InlineData("1000000000001")]
    public void RejectsNonIntegerOrUnboundedComponents(string input)
    {
        var line = string.Concat(
            "{\"timestamp\":\"2026-08-27T01:02:03Z\",\"type\":\"event_msg\",",
            "\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{",
            "\"input_tokens\":", input,
            ",\"cached_input_tokens\":0,\"output_tokens\":2}}}}");

        var result = CodexJsonlParser.Parse(Encoding.UTF8.GetBytes(line));

        Assert.Equal(ParsedLineKind.Malformed, result.Kind);
    }

    [Fact]
    public void ParsesInheritedSessionMetadata()
    {
        const string line = """
            {"timestamp":"2026-08-27T01:02:03Z","type":"session_meta","payload":{"id":"child","parent_thread_id":"parent","subagent_history_start_ordinal":10}}
            """;

        var result = CodexJsonlParser.Parse(Encoding.UTF8.GetBytes(line));

        Assert.Equal(ParsedLineKind.SessionMetadata, result.Kind);
        Assert.True(result.SessionMetadata!.InheritsHistory);
        Assert.Equal(10, result.SessionMetadata.SubagentHistoryStartOrdinal);
    }
}
