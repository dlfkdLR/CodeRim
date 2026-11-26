using System.Diagnostics;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

var now = DateTimeOffset.UnixEpoch;
SessionActivity Session(string id, string? origin, string provider = "codex", string? host = null) =>
    new(id, provider, id, "busy", now) { UsageSessionId = origin, RemoteHostId = host };
UsageEvent Event(string key, string node, TokenUsage usage, string provider = "codex") =>
    new(key, now, usage, SessionId: node, Provider: provider);
SessionDetails Link(string child, string? parent) => new(child, parent, []);

Fixture Generate(int seed, int rootCount, int children, int eventCount, bool sparse)
{
    var random = new Random(seed);
    var roots = Enumerable.Range(0, rootCount).Select(i => Session("Display-" + i, "s-" + i)).ToArray();
    var links = new List<SessionDetails>();
    for (int i = 0; i < rootCount; i++)
        for (int j = 0; j < children; j++)
            links.Add(Link($"c-{i}-{j}", j == 0 ? $"s-{i}" : $"c-{i}-{j-1}"));
    if (rootCount > 1 && children > 1)
    {
        links.Add(Link("c-0-0","s-1")); links.Add(Link("c-0-0","c-0-1")); links.Add(Link("c-0-0","s-1"));
    }
    var events = new List<UsageEvent>();
    for (int i = 0; i < eventCount; i++)
    {
        var n = random.Next(Math.Max(1, rootCount));
        var node = children > 0 && i % 3 != 0 ? $"c-{n}-{random.Next(children)}" : $"s-{n}";
        if (sparse && i % 19 != 0) node = "unowned";
        var count = random.Next(250);
        events.Add(Event((i / 5 * 5 + Math.Min(i % 5, 3)).ToString(), node, new(count, count/3, i%21, i%4 == 0 ? count/3 : null),
            i % 17 == 0 ? "claude" : "codex"));
    }
    return new(roots, events, links);
}

IReadOnlyDictionary<string, long> Project(Fixture f, CancellationToken token = default) =>
    SessionPresentation.TokenTotals(f.Sessions, "codex", f.Events, f.Links, token);

if (args.Length == 0)
{
    var good = new TokenUsage(10, 3, 2);
    var cases = new List<Fixture> { new([], [], []),
        new([Session("a", "Aa"), Session("b", "aa"), Session("remote", "Aa", host: "ssh"), Session("child", "shared"),
             Session("claude", "Aa", "claude"), Session("empty", null)],
            [Event("first", "none", good), Event("first", "unowned-child", good), Event("first", "aa", good), Event("first","Aa",good),
             Event("second","shared",TokenUsage.Zero), Event("x","loop",good), Event("x","Aa",new(-1,0,0))],
            [Link("shared","Aa"), Link("shared","aa"), Link("loop","shared"), Link("shared","loop"),Link("unowned-child","none")]),
        new([Session("second","s"),Session("first","f"),Session("second","f")],
            [Event("a","f",good),Event("b","s",good)], []),
        new([Session("é", "é"),Session("e\u0301", "e\u0301"),Session("case","Case")],
            [Event("k","e\u0301",good),Event("K","é",TokenUsage.Zero),Event("x","case",good)], []),
        new([Session("all child","a")],[Event("x","a",good)],[Link("a","b"),Link("b","a")])
    };
    foreach (var usage in new TokenUsage[] { new(long.MinValue,0,0), new(1,-1,0), new(1,0,-1), new(1,2,0),
        new(2,1,1,-1), new(2,1,1,2), new(long.MaxValue,0,1), new(long.MaxValue,0,0), new(0,0,0), new(0,0,0,0) })
        cases.Add(new([Session("root","one"),Session("other","two"),Session("unmeasured","absent")],
            [Event("1","two",TokenUsage.Zero),Event("2","one",usage),Event("3","one",good)], []));
    for (int i = 0; i < 90; i++)
        cases.Add(Generate(i, (i%11) switch { 0 => 65, 1 => 64, 2 => 63, _ => i%14 }, i%6, (i%3) switch { 0 => 255, 1 => 256, _ => 403 }, i%2 == 0));
    // A corrupt owner on either side of the ABI block boundary must stay suppressed.
    foreach (var n in new[] {255, 256, 257, 511, 512, 513})
    {
        var rows = Enumerable.Range(0, n).Select(i => Event("event" + i, "root", good)).ToList();
        rows.Add(Event("invalid", "root", new(1, 2, 0)));
        rows.Add(Event("zero", "other", TokenUsage.Zero));
        cases.Add(new([Session("a", "root"), Session("b", "other")], rows, []));
    }
    foreach (var f in cases)
        Console.WriteLine(JsonSerializer.Serialize(Project(f).ToArray())); // Preserve actual dictionary enumeration order.
    var history = Generate(1, 3, 8, 700, false);
    foreach (var at in new[] {"metadata", "event before block", "event after block"})
    {
        using var midRead = new CancellationTokenSource();
        var watched = at == "metadata"
            ? history with { Links = new CancellingList<SessionDetails>(history.Links, midRead, 7) }
            : history with { Events = new CancellingList<UsageEvent>(history.Events, midRead, at == "event before block" ? 100 : 645) };
        try { Project(watched, midRead.Token); throw new Exception("Missing history cancellation."); }
        catch (OperationCanceledException ex) when (ex.CancellationToken == midRead.Token) { Console.WriteLine(at + " cancelled"); }
    }
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { Project(cases[0], cancelled.Token); throw new Exception("Missing cancellation."); }
    catch (OperationCanceledException ex) when (ex.CancellationToken == cancelled.Token) { Console.WriteLine("cancelled"); }
}
else
{
    var f = Generate(907, int.Parse(args[0]), int.Parse(args[1]), int.Parse(args[2]), bool.Parse(args[3]));
    var repeats = int.Parse(args[4]); long checksum = 0;
    using var lifetime = new CancellationTokenSource();
    long Consume() => Project(f, lifetime.Token).Aggregate(0L, (total, entry) => unchecked(total * 17 + entry.Value + entry.Key.Length));
    for (int i = 0; i < 32; i++) Consume();
    using var process = Process.GetCurrentProcess();
    var cpu = process.TotalProcessorTime;
    var clock = Stopwatch.StartNew();
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < repeats; i++) checksum = unchecked(checksum + Consume());
    var managedBytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / repeats;
    clock.Stop(); process.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new { checksum, cpuUs = (process.TotalProcessorTime-cpu).TotalMicroseconds/repeats,
        wallUs=clock.Elapsed.TotalMicroseconds/repeats, managedBytes, peakRss=process.PeakWorkingSet64 }));
}

record Fixture(IReadOnlyList<SessionActivity> Sessions, IReadOnlyList<UsageEvent> Events, IReadOnlyList<SessionDetails> Links);


// Simulate the real dashboard cancelling a captured history while it is being enumerated.
sealed class CancellingList<T>(IReadOnlyList<T> source, CancellationTokenSource lifetime, int at) : IReadOnlyList<T>
{
    public int Count => source.Count;
    public T this[int i] => source[i];
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            if (i == at) lifetime.Cancel();
            yield return source[i];
        }
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
