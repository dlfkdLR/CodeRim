using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;

namespace CodeRim.UsageCore.Tests;

public sealed class ClaudeUsageContractTests
{
    [Fact]
    public void SharedStreamingAndProvenanceContract()
    {
        using var resource = typeof(ClaudeUsageContractTests).Assembly.GetManifestResourceStream("claude-usage-v1.json")!;
        var contracts = JsonSerializer.Deserialize<Contract[]>(resource, JsonSerializerOptions.Web)!;
        Assert.NotEmpty(contracts);
        Assert.Equal(contracts.Length, contracts.Select(c => c.Name).Distinct().Count());
        foreach (var contract in contracts)
        {
            var events = new Dictionary<string, UsageEvent>();
            foreach (var file in contract.Files)
            {
                var decoder = new UsageLogSession(file.Name, 1000, "claude", new byte[32], false);
                foreach (var line in file.Lines) Assert.True(decoder.Consume(Encoding.UTF8.GetBytes(line), false));
                var decoded = decoder.Complete();
                Assert.False(decoded.ResourceLimitReached);
                foreach (var item in decoded.Events)
                    events[item.EventKey] = events.TryGetValue(item.EventKey, out var old) ? ClaudeJsonlParser.Merge(old, item) : item;
            }
            var expected = contract.Expected;
            var usage = events.Values.Aggregate(TokenUsage.Zero, (sum, e) => sum.Add(e.Usage));
            Assert.True(usage == new TokenUsage(expected.InputTokens, expected.CachedInputTokens,
                expected.OutputTokens, expected.CacheWriteInputTokens), contract.Name);
            if (expected.Projects is { } projects)
                Assert.Equal(projects, events.Values.Select(e => e.Project).Distinct().Order(StringComparer.Ordinal));
            Assert.Equal(expected.Events, events.Count);
            Assert.Equal(expected.Sessions, events.Values.Select(e => e.SessionId).Distinct().Count());
            Assert.Equal(expected.Children, events.Values.Where(e => e.ImportParentSessionId is not null).Select(e => e.SessionId).Distinct().Count());
            Assert.All(events.Values, e => Assert.Equal(DateTimeOffset.Parse("2026-08-31T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture), e.OccurredAt));
        }
    }

    private sealed record Contract(string Name, Source[] Files, Expected Expected);
    private sealed record Source(string Name, string[] Lines);
    private sealed record Expected(long InputTokens, long CachedInputTokens, long CacheWriteInputTokens,
        long OutputTokens, int Events, int Sessions, int Children, string[]? Projects);
}
