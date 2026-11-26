using System.Diagnostics;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

var fixedDate = new DateTimeOffset(2020, 11, 19, 13, 27, 15, TimeSpan.FromMinutes(330)).AddTicks(1234567);
if (args[0] == "write")
{
    var count = int.Parse(args[2]);
    var windows = int.Parse(args[3]);
    var rows = ProviderCatalog.All.Take(count).Select((p,i) =>
    {
        TokenUsage tokens = new(12345+i, 190, 82+i, i % 2 == 0 ? 12 : null);
        var local = new LocalUsage("this-pc", i % 3 == 0 ? "partial" : "ready", fixedDate, fixedDate, "synthetic-zone",
            new() { ["month"]=TokenUsage.Zero, ["week"]=tokens, ["today"]=tokens, ["all-time"]=new(long.MaxValue,0,1) });
        var quota = Enumerable.Range(0, windows).Select(n => new LimitWindow("window-" + n, n % 2 == 0 ? "Week 😀" : "창",
            n % 3 == 0 ? 12.35 : null, n % 4 == 0 ? fixedDate : null, 15,
            n % 3 == 1 ? 123456 : null, n % 3 == 2 ? 102 : null, "requests", n % 5 == 0 ? "safe <display>" : null)).ToArray();
        var reading = new ProviderReading(p.Id, ReadingState.Ready, quota, fixedDate, Message: "sample");
        return new CompanionProvider(p.Id, p.Name + " · 예시", i % 5 != 4, local, reading, CachedLimits: i % 4 == 0 ? reading : null);
    }).ToList();
    CompanionFile.Write(new(1, fixedDate, rows), args[1]);
}
else if (args[0] == "read")
{
    var repeat = int.Parse(args[2]);
    long Consume()
    {
        var s = CompanionFile.ReadForCli(args[1]);
        return s.Providers.Aggregate(s.GeneratedAt.UtcTicks, (sum,p) => unchecked(sum * 17 + p.Limits.Windows.Count + (p.LocalUsage?.Totals["today"].InputTokens ?? 0)));
    }
    for (int i=0; i<12; i++) Consume();
    var checksum = 0L;
    using var proc = Process.GetCurrentProcess();
    var cpu = proc.TotalProcessorTime;
    var bytes = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    for (int i=0; i<repeat; i++) checksum=unchecked(checksum + Consume());
    sw.Stop(); var allocated=(GC.GetAllocatedBytesForCurrentThread()-bytes)/repeat;proc.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new{checksum,wallUs=sw.Elapsed.TotalMicroseconds/repeat,
        cpuUs=(proc.TotalProcessorTime-cpu).TotalMicroseconds/repeat,managedBytes=allocated,peakRss=proc.PeakWorkingSet64}));
}
else throw new ArgumentException("Unknown synthetic fixture operation.");

// A .NET test startup hook sets cultures on Windows as well as on Unix. It lives only in
// this synthetic fixture assembly. The production executable has no test culture switch.
internal static class StartupHook
{
    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("CODERIM_QA_CLI_CULTURE") is { } name)
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(name);
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
    }
}
