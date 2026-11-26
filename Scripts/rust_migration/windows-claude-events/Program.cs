using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;
using CodeRim.Core.Services;

var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
var secondKey = new byte[32];
var dir = args[1];

if (args[0] == "cases")
{
    foreach (var encoded in File.ReadLines(Path.Combine(dir, "rows.txt")))
    {
        var bytes = Convert.FromBase64String(encoded);
        foreach (var (projectKey, source) in new[] { (key, "main"), (secondKey, "agent-fixture") })
        {
            try
            {
                var e = ClaudeJsonlParser.Parse(bytes, projectKey, source);
                Console.WriteLine(JsonSerializer.Serialize(e));
            }
            catch (Exception error)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { exception = error.GetType().FullName, error.HResult, error.Message }));
            }
        }
    }
}
else if (args[0] == "scan")
{
    foreach (var strict in new[] { false, true })
    {
        var scanner = new UsageScanner("claude", [Path.Combine(dir, "logs")], key, strict);
        var result = await scanner.ScanAsync(WeekStart.Monday, CancellationToken.None);
        if (result.Events.Count != 2048) throw new InvalidDataException("Expected every synthetic message");
        Console.WriteLine(JsonSerializer.Serialize(new { result.Events, result.Sessions, result.SourceCount,
            result.HasMoreWork, result.Snapshot.Quality, result.Snapshot.AllTime }));
    }
}
else
{
    var results = new List<object>();
    foreach (var file in Directory.GetFiles(Path.Combine(dir, "bench")).Order(StringComparer.Ordinal))
    {
        var bytes = File.ReadAllBytes(file);
        var iterations = bytes.Length > 8192 ? 96 : 384;
        for (int i = 0; i < 24; i++) ClaudeJsonlParser.Parse(bytes, key);
        var elapsed = new double[9];
        var allocated = new long[9];
        int accepted = 0;
        for (int sample = 0; sample < elapsed.Length; sample++)
        {
            var allocatedPreviously = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            accepted = 0;
            for (int i = 0; i < iterations; i++)
                if (ClaudeJsonlParser.Parse(bytes, key) is not null) accepted++;
            elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
            allocated[sample] = (GC.GetAllocatedBytesForCurrentThread() - allocatedPreviously) / iterations;
        }
        results.Add(new { name = Path.GetFileNameWithoutExtension(file), accepted, elapsed, allocated });
    }
    for (int i = 0; i < 5; i++)
    {
        var scanner = new UsageScanner("claude", [Path.Combine(dir, "logs")], key);
        var start = Stopwatch.GetTimestamp();
        var scan = await scanner.ScanAsync(WeekStart.Monday, CancellationToken.None);
        results.Add(new { name = "scan", count = scan.Events.Count, elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
    }
    Console.WriteLine(JsonSerializer.Serialize(results));
}
