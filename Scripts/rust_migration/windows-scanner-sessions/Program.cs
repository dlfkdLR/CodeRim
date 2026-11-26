using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

var epoch = DateTimeOffset.UnixEpoch;
var cases = new List<object>();
if (args[0] == "scan")
{
    var path = args[1];
    Directory.CreateDirectory(path);
    var rows = Enumerable.Range(0, 720).Select(i => JsonSerializer.Serialize(new {
        type = "assistant", sessionId = "session-" + (i % 32), timestamp = epoch.AddMinutes(31 - i % 32).ToString("O"),
        cwd = "C:/fixture/project" + (i % 5), message = new { id = "message-" + i, role = "assistant",
            model = "claude-sonnet-4-6", usage = new { input_tokens = 7, output_tokens = 2 } } })).ToArray();
    File.WriteAllLines(Path.Combine(path, "parents.jsonl"), rows);
    File.WriteAllLines(Path.Combine(path, "agent-fixture.jsonl"), rows.Take(16));
    var scanner = new UsageScanner("claude", [path], new byte[32]);
    for (var i = 0; i < 2; i++)
    {
        var scan = await scanner.ScanAsync(WeekStart.Monday, CancellationToken.None);
        if (scan.Events.Count != rows.Length || scan.Sessions.Count < 32) throw new InvalidDataException("Synthetic scan omitted sessions");
        cases.Add(new { scan.SourceCount, scan.HasMoreWork, events = scan.Events.Count, tokens = scan.Snapshot.AllTime.TotalTokens,
            sessions = scan.Sessions.Count, digest = Digest(scan.Sessions) });
    }
}
else
{
    string?[] labels = [null, "", "a", "A", "é", "e\u0301", "\uD800", "\uDC00", "📎", "\uE000", "서울", "a\0b"];
    foreach (var (count, distinct, ties) in new[] { (0, 1, false), (1, 1, false), (8, 2, true), (128, 12, true),
        (1024, 256, false), (4096, 8, false), (4096, 4096, true), (65536, 128, false), (65536, 16384, false),
        (65536, 1, true) })
    {
        var ids = Enumerable.Range(0, distinct).Select(i => i == 0 ? null : "s-" + i).ToArray();
        var ev = Enumerable.Range(0, count).Select(i =>
            new UsageEvent("row-" + i, epoch.AddTicks(ties ? 0 : (i * 17L) % 1024).ToOffset(TimeSpan.FromHours(i % 23 - 11)),
                new TokenUsage(1, 0, 0), SessionId: ids[i % distinct]!, ProjectId: i % 5 == 0 ? "unknown" : i % 13 == 0 ? null! : "UNKNOWN",
                Project: labels[i % labels.Length]!, Provider: "claude")
            { ImportParentSessionId = i % 3 == 0 ? labels[(i / 3) % labels.Length] : null }).ToList();
        var result = Project(ev);
        if (result.Length != Math.Min(count, distinct)) throw new InvalidDataException("Incorrect synthetic session cardinality");
        if (args[0] == "contract")
        {
            cases.Add(new { count, distinct, ties, groups = result.Length, digest = Digest(result) });
            ev.Reverse();
            cases.Add(new { count, distinct, ties, groups = result.Length, digest = Digest(Project(ev)) });
            continue;
        }
        for (var i = 0; i < 6; i++) Project(ev);
        var elapsed = new double[11];
        var allocated = new long[11];
        var repeats = Math.Max(1, Math.Min(256, 65536 / Math.Max(1, count)));
        for (int sample = 0; sample < elapsed.Length; sample++)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (int i = 0; i < repeats; i++) result = Project(ev);
            elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / repeats;
            allocated[sample] = (GC.GetAllocatedBytesForCurrentThread() - bytes) / repeats;
        }
        Array.Sort(elapsed);
        Array.Sort(allocated);
        cases.Add(new { count, distinct, ties, groups = result.Length, ns = elapsed[5], bytes = allocated[5] });
    }
}
Console.WriteLine(JsonSerializer.Serialize(cases));

static SessionDetails[] Project(List<UsageEvent> events) =>
#if CODERIM_RUST_ACCOUNTING
    UsageScanner.ClaudeSessions(events);
#else
    Released.Project(events);
#endif

static string Digest(IReadOnlyList<SessionDetails> groups)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    void Text(string? value)
    {
        writer.Write(value?.Length ?? -1);
        if (value is not null) foreach (var codeUnit in value) writer.Write((ushort)codeUnit);
    }
    foreach (var group in groups)
    {
        Text(group.Id);
        Text(group.ParentId);
        Text(group.ProjectName);
        Text(group.StartedAt?.ToString("O", CultureInfo.InvariantCulture));
        Text(group.ProjectObservedAt?.ToString("O", CultureInfo.InvariantCulture));
        writer.Write(group.Attachments.Count);
    }
    return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
}
