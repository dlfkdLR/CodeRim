using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

var utc = TimeZoneInfo.Utc;
var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
var spring = new DateTimeOffset(2025, 3, 12, 12, 30, 0, TimeSpan.Zero);
var autumn = new DateTimeOffset(2025, 11, 5, 12, 30, 0, TimeSpan.Zero);

static UsageEvent[] Events(int count, DateTimeOffset through, int models = 13)
{
    string[] ids = ["", "A", "a", "Café", "Cafe\u0301", "a\u0000b", "한글", "😀"];
    var data = new UsageEvent[count];
    for (int i = 0; i < data.Length; i++)
    {
        var at = through.AddMinutes(-((long)i * 3931 % (31 * 24 * 60)));
        int m = (i * 5) % models;
        string model = m < ids.Length ? ids[m] : $"model-{m}";
        var usage = i % 11 == 0 ? TokenUsage.Zero : new TokenUsage(i * 27L, i % 5, i % 219, i % 19 == 0 ? null : i % 12);
        data[i] = new UsageEvent($"e-{i}",at.ToOffset(TimeSpan.FromHours(i % 17 - 8)),usage,model,SessionId: $"session-{(i*7)%181}")
            { Repaired = i % 89 == 0 };
    }
    return data;
}
static string Snapshot(UsageEvent[] events, DateTimeOffset through, AnalyticsRange range, TimeZoneInfo zone,
    UsageSnapshot? local = null, bool useNullLocal = false)
{
    var frame = new AnalyticsSourceFrame(through, useNullLocal ? null! : local ?? UsageSnapshot.Empty with {Quality = DataQuality.Exact},
        Array.AsReadOnly(events));
    try
    {
        var result = SettingsUsageAnalytics.Build(frame,range,zone);
        // Serialize native DateTimeOffset values including their offsets and every ordered collection.
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result)));
    }
    catch (Exception error) { return error.GetType().FullName + "|" + error.Message; }
}
if (args.Length != 0)
{
    int count = int.Parse(args[0]), repeat = int.Parse(args[1]);
    var input = Events(count,autumn);
    var frame = new AnalyticsSourceFrame(autumn,UsageSnapshot.Empty with {Quality=DataQuality.Exact},Array.AsReadOnly(input));
    long Read()
    {
        var s = SettingsUsageAnalytics.Build(frame, AnalyticsRange.ThirtyDays,eastern);
        return s.Usage.TotalTokens + s.Models.Count * 7 + s.Sessions.Count * 19 + s.Days.Count;
    }
    for (int i=0; i<8; i++) Read();
    long checksum = 0;
    using var process = Process.GetCurrentProcess();
    var cpu = process.TotalProcessorTime;
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew();
    for (int i=0; i<repeat; i++) checksum = unchecked(checksum + Read());
    watch.Stop();
    var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated)/repeat;
    process.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new {checksum, wallUs=watch.Elapsed.TotalMicroseconds/repeat,
        cpuUs=(process.TotalProcessorTime-cpu).TotalMicroseconds/repeat,managedBytes=bytes,peakRss=process.PeakWorkingSet64}));
    return;
}
var cases = new SortedDictionary<string,string>(StringComparer.Ordinal);
void Check(string name, UsageEvent[] events, DateTimeOffset through, AnalyticsRange range,
    TimeZoneInfo zone, UsageSnapshot? local = null, bool useNullLocal = false) =>
    cases.Add(name, Snapshot(events,through,range,zone,local,useNullLocal));
