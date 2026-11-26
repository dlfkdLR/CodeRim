using System.Text;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class MobileSessionOrderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_714_688_000_123);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(511)]
    public void ExportKeepsStableTiesAndOnlyOptedInUnicodeTitles(int count)
    {
        var providers = Enumerable.Range(0, 101).Select(i => new CompanionProvider($"p{i}", $"Provider {i}", true, null,
            new($"p{i}", ReadingState.Ready, [], Now))).Prepend(new("off", "Off", false, null, new("off", ReadingState.Ready, [], Now))).ToArray();
        var source = new CompanionSnapshot(1, Now, providers);
        var empty = MobileSnapshotBuilder.Create(source, [], false, Now);
        var states = new[] { "waiting", "busy", "idle", "working", "WAITING", "" };
        var ids = new[] { "off", "p100", "p0", "p99", "P99", "p10" };
        string[] titles = [new string('a', 58) + "😀🙌!", "é" + new string('가', 58) + "e\u0301", "bad\ud800text",
            "bad\udc00", "😀 short", new string('x', 60) + "\ud800", ""];
        var sessions = Enumerable.Range(0, count).Select(i => new SessionActivity($"local-{i}",
            ids[(i * 7 + i / 6) % ids.Length], $"{i}-{titles[i % titles.Length]}", states[(i * 5 + i / 7) % states.Length],
            DateTimeOffset.FromUnixTimeMilliseconds((i % 4 - 2) * 1234).AddTicks(i % 9999))).ToArray();
        foreach (bool share in new[] { false, true })
        {
            var actual = MobileSnapshotBuilder.Create(source, sessions, share, Now);
            var projected = sessions.Where(s => empty.Providers.Any(p => p.Id == s.Provider))
                .Select(s => new MobileSession(s.Provider, s.State switch
                    { "waiting" => "waiting", "busy" => "working", "idle" => "idle", _ => "unavailable" },
                    share ? string.Concat(s.Name.EnumerateRunes().Take(60)) : "", s.Since.ToUnixTimeMilliseconds() / 1000d))
                .OrderBy(s => s.Phase switch { "waiting" => 0, "working" => 1, "idle" => 2, _ => 3 })
                .ThenByDescending(s => s.Since).Take(64).ToArray();
            Assert.Equal(projected, actual.Sessions.ToArray());
            Assert.Equal(empty.GeneratedAt, actual.GeneratedAt);
            Assert.Equal(empty.Providers.Select(p => p.Id), actual.Providers.Select(p => p.Id));
        }
    }

    [Fact]
    public void MillisecondTiesAtTheLimitKeepInputOrderAcrossTimeZoneOffsets()
    {
        var snapshot = new CompanionSnapshot(1, Now, [new("p", "P", true, null, new("p", ReadingState.Ready, [], Now))]);
        var sessions = Enumerable.Range(0, 70).Select(i => new SessionActivity("unused", "p", $"동점 {i}", "waiting",
            DateTimeOffset.UnixEpoch.AddTicks(-9000 + i).ToOffset(TimeSpan.FromHours(i % 13)))).ToArray();
        var actual = MobileSnapshotBuilder.Create(snapshot, sessions, true, Now);
        Assert.Equal(sessions.Take(64).Select(s => s.Name), actual.Sessions.Select(s => s.Title));
        Assert.All(actual.Sessions, s => Assert.Equal(-0.001, s.Since));
    }
}
