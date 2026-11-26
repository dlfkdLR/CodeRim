using System.Diagnostics;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

var ledger = new UsageRepository(args[1]);
var day = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
string[] models = ["gpt-6-astra", "gpt-5.6", "unknown-사용량", "gpt-5.6-sol", "gpt-5.5", "gpt-4o", "e\u0301", "é"];
UsageEvent Row(int i, string model, TokenUsage usage, PricingContext? context) =>
    new($"evt-{i}", day.AddMinutes(i), usage, model, $"project {i % 7}", $"session-{i % 11}", "codex", $"p{i % 7}") { PricingContext = context };
UsageEvent Ordinary(int i, string model) => Row(i, model, new TokenUsage(i + 14, i % 11, i % 31, i % 3),
    i % 3 == 0 ? PricingContext.HighContext : PricingContext.Standard);
if (args[0] == "seed") {
    ledger.Merge("codex", Enumerable.Range(0, 4096).Select(i => Ordinary(i, models[i % models.Length])), []);
    return;
}
var persisted = ledger.Read("codex").ToArray();
if (persisted.Length != 4096) throw new InvalidDataException("Incomplete synthetic SQLite fixture.");

if (args[0] == "bench") {
    var workloads = new List<(string name, Func<CostSummary> run)>();
    foreach (int count in new[] { 0, 1, 31, 32, 128, 1024, 4096 }) {
        var rows = persisted.Take(count).ToArray();
        var single = rows.Select(e => e with { Model = "gpt-6-astra" }).ToArray();
        workloads.Add(($"one-model-{count}", () => UsageAnalytics.Estimate(single)));
        if (count >= 128) workloads.Add(($"sqlite-mixed-{count}", () => UsageAnalytics.Estimate(rows)));
    }
    var manyModels = persisted.Select((e, i) => e with { Model = $"unknown-{i}" }).ToArray();
    workloads.Add(("distinct-unpriced-4096", () => UsageAnalytics.Estimate(manyModels)));
    var sessions = persisted.GroupBy(e => e.SessionId, StringComparer.Ordinal).ToArray();
    workloads.Add(("sqlite-session-list-4096", () => {
        CostSummary? last = null;
        foreach (var session in sessions) last = UsageAnalytics.Estimate(session);
        return last!;
    }));
    var samples = new List<object>();
    decimal check = 0;
    foreach (var (name, run) in workloads) {
        for (int i = 0; i < 12; i++) _ = run();
        int repeats = name.EndsWith("4096", StringComparison.Ordinal) ? 20 : 160;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (int i = 0; i < repeats; i++) { var result = run(); check += result.Amount ?? result.ExcludedTokens; }
        var ns = Stopwatch.GetElapsedTime(started).TotalNanoseconds / repeats;
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / repeats;
        samples.Add(new {name, ns, bytes});
    }
    Console.WriteLine(JsonSerializer.Serialize(new {check, samples}));
    return;
}

var excludes = new HashSet<string>(["gpt-5.6", "e\u0301"], StringComparer.Ordinal);
void Record(IEnumerable<UsageEvent> rows) {
    Console.WriteLine(JsonSerializer.Serialize(new[] { UsageAnalytics.Estimate(rows), UsageAnalytics.Estimate(rows, excludes) }));
}
Record(persisted);
foreach (var g in persisted.GroupBy(e => e.SessionId)) Record(g);
foreach (var g in persisted.GroupBy(e => e.ProjectId)) Record(g);
foreach (var a in new[] {"session", "project", "model", "day"})
    Console.WriteLine(JsonSerializer.Serialize(UsageAnalytics.Group(persisted, a)));
foreach (var range in Enum.GetValues<AnalyticsRange>())
    Console.WriteLine(JsonSerializer.Serialize(AnalyticsTimeline.Build(persisted, range, day.AddDays(3), TimeZoneInfo.Utc)));
foreach (int size in new[] {511, 512, 513, 1024, 4096})
foreach (int distinct in new[] {1, 8, 64, 512, 4096}) {
    var rows = Enumerable.Range(0, size).Select(i =>
        Ordinary(i, i % distinct < models.Length ? models[i % distinct] : $"모델-{i % distinct}")).ToArray();
    Record(rows);
    Record(rows.Reverse().ToArray());
    foreach (var usage in new[] {TokenUsage.Zero, new TokenUsage(1, 0, 0, 0), new TokenUsage(-1, 0, 0, 0)}) {
        rows[size / 2] = rows[size / 2] with {Model = null!, Usage = usage};
        try { Record(rows); }
        catch (ArgumentException error) { Console.WriteLine(JsonSerializer.Serialize(new {Type = error.GetType().Name, error.ParamName})); }
    }
}
foreach (var n in new[] {0, 1, 31, 32, 33, 127, 128, 129, 256, 257, 4096})
foreach (var model in models)
foreach (var scenario in Enumerable.Range(0, 15)) {
    var rows = Enumerable.Range(0, n).Select(i => {
        var std = Ordinary(i,model);
        if (i != n / 2 && i != n - 1) return scenario == 10 ? std with {Usage = TokenUsage.Zero} :
            scenario == 12 ? std with {PricingContext = PricingContext.HighContext} : std;
        var (u, context) = scenario switch {
            0 => (new TokenUsage(272_000,1,5,1),(PricingContext?)null),
            1 => (new TokenUsage(272_001,1,5,1),null),
            2 => (new TokenUsage(272_001,1,5,1),PricingContext.Standard),
            3 => (new TokenUsage(10,0,0,null),PricingContext.HighContext),
            4 => (new TokenUsage(10,-1,1,0),PricingContext.Standard),
            5 => (new TokenUsage(long.MaxValue, long.MaxValue, long.MaxValue,0),PricingContext.HighContext),
            6 => (new TokenUsage(long.MinValue,1,-1,null),PricingContext.Standard),
            7 => (new TokenUsage(0,0,0,null), (PricingContext)7),
            8 => (new TokenUsage(10,4,10,7),PricingContext.HighContext),
            9 => (new TokenUsage(10,4,10,6),(PricingContext)(-1)),
            10 => (TokenUsage.Zero,null),
            11 => (new TokenUsage(long.MaxValue,0,long.MaxValue,long.MaxValue),PricingContext.HighContext),
            12 => (new TokenUsage(100,0,30,10),PricingContext.HighContext),
            13 => (new TokenUsage(0,0,10,null), PricingContext.Standard),
            _ => (new TokenUsage(10,12,3,0),null),
        };
        return std with {Usage = u, PricingContext = context};
    }).ToArray();
    Record(rows);
}
