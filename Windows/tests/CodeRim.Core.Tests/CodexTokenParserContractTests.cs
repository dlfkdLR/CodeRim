using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;

namespace CodeRim.Core.Tests;

public sealed class CodexTokenParserContractTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var resource = typeof(CodexTokenParserContractTests).Assembly
            .GetManifestResourceStream("codex-token-parser-v1.json")!;
        using var document = JsonDocument.Parse(resource);
        var cases = document.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(cases);
        Assert.Equal(cases.Length, cases.Select(item => item.GetProperty("name").GetString()).Distinct().Count());
        foreach (var item in cases)
            yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PreservesSharedTokenParserContract(string name, string json)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(name, fixture.Name);
        var expected = fixture.WindowsExpected ?? fixture.Expected;
        var result = CodexJsonlParser.Parse(Encoding.UTF8.GetBytes(fixture.Line));
        Assert.Equal(Enum.Parse<ParsedLineKind>(expected.Kind, ignoreCase: true), result.Kind);
        Assert.Equal(expected.Diagnostic, result.Diagnostic);
        var token = expected.Token;
        Assert.Equal(token is null ? null : new TokenObservation(
            DateTimeOffset.UnixEpoch.AddSeconds(token.OccurredAt), token.Ordinal,
            token.LastUsage, token.CumulativeUsage), result.Token);
    }

    private sealed record Fixture(string Name, string Line, Expected Expected, Expected? WindowsExpected);
    private sealed record Expected(string Kind, string? Diagnostic, Observation? Token);
    private sealed record Observation(double OccurredAt, long? Ordinal, TokenUsage? LastUsage, TokenUsage? CumulativeUsage);
}
