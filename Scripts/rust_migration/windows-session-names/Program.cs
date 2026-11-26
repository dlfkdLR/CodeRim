using System.Diagnostics;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

var clock = DateTimeOffset.UnixEpoch;
string[] projects = ["Unknown project", "", " \t ", "Projects", "é", "e\u0301", "봄", "🌿"];
var samples = new List<object>();
int checkedFrames = 0, checkedNames = 0;
long checksum = 0;
foreach (int size in new[] {0, 1, 64, 4096, 16384}) {
    var source = Enumerable.Range(0, size).Select(i => new UsageEvent($"e{i % 17}", clock.AddHours(i % 23),
        new TokenUsage(1, 0, i % 13), "fixture", projects[i % projects.Length], $"s{i % 256}",
        "codex", i % 3 == 0 ? "unknown" : $"p{i % 19}")).ToArray();
    var sessions = source.GroupBy(e => e.SessionId).Select(g => new SettingsUsageSession(g.Key, TokenUsage.Zero, clock)).ToArray();
    foreach (string provider in new[] {"codex", "claude"})
    foreach (int metadata in new[] {0, 80, 100})
    foreach (bool expanded in new[] {false, true}) {
        var old = new Legacy(provider);
        var current = new Current(provider);
        foreach (var s in sessions.Select((x,i) => (x.Id,i)).Where(y => y.i % 100 < metadata)) {
            if (s.i % 3 == 0) {
                string title = s.i % 2 == 0 ? "" : $"title-{s.Id}";
                old.store.CodexAnalyticsLabels[s.Id] = new(title, "");
                current.store.CodexAnalyticsLabels[s.Id] = new(title, "");
            }
            old.analyticsMetadata[s.Id] = new(null,$"metadata-{s.Id}");
            current.analyticsMetadata[s.Id] = new(null,$"metadata-{s.Id}");
        }
        int shown = expanded ? sessions.Length : Math.Min(5,sessions.Length);
        string[] Original() => sessions.Take(shown).Select(item => old.Name(source.Where(x => x.SessionId == item.Id),item.Id)).ToArray();
        IReadOnlyList<string> Candidate() => AnalyticsEventNames.ForSessions(source, sessions, shown, current.Published);
        if (!Original().SequenceEqual(Candidate(),StringComparer.Ordinal)) throw new InvalidDataException("Session names differ.");
        checkedFrames++; checkedNames += shown;
        if (provider != "codex" || size < 4096) continue;
        var runs = new List<object>();
        long expected = Original().Sum(x => x.Length);
        void Measure(string side, Func<IReadOnlyList<string>> resolve) {
            int repeat = expanded ? 1 : 6;
            long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            long measure = 0;
            for (int i = 0; i < repeat; i++) measure += resolve().Sum(n => n.Length);
            var ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / repeat;
            var bytes = (GC.GetAllocatedBytesForCurrentThread()-allocated) / repeat;
            if(measure != expected*repeat)throw new InvalidDataException("Ranking changed during measurement.");
            runs.Add(new {side,ns,bytes}); checksum+=measure;
        }
        for(int round=0;round<7;round++) {
            if (round%2==0) {Measure("release", Original);Measure("candidate", Candidate);}
            else {Measure("candidate", Candidate);Measure("release", Original);}
        }
        samples.Add(new {size,metadata,shown,runs});
    }
}
var tie = new UsageEvent("z",clock,new TokenUsage(1,0,0),"m","Unknown project","tie","codex","unknown");
var ranked = new[] { new SettingsUsageSession("tie",TokenUsage.Zero,clock) };
var choices = new[] {tie, tie with {Project=" ",OccurredAt=clock.AddDays(5)},tie with {Project="B",ProjectId="z",OccurredAt=clock.AddDays(-6)},
    tie with {Project="𐀀",EventKey="a",ProjectId="a",OccurredAt=clock.AddDays(-6)},
    tie with {Project="\uE000",EventKey="a",ProjectId="a",OccurredAt=clock.AddDays(-6)},
    tie with {Project="outside-range",SessionId="other"},tie with {Project="unrelated null session",SessionId=null!}};
var random = new Random(843451);
for (int i=0;i<512;i++){
    var old = new Legacy("codex"); var selected=choices.OrderBy(_=>random.Next()).Take(1+random.Next(choices.Length)).ToList();
    selected.Add(tie); string expected=old.Name(selected.Where(e=>e.SessionId=="tie"),"tie");
    var actual=AnalyticsEventNames.ForSessions(selected,ranked,1,old.Published);
    if(expected!=actual[0])throw new InvalidDataException("Ordinal or date tie changed.");
    checkedFrames++;checkedNames++;
}
foreach (string provider in new[] {"codex", "claude"}) {
    var old = new Legacy(provider);
    var current = new Current(provider);
    for (int n = 0; n < 256; n++) {
        var rows = choices.OrderBy(_ => random.Next()).Select((e,i) => e with {
            ProjectId = n % 3 == 0 ? "unknown" : e.ProjectId,
            SessionId = $"s{i}", Project = n % 3 == 0 ? "Unknown project" : e.Project,
            OccurredAt = n % 7 == 0 ? clock : e.OccurredAt }).ToArray();
        for (int i = 0; i < rows.Length; i++) {
            string alias = i % 4 == 0 ? "한글" : i % 3 == 0 ? "alias" : $"K{i % 5}";
            old.store.CodexAnalyticsLabels[rows[i].SessionId] = new(null, alias);
            current.store.CodexAnalyticsLabels[rows[i].SessionId] = new(null, alias);
        }
        if (old.Project(rows,"p") != current.Project(rows,"p") || old.Name(rows,"s") != current.Name(rows,"s"))
            throw new InvalidDataException("Project or session detail changed.");
        checkedFrames++; checkedNames+=2;
    }
}
Console.WriteLine(JsonSerializer.Serialize(new {system=System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),checkedFrames,checkedNames,checksum,samples}));
