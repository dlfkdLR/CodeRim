using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRim.Core.Services;

var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
if (args is ["first", var kind, var size])
{
    int count = int.Parse(size, System.Globalization.CultureInfo.InvariantCulture);
    var input = Make(kind, count, 13);
    var before = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    var result = SessionPresentation.Groups(input);
    var us = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine(JsonSerializer.Serialize(new { groups = result.Count, us, bytes }, options));
    return;
}
if (args is ["bench"])
{
    foreach (var (shape, count) in new (string, int)[] { ("flat", 0), ("flat", 1), ("flat", 2), ("flat", 8),
        ("tree", 2), ("tree", 8), ("flat", 64), ("tree", 64), ("chain", 64), ("missing", 64),
        ("flat", 512), ("tree", 512), ("chain", 512), ("chain", 1024) })
    {
        var source = Make(shape, count, 13);
        int rounds = count switch { <= 8 => 300, <= 64 => 30, <= 512 => 4, _ => 2 };
        for (var i = 0; i < 4; i++) _ = SessionPresentation.Groups(source);
        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var groups = 0;
        for (var i = 0; i < rounds; i++) groups += SessionPresentation.Groups(source).Count;
        var us = Stopwatch.GetElapsedTime(start).TotalMicroseconds / rounds;
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - startBytes) / rounds;
        Console.WriteLine(JsonSerializer.Serialize(new { shape, count, groups, us, bytes }, options));
    }
    return;
}
void Check(string name, IReadOnlyList<SessionActivity> input)
{
    try
    {
        var first = new Dictionary<SessionActivity, int>(ReferenceEqualityComparer.Instance);
        if (input is SessionActivity[] source)
            for (int i = 0; i < source.Length; i++) if (source[i] is { } item) first.TryAdd(item, i);
        var groups = SessionPresentation.Groups(input);
        object Row(ActivityDisplayRow row) => new { row.Session, row.Depth, row.ContextOnly, original = first.GetValueOrDefault(row.Session, -1) };
        var result = groups.Select(g => new { parent = Row(g.Parent), children = g.Children.Select(Row) });
        Console.WriteLine(JsonSerializer.Serialize(new { name, groups = groups.Count, rows = groups.Sum(g => 1 + g.Children.Count),
            sha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result, options))) }, options));
    }
    catch (Exception ex)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { name, error = ex.GetType().FullName, ex.HResult, ex.Message }, options));
    }
}
foreach (var shape in new[] { "flat", "tree", "chain", "cyclic", "missing", "mixed" })
    foreach (var count in new[] { 0, 1, 2, 3, 4, 7, 16, 32, 65, 128 })
        for (int seed = 0; seed < 9; seed++) Check($"{shape}-{count}-{seed}", Make(shape, count, seed));
Check("deep", Make("chain", 2048, 1));
var one = Make("flat", 1, 1)[0];
Check("null-item", [null!]);
Check("null-id", [one with { Id = null! }]);
Check("null-state", [one with { State = null! }]);
Check("null-provider", [one with { Provider = null! }]);
Check("sequence-changed-added-parent", new ChangingSessions([one], [one with { Id = "foreign", ParentThreadId = "10000000-abcf-4000-8000-0000000000af" }]));
Check("sequence-changed-parent-exists", new ChangingSessions([one], [one with { Id = "foreign", CodexThreadId = null, ParentThreadId = one.CodexThreadId }]));
Check("sequence-changed-removed", new ChangingSessions([one], []));
Check("duplicates", [one with { CodexThreadId = null }, one, one with { ParentThreadId = "00000000-0000-0000-0000-0000000000AF" }]);

static SessionActivity[] Make(string shape, int count, int seed)
{
    static string Thread(int number) => number.ToString("x8") + "-abcf-4000-8000-0000000000af";
    var random = new Random(seed);
    var sessions = new List<SessionActivity>();
    for (var i = 0; i < count; i++)
    {
        var thread = Thread(i);
        var parent = shape switch
        {
            "chain" when i > 0 => i - 1,
            "tree" when i > 0 => (i - 1) / 3,
            "missing" => count + i / 3,
            "cyclic" => (i + 1) % count,
            "mixed" => random.Next(-2, count + 2),
            _ => -1
        };
        var state = new[] { "waiting", "busy", "idle", "archived" }[(i + seed) % 4];
        string id = (shape == "mixed" ? new[] { "e\u0301", "é", "작업", "😀", "\uD800", "0" }[i % 6] : "task") + (i * 7919 % 100000).ToString("D5");
        var time = DateTimeOffset.UnixEpoch.AddTicks((i + seed) % 7).ToOffset(TimeSpan.FromHours(i % 3 - 1));
        var item = new SessionActivity(id, shape == "mixed" && i % 7 == 0 ? "claude" : "codex", "name" + i, state, time)
        {
            CodexThreadId = thread, ParentThreadId = parent < 0 ? null : (i % 3 == 0 ? Thread(parent).ToUpperInvariant() : Thread(parent)),
            RemoteHostId = shape == "mixed" && i % 3 == 1 ? "host" : null,
            Detail = "detail" + i, ParentThreadTitle = i % 2 == 0 ? null : "parent" + i,
            UsageSessionId = "source" + i, ProcessId = i, ProcessStartedAt = time
        };
        sessions.Add(item);
        if (shape == "mixed" && i % 5 == 0) sessions.Add(item with { Name = "duplicate", ParentThreadId = Thread((i + 1) % count) });
    }
    random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sessions));
    return sessions.ToArray();
}

sealed class ChangingSessions(SessionActivity[] initial, SessionActivity[] second) : IReadOnlyList<SessionActivity>
{
    private int reads;
    public int Count => throw new NotSupportedException();
    public SessionActivity this[int index] => throw new NotSupportedException();
    public IEnumerator<SessionActivity> GetEnumerator() =>
        ((IEnumerable<SessionActivity>)(reads++ == 0 ? initial : second)).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