foreach (var (tag,through) in new[]{("spring",spring),("autumn",autumn)})
    foreach (var (zoneName,zone) in new[]{("utc",utc),("eastern",eastern),("kathmandu",TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu"))})
        foreach (var range in Enum.GetValues<AnalyticsRange>())
            foreach (var count in new[]{0,63,64,65,600,5000})
                Check($"{tag}-{zoneName}-{range}-{count}", Events(count,through),through,range,zone);
var rows = Enumerable.Range(0,130).Select(i=>new UsageEvent(i.ToString(),autumn,TokenUsage.Zero,"one",SessionId:"s")).ToArray();
foreach(var q in Enum.GetValues<DataQuality>())
    foreach(var repaired in new[]{false,true})
        foreach(var retains in new[]{false,true})
        {
            var series = (UsageEvent[])rows.Clone();
            series[3] = series[3] with {Usage=new(1,0,0,null),Repaired=repaired};
            var local = UsageSnapshot.Empty with {Quality=q,RetainsPartialHistory=retains};
            Check($"quality-{q}-{repaired}-{retains}",series,autumn,AnalyticsRange.SevenDays,utc,local);
        }
Check("zero-model-retained",rows,autumn,AnalyticsRange.SevenDays,utc);
Check("missing-source",rows,autumn,AnalyticsRange.SevenDays,utc,useNullLocal:true);
var boundaries = new[]{AnalyticsTimeline.Start(AnalyticsRange.SevenDays,autumn,eastern),autumn}.SelectMany(t=>new[]{t.AddTicks(-1),t,t.AddTicks(1)}).ToArray();
var ties = (UsageEvent[])rows.Clone();
for(int i=0; i<ties.Length; i++) ties[i]=ties[i] with {OccurredAt=boundaries[i%boundaries.Length].ToOffset(TimeSpan.FromHours(i%15-7)),Usage=new(i,2,i,i)};
Check("inclusive-range-offset-ties",ties,autumn,AnalyticsRange.SevenDays,eastern);
foreach(var kind in new[]{"max","underflow","null-model","null-session","write-null","repaired-outside","all-invalid-relations","max-day"})
{
    var series=(UsageEvent[])rows.Clone();
    series[0]=kind switch {
       "max"=>series[0] with{Usage=new(long.MaxValue,long.MaxValue,long.MaxValue,long.MaxValue)},
       "underflow"=>series[0] with{Usage=new(long.MinValue,-2,long.MinValue,-1)},
       "null-model"=>series[0] with{Model=null!},
       "null-session"=>series[0] with{SessionId=null!},
       "write-null"=>series[0] with{Usage=new(0,0,0,null)},
       "repaired-outside"=>series[0] with{Repaired=true,OccurredAt=autumn.AddDays(-12)},
       _=> series[0] with{Usage=new(1,123,1,77)} };
    series[1]=series[1] with{Usage=new(14,3,8,2)};
    if(kind=="max-day")
        for(int i=0;i<series.Length;i++)series[i]=series[i] with{OccurredAt=i%2==0?DateTimeOffset.MaxValue:DateTimeOffset.MaxValue.AddTicks(-1)};
    Check(kind,series,kind=="max-day"?DateTimeOffset.MaxValue:autumn,AnalyticsRange.SevenDays,utc);
}
Check("bounds-overflow",rows,DateTimeOffset.MinValue,AnalyticsRange.SevenDays,utc);
Check("unknown-range",rows,autumn,(AnalyticsRange)999,utc);
Check("over-cell-budget",Events(10000,autumn,3509),autumn,AnalyticsRange.ThirtyDays,utc);
foreach(var count in new[]{63,64,65,65_537,UsageScanner.MaximumEventCount,UsageScanner.MaximumEventCount+1})
    Check($"retained-{count}",Enumerable.Repeat(rows[0] with{Usage=new(11,2,3,1)},count).ToArray(),
        autumn,AnalyticsRange.ThirtyDays,utc);
Check("partial-source",rows.Select(e=>e with{Usage=new(1,0,1,0)}).ToArray(),
    autumn,AnalyticsRange.ThirtyDays,utc,UsageSnapshot.Empty with{SourcesPartial=true});
var mixed=(UsageEvent[])rows.Clone();
mixed[0]=mixed[0] with{Usage=new(long.MaxValue,0,0),OccurredAt=autumn};
mixed[1]=mixed[1] with{Usage=new(1,0,0),OccurredAt=autumn.AddTicks(-2)};
mixed[2]=mixed[2] with{Usage=new(-3,0,0),OccurredAt=autumn.AddTicks(-1)};
Check("signed-order-sensitive",mixed,autumn,AnalyticsRange.SevenDays,utc);
Console.WriteLine(JsonSerializer.Serialize(cases));
