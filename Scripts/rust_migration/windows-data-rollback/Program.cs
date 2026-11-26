using System.Runtime.InteropServices;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

if (args.Length != 3 || args[0] is not ("seed" or "merge" or "read"))
    throw new ArgumentException("Expected seed|merge|read, synthetic database and source directory.");

var (operation, database, sources) = (args[0], args[1], args[2]);
var repository = new UsageRepository(database);
if (repository.IsNewerSchema)
    throw new InvalidDataException("Unsupported fixture schema.");

var at = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
if (operation == "seed")
{
    // Populate the same auxiliary tables as ordinary resets and session imports.
    repository.Merge("claude", [new("retired-fixture-event", at.AddDays(-2), new(8, 0, 1), Provider: "claude")]);
    repository.Clear("claude", at.AddDays(-1));
    repository.Merge("codex", [new UsageEvent("marked-fixture", at.AddDays(-3), new(2, 0, 1),
        SessionId: "prior-session") { Repaired = true, PricingContext = PricingContext.HighContext }],
        [new SessionDetails("prior-session", "prior-parent", [new("fixture-attachment", at.AddDays(-3), 2)])
            { StartedAt = at.AddDays(-3), ProjectName = "Mock project", ProjectObservedAt = at.AddDays(-3) }]);
}
if (operation != "read")
{
    var scanner = new UsageScanner("codex", [sources], projectKey: new byte[32], requireCompleteSources: true);
    var scanned = await scanner.ScanAsync(WeekStart.Monday, CancellationToken.None);
    if (scanned.HasMoreWork || scanned.Events.Count == 0)
        throw new InvalidDataException("The synthetic log was not fully scanned.");
    repository.Merge("codex", scanned.Events, scanned.Sessions);
}

var events = repository.Read("codex");
var snapshot = UsageAggregation.Aggregate(events, at.AddDays(1), WeekStart.Monday, false, TimeZoneInfo.Utc);
var mode = typeof(UsageAggregation).Assembly.GetType("CodeRim.Core.Services.RustUsageAggregation") is null
    ? "stable" : "candidate";
Console.WriteLine(JsonSerializer.Serialize(new {
    mode, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    schema = UsageRepository.SchemaVersion, signedTotal = snapshot.AllTime.TotalTokens,
    events = events.OrderBy(e => e.EventKey, StringComparer.Ordinal).Select(e => new {
        e.EventKey, e.OccurredAt, e.Usage, e.Model, e.Project, e.SessionId, e.Provider, e.ProjectId,
        e.Repaired, e.PricingContext }),
    sessions = repository.ReadSessionDetails("codex").OrderBy(s => s.Id, StringComparer.Ordinal),
    retainedRepair = repository.HasRepairedEventsSince("codex", at.AddDays(-4))
}));
