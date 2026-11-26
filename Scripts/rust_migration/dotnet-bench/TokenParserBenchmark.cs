using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Parsing;

internal static class TokenParserBenchmark
{
    internal static void Run(string workload)
    {
        const string normal = """{"type":"event_msg","timestamp":"2026-01-02T03:04:05.1234567+09:00","ordinal":8,"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":150,"cached_input_tokens":40,"output_tokens":30},"last_token_usage":{"input_tokens":75,"cached_input_tokens":12,"output_tokens":20}}}}""";
        var rate = normal.Replace("\"info\":", "\"rate_limits\":{\"primary\":{\"used_percent\":12.3,\"window_minutes\":300}},\"info\":", StringComparison.Ordinal);
        string[] lines = workload == "parse-tokens" ? [normal, rate] :
            [normal, rate, normal.Replace("150", "\"150\"", StringComparison.Ordinal),
             """{"type":"session_meta","timestamp":"2026-01-02T03:04:05Z","payload":{"id":"fixture"}}""",
             """{"type":"event_msg","payload":{"type":"task_started"},"timestamp":"2026-01-02T03:04:05Z"}""",
             """{"type":"response_item","payload":{"type":"message","content":[]}}"""];
        var rows = lines.Select(Encoding.UTF8.GetBytes).ToArray();
        long Fold(int repeats)
        {
            long sum = 0;
            for (int i = 0; i < repeats; i++)
                foreach (var row in rows)
                {
                    var parsed = CodexJsonlParser.Parse(row);
                    sum += (int)parsed.Kind + (parsed.Token?.CumulativeUsage?.TotalTokens ?? 0)
                        + (parsed.Token?.LastUsage?.TotalTokens ?? 0) + (parsed.Token?.OccurredAt.UtcTicks % 10000 ?? 0);
                }
            return sum;
        }
        const int repeats = 12000;
        Fold(256);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var checksum = Fold(repeats);
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine(JsonSerializer.Serialize(new { elapsedMs = elapsed.TotalMilliseconds,
            allocatedBytes = allocated, checksum, observations = rows.Length * repeats,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription }));
    }
}
