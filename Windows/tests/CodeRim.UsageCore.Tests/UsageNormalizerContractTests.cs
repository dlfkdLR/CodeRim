using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.UsageCore.Tests;

public sealed class UsageNormalizerContractTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var resource = typeof(UsageNormalizerContractTests).Assembly
            .GetManifestResourceStream("usage-normalization-v1.json")!;
        using var document = JsonDocument.Parse(resource);
        var cases = document.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(cases);
        Assert.Equal(cases.Length, cases.Select(item => item.GetProperty("name").GetString()).Distinct().Count());
        foreach (var item in cases)
            yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PreservesSharedNormalizationContract(string name, string json)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(name, fixture.Name);
        Assert.NotEmpty(fixture.Steps);
        var state = UsageNormalizationState.Empty;
        foreach (var step in fixture.Steps)
        {
            var result = UsageNormalizer.Normalize(new TokenObservation(
                DateTimeOffset.FromUnixTimeSeconds(step.OccurredAt), null,
                step.LastUsage, step.CumulativeUsage), state);
            var expected = step.Expected;
            // Windows keeps repaired zero rows as durable partial-history evidence.
            Assert.Equal(expected.WindowsDelta ?? expected.Delta, result.Delta);
            Assert.Equal(new UsageNormalizationState(expected.CumulativeHighWaterMark,
                expected.LastObservedAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null,
                Enum.Parse<DataQuality>(expected.Quality, ignoreCase: true)), result.State);
            Assert.Equal(expected.Diagnostic, result.Diagnostic);
            Assert.Equal(expected.WindowsRepaired, result.Repaired);
            state = result.State;
        }
    }

    private sealed record Fixture(string Name, Step[] Steps);
    private sealed record Step(long OccurredAt, TokenUsage? LastUsage, TokenUsage? CumulativeUsage, Expected Expected);
    private sealed record Expected(TokenUsage? Delta, TokenUsage? CumulativeHighWaterMark, long? LastObservedAt,
        string Quality, string? Diagnostic, bool WindowsRepaired, TokenUsage? WindowsDelta);
}
