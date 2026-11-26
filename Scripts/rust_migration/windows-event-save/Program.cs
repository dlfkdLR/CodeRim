using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var database = args[0];
var ledger = new UsageRepository(database);
var operation = args[1];
var provider = args[2];
var stage = int.Parse(args[3]);
var count = int.Parse(args[4]);
var first = new DateTimeOffset(2026, 11, 24, 4, 30, 0, TimeSpan.Zero);
var events = Enumerable.Range(0, count).Select(i => {
    var id = i % 13 == 12 ? i - 1 : i;
    var usage = (i % 29) switch {
        0 => new TokenUsage(-3, -1, -2, -1),
        1 => new TokenUsage(14, 20, 31, 4),
        2 => new TokenUsage(18, 13, 2, 90),
        3 => new TokenUsage(long.MaxValue, long.MaxValue - 2, 1, 3),
        _ => new TokenUsage(107 + stage, i % 15, i % 12, i % 3 == 0 ? null : (long?)(stage + 1)),
    };
    var text = new[] {"", "é", "e\u0301", "🪐", "공유"};
    return new UsageEvent($"evt{id}", first.AddMilliseconds(i - stage), usage, text[i % 5],
        text[(i + stage) % 5], $"s{i % 4}", i % 17 == 16 ? "other" : provider, i % 3 == 0 ? "unknown" : $"p{i % 4}") {
        Repaired = i % 31 == 4, PricingContext = i % 3 == 0 ? null : (PricingContext)(i % 4),
        ImportParentSessionId = i % 9 == 8 ? "parent" : null,
    };
}).ToArray();
var sessions = new[] { new SessionDetails("s1", null, [new AttachmentObservation("img", first, stage + 1)]) };
if (operation == "bench") {
    var warmup = new UsageRepository(database + ".warm");
    warmup.Merge(provider, events.Take(64), sessions);
}
IReadOnlyList<UsageEvent> result;
var before = GC.GetAllocatedBytesForCurrentThread();
var clock = Stopwatch.GetTimestamp();
try {
    result = operation switch {
        "read" => ledger.Read(provider),
        "rebuild" => ledger.Rebuild(provider, events, sessions),
        "merge" or "bench" => ledger.Merge(provider, events, sessions),
        _ => throw new ArgumentException("Unknown operation"),
    };
}
catch (SqliteException error) {
    Console.WriteLine(JsonSerializer.Serialize(new { error.SqliteErrorCode, error.SqliteExtendedErrorCode, error.Message }));
    return;
}
catch (InvalidDataException error) {
    Console.WriteLine(JsonSerializer.Serialize(new {error.Message}));
    return;
}
var ns = Stopwatch.GetElapsedTime(clock).TotalNanoseconds;
var allocation = GC.GetAllocatedBytesForCurrentThread() - before;
if (operation == "bench") {
    Console.WriteLine(JsonSerializer.Serialize(new { ns, allocation, count = result.Count, repairs = ledger.LastWriteRepairedEvents }));
} else {
    Console.WriteLine(JsonSerializer.Serialize(new { rows = result.Select(row => new { row, row.Repaired, row.PricingContext }),
        repairs = ledger.LastWriteRepairedEvents, sessions = ledger.ReadSessionDetails(provider) }));
}
