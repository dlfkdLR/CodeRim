using System.Text;
using CodeRim.Core.Parsing;

namespace CodeRim.UsageCore.Tests;

public sealed class RustCodexTokenParserTests
{
    private const string Row = """{"type":"event_msg","timestamp":"2024-02-29T23:59:59.1234567-03:30","ordinal":23,"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":18,"cached_input_tokens":3,"output_tokens":7,"cache_write_input_tokens":2}}}}""";

    [Theory]
    [InlineData("canonical", true)]
    [InlineData("rate-limits", true)]
    [InlineData("non-ISO-date", true)]
    [InlineData("string-count", false)]
    [InlineData("invalid-ordinal", false)]
    [InlineData("duplicate-field", false)]
    [InlineData("invalid-time", false)]
    [InlineData("invalid-usage", false)]
    [InlineData("extra-field", false)]
    public void MatchesWindowsParserOnASlicedBuffer(string variant, bool borrowed)
    {
        var text = variant switch
        {
            "rate-limits" => Row.Replace("\"info\":", "\"rate_limits\":{\"primary\":{\"used_percent\":23.5}},\"info\":"),
            "non-ISO-date" => Row.Replace("2024-02-29T23:59:59.1234567-03:30", "02/29/2024 13:02:03 -04:00"),
            "string-count" => Row.Replace("18,", "\"18\","),
            "invalid-ordinal" => Row.Replace("\"ordinal\":23", "\"ordinal\":-1"),
            "duplicate-field" => Row.Replace("\"ordinal\":23", "\"ordinal\":1,\"ordinal\":23"),
            "invalid-time" => Row.Replace("2024-02-29", "invalid"),
            "invalid-usage" => Row.Replace("\"cached_input_tokens\":3", "\"cached_input_tokens\":99"),
            "extra-field" => Row.Replace("\"ordinal\":", "\"future\":42,\"ordinal\":"),
            _ => Row
        };
        const string prefix = "outside!";
        var bytes = Encoding.UTF8.GetBytes(prefix + text + "!");
        var slice = bytes.AsMemory(prefix.Length, Encoding.UTF8.GetByteCount(text));
        var expected = CodexJsonlParser.ParseManaged(slice);
        var actual = CodexJsonlParser.Parse(slice);
        Assert.Equal(borrowed, RustCodexTokenParser.TryParse(slice.Span) != null);
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Token?.OccurredAt.Offset, actual.Token?.OccurredAt.Offset);
        Assert.Equal(expected.Token?.OccurredAt.UtcTicks, actual.Token?.OccurredAt.UtcTicks);
    }
}
