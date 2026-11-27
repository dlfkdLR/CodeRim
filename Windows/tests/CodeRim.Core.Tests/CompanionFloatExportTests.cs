using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class CompanionFloatExportTests
{
    [Theory]
    [InlineData(-25)]
    [InlineData(-958)]
    public void PublishedSnapshotKeepsDistinctCostAndQuotaValuesAcrossReopen(int exponent)
    {
        var boundary = Math.ScaleB(1, exponent);
        double[] values = [Math.BitDecrement(boundary), boundary, Math.BitIncrement(boundary)];
        var entries = values.SelectMany(value => new[] { value, -value })
            .Select(value => new ProviderCostEntry("2024-02-29", null, null, null, null, null, value, value))
            .ToArray();
        var windows = values.Select((value, index) => new LimitWindow($"quota-{index}", "Quota", value)).ToArray();
        var costs = new ProviderCostUsage("USD", 1, "Credits", null, entries, AllowCredits: true);
        var reading = new ProviderReading("codex", ReadingState.Ready, windows, CostUsage: costs);
        var provider = new CompanionProvider("codex", "Codex", true, null, reading, CachedLimits: reading);
        var directory = Directory.CreateTempSubdirectory("coderim-cost-roundtrip-");
        try
        {
            var path = Path.Combine(directory.FullName, "snapshot.json");
            CompanionFile.Write(new CompanionSnapshot(1, DateTimeOffset.UnixEpoch, [provider]), path);
            var restored = Assert.Single(CompanionFile.Read(path).Providers);
            foreach (var actual in new[] { restored.Limits, restored.CachedLimits! })
            {
                Assert.Equal(values.Select(BitConverter.DoubleToInt64Bits),
                    actual.Windows.Select(window => BitConverter.DoubleToInt64Bits(window.UsedPercent!.Value)));
                var expected = entries.Select(row => BitConverter.DoubleToInt64Bits(row.Cost!.Value)).ToArray();
                Assert.Equal(expected, actual.CostUsage!.Entries.Select(row => BitConverter.DoubleToInt64Bits(row.Cost!.Value)));
                Assert.Equal(expected, actual.CostUsage.Entries.Select(row => BitConverter.DoubleToInt64Bits(row.EstimatedCost!.Value)));
            }
            // Other numbers keep the public serializer's spelling, including the
            // adjacent value that the released CLI printed for the boundary itself.
            var adjacent = Math.BitDecrement(boundary);
            Assert.Equal(JsonSerializer.Serialize(adjacent), JsonSerializer.Serialize(adjacent, CompanionFile.ExportJsonOptions));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void ExactNumbersKeepTheReleasedEightMiBWriteBoundary()
    {
        const int limit = 8 * 1024 * 1024;
        var value = Math.ScaleB(1, -958);
        var reading = new ProviderReading("codex", ReadingState.Ready, [new LimitWindow("quota", "Quota", value)]);
        var provider = new CompanionProvider("codex", "x", true, null, reading);
        var snapshot = new CompanionSnapshot(1, DateTimeOffset.UnixEpoch, [provider]);
        var initialLength = JsonSerializer.SerializeToUtf8Bytes(snapshot, CompanionFile.JsonOptions).Length;
        var directory = Directory.CreateTempSubdirectory("coderim-cost-size-");
        try
        {
            var path = Path.Combine(directory.FullName, "snapshot.json");
            var nameLength = limit - initialLength + provider.Name.Length;
            foreach (var offset in new[] { -1, 0 })
            {
                var item = provider with { Name = new string('x', nameLength + offset) };
                CompanionFile.Write(snapshot with { Providers = [item] }, path);
                Assert.Equal(limit + offset, new FileInfo(path).Length);
                Assert.Equal(value, CompanionFile.Read(path).Providers[0].Limits.Windows[0].UsedPercent);
            }
            var overflow = provider with { Name = new string('x', nameLength + 1) };
            Assert.Throws<InvalidDataException>(() => CompanionFile.Write(snapshot with { Providers = [overflow] }, path));
            Assert.Equal(limit, new FileInfo(path).Length);
            var retained = Assert.Single(CompanionFile.Read(path).Providers);
            Assert.Equal(nameLength, retained.Name.Length);
            Assert.Equal(value, retained.Limits.Windows[0].UsedPercent);
        }
        finally { directory.Delete(true); }
    }

}
