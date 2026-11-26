using System.Diagnostics;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

static SettingsUsageModel Model(string? id, long input, long cached, long output, string? title = null) =>
    new(id!, title ?? id ?? "Missing model", new(input, cached, output));

static SettingsUsageSnapshot Snapshot(SettingsUsageModel[] models, SettingsUsageModel[][] buckets, TokenUsage usage)
{
    var start = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(9));
    var days = buckets.Select((rows, i) => new SettingsUsageDay(start.AddDays(i), start.AddDays(i+1),
        rows.Aggregate(TokenUsage.Zero, (total, r) => total.Add(r.Usage)), rows)).ToArray();
    return new(AnalyticsRange.ThirtyDays, start.AddDays(days.Length), usage, DataQuality.Exact, days, models, []);
}

static SettingsUsageSnapshot Generated(int seed, int count, int days)
{
    var random = new Random(seed);
    var models = Enumerable.Range(0, count).Select(i => Model("model-"+i, random.Next(100, 10000), 4, random.Next(100, 1000))).ToArray();
    var groups = Enumerable.Range(0, days).Select(_ => models.Where(_ => random.Next(5) != 0)
        .Select(m => Model(m.Id, random.Next(500), random.Next(60), random.Next(100))).ToArray()).ToArray();
    return Snapshot(models, groups, models.Aggregate(TokenUsage.Zero, (sum, m) => sum.Add(m.Usage)));
}

static void Contract()
{
    SettingsUsageModel[] named = [Model("Café",9,1,3),Model("Cafe\u0301",9,1,3),Model("é",5,0,4),
        Model("é",19,0,1,"Duplicate title"),Model(null,15,2,1),Model("",3,0,1),Model("other-models",3,1,0)];
    SettingsUsageModel[] extremes = [Model("dup",long.MaxValue,0,-1),Model("dup",long.MinValue,0,0),
        Model("floor",long.MinValue,1,0),Model("unknown",long.MaxValue,-1,9),Model("other",-17,2,1),
        Model("a",long.MinValue,0,0),Model("b",-1,0,long.MaxValue)];
    var snapshots = new List<SettingsUsageSnapshot> {
        Snapshot([], [], TokenUsage.Zero), Snapshot([], [[],named], new(123,2,1)),
        Snapshot(named,[named.Reverse().ToArray(), [], [Model("unlisted",4,1,4)]],new(long.MinValue,1,5)),
        Snapshot(extremes,[extremes,extremes.Reverse().ToArray()],new(long.MaxValue,-1,6)),
        Snapshot([Model(null,8,0,1),Model(null,2,0,5)],[[Model(null,12,3,1)]],TokenUsage.Zero)
    };
    snapshots.AddRange(Enumerable.Range(0,75).Select(i => Generated(i,i==74 ? 80 : i%15,i%9)));
    foreach (var (snapshot,index) in snapshots.Select((s,i) => (s,i)))
    foreach (var grouping in Enum.GetValues<SettingsUsageGrouping>())
    foreach (var cached in new[]{false,true})
    {
        var project=SettingsUsageAnalytics.CreateSeriesProjection(snapshot,grouping,cached);
        SettingsUsageDay?[] selected=[null,..snapshot.Days];
        var series=selected.Select(day => project(day)).ToArray();
        for(var i=0;i<selected.Length;i++)
            if(!series[i].SequenceEqual(SettingsUsageAnalytics.Series(snapshot,grouping,cached,selected[i])))
                throw new Exception("Normal entry point disagrees with the per-chart projection.");
        Console.WriteLine(JsonSerializer.Serialize(new {index,grouping,cached,series}));
    }
}
static long Consume(SettingsUsageSnapshot s, SettingsUsageGrouping grouping, bool cached)
{
    var project=SettingsUsageAnalytics.CreateSeriesProjection(s,grouping,cached);
    long checksum=0;
    void Add(IReadOnlyList<SettingsUsageSeries> values)
    {
        foreach(var v in values) checksum=unchecked(checksum*31+v.Tokens+v.Title.Length);
    }
    Add(project(null));
    foreach(var day in s.Days) Add(project(day));
    return checksum;
}
if(args.Length==0) Contract();
else
{
    var kind=Enum.Parse<SettingsUsageGrouping>(args[0]);
    var models=int.Parse(args[1]);var days=int.Parse(args[2]);var cycles=int.Parse(args[3]);
    var snaps=Enumerable.Range(0,5).Select(i=>Generated(i,models,days)).ToArray();
    for(var n=0;n<120;n++) Consume(snaps[n%5],kind,n%2==0);
    using var proc=Process.GetCurrentProcess();
    var cpu=proc.TotalProcessorTime;var sw=Stopwatch.StartNew();long checksum=0;
    for(var n=0;n<cycles;n++)checksum=unchecked(checksum+Consume(snaps[n%5],kind,n%2==0));
    sw.Stop();proc.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new {checksum,wallUs=sw.Elapsed.TotalMicroseconds/cycles,
        cpuUs=(proc.TotalProcessorTime-cpu).TotalMicroseconds/cycles,peakRss=proc.PeakWorkingSet64}));
}
