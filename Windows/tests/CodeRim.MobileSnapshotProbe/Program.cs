using System.Diagnostics;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

internal static class Program
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_714_688_000_123);
    private static readonly CompanionSnapshot Snapshot = new(1, Now, [
        new("p", "P", true, null, new("p", ReadingState.Ready, [new("daily", "Daily", 22, Now)], Now)),
        new("🪴", "Plant", true, null, new("🪴", ReadingState.Ready, [new("weekly", "Weekly", 0, null)], Now.AddDays(-1))),
        new("off", "Off", false, null, new("off", ReadingState.NeedsAuth, [], null))
    ]);

    private static SessionActivity[] Sessions(int seed, int count)
    {
        var random = new Random(seed);
        string[] states = ["waiting", "busy", "idle", "working", "busy", "pending"];
        string[] names = ["짧은 이름", "e\u0301😀", new string('가', 58) + "😀비밀", new string('0', 61), "a\ud800b", ""];
        string[] providers = ["p", "🪴", "p", "off", "P", "p"];
        return Enumerable.Range(0, count).Select(i => new SessionActivity("hidden-path-" + i,
            providers[random.Next(providers.Length)], names[random.Next(names.Length)] + i,
            states[random.Next(states.Length)], DateTimeOffset.UnixEpoch.AddMilliseconds(random.Next(-4, 5))
                .AddTicks(random.Next(10000)).ToOffset(TimeSpan.FromHours(i % 13)))).ToArray();
    }

    public static void Main(string[] args)
    {
        if (args is ["--bench"]) { Benchmark(); return; }
        if (args.Length != 0) throw new ArgumentException("Expected no arguments or --bench.");
        foreach (int count in new[] { 0, 1, 8, 63, 64, 65, 97, 200, 1536 })
        foreach (int seed in Enumerable.Range(0, 11))
        foreach (bool share in new[] { false, true })
            Console.WriteLine(JsonSerializer.Serialize(MobileSnapshotBuilder.Create(Snapshot, Sessions(seed, count), share, Now)));
    }

    private static void Benchmark()
    {
        int checksum = 0;
        var samples = new List<object>();
        foreach (int count in new[] { 0, 1, 8, 64, 512, 4096 })
        foreach (bool share in new[] { false, true })
        {
            var sessions = Sessions(111, count);
            int iterations = count switch { 4096 => 30, 512 => 100, 64 => 300, _ => 1200 };
            for (int i = 0; i < Math.Min(iterations, 50); i++)
                checksum += MobileSnapshotBuilder.Create(Snapshot, sessions, share, Now).Sessions.Count;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
                checksum += MobileSnapshotBuilder.Create(Snapshot, sessions, share, Now).Sessions.Count;
            var duration = Stopwatch.GetElapsedTime(start);
            samples.Add(new { count, share, iterations, ns = duration.TotalNanoseconds / iterations,
                bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { checksum, samples }));
    }
}
