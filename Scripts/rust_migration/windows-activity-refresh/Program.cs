using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRim.Core.Services;

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
Options OptionsFor(int flags) => new((flags & 1) != 0, (flags & 2) != 0, "finished-fixture", "blocked-fixture");
if (args is ["runtime"])
{
    Console.WriteLine(JsonSerializer.Serialize(new { os = RuntimeInformation.OSDescription, isa = RuntimeInformation.ProcessArchitecture.ToString(),
        framework = RuntimeInformation.FrameworkDescription }, json));
    return;
}
if (args is ["bench", ..] || args is ["first", ..])
{
    var counts = args.Length > 1 ? new[] { int.Parse(args[1]) } : new[] { 0, 1, 2, 8, 32, 64, 256, 1024 };
    foreach (int n in counts)
    foreach (var shape in args.Length > 2 ? [args[2]] : new[] { "unchanged", "reloaded", "reordered", "completed" })
    {
        var (old, next) = Frames(n, shape, 13);
        var store = new Store(old, OptionsFor(1));
        var rounds = args[0] == "first" ? 1 : n switch { <= 8 => 300, <= 64 => 50, <= 256 => 6, _ => 2 };
        if (args[0] != "first")
            for (int i = 0; i < 5; i++) { store.UpdateSessionActivity(next); store.UpdateSessionActivity(old); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
        {
            store.UpdateSessionActivity(next);
            if (args[0] != "first") store.UpdateSessionActivity(old);
        }
        var calls = rounds * (args[0] == "first" ? 1 : 2);
        var us = Stopwatch.GetElapsedTime(start).TotalMicroseconds / calls;
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / calls;
        Console.WriteLine(JsonSerializer.Serialize(new { shape, n, calls, us, bytes, returned = store.Sessions.Count }, json));
    }
    return;
}

void Check(string key, SessionActivity[] old, IReadOnlyList<SessionActivity> next, int flags, string action)
{
    var store = new Store(old, OptionsFor(flags));
    List<string> callbacks = [];
    int nested = 0;
    var actualInput = next as SessionActivity[] ?? [];
    string Identity(SessionActivity? row)
    {
        if (row == null) return "nil";
        var i = Array.FindIndex(actualInput, x => ReferenceEquals(x, row));
        var p = Array.FindIndex(old, x => ReferenceEquals(x, row));
        return i + "/" + p;
    }
    void Callback(string name)
    {
        callbacks.Add(name + "@" + store.Sessions.Count);
        if (action == name + "-throw") throw new InvalidOperationException("fixture " + name);
        if (action == name + "-reenter" && nested++ == 0) store.UpdateSessionActivity(Array.Empty<SessionActivity>());
    }
    if (action != "no-listeners")
    {
        store.Published += () => Callback("changed");
        store.SessionAttentionRequested += row =>
        {
            callbacks.Add("selected:" + Identity(row) + "/" + row.Name + "/" + row.Since.ToString("O"));
            Callback("attention");
        };
    }
    SessionChime.Played = sound => { callbacks.Add("sound:" + sound); Callback("chime"); };
    string? error = null;
    try { store.UpdateSessionActivity(next); }
    catch (Exception ex) { error = ex.GetType().FullName + "|" + ex.HResult + "|" + ex.Message; }
    finally { SessionChime.Played = null; }
    // Serialize input-reference positions separately from the record to distinguish copy-with from reuse.
    // Preserve all UTF-16 code units even where the JSON serializer substitutes U+FFFD.
    static int[]? Units(string? value) => value?.Select(c => (int)c).ToArray();
    var rows = store.Sessions.Select(item => item is null ? null : new { item, identity = Identity(item), idUnits = Units(item.Id),
        providerUnits = Units(item.Provider), stateUnits = Units(item.State), since = item.Since.ToString("O") });
    var encoded = JsonSerializer.SerializeToUtf8Bytes(new { callbacks, error, sameCollection = ReferenceEquals(next, store.Sessions), rows }, json);
    Console.WriteLine(JsonSerializer.Serialize(new { key, sha256 = Convert.ToHexString(SHA256.HashData(encoded)), rows = store.Sessions.Count,
        error, sounds = callbacks.Count(x => x.StartsWith("sound:")), attentions = callbacks.Count(x => x.StartsWith("selected:")), changed = callbacks.Count(x => x.StartsWith("changed@")) }, json));
}
foreach (var size in new[] { 0, 1, 2, 5, 16, 64 })
    foreach (var shape in new[] { "unchanged", "reloaded", "reordered", "completed", "duplicate", "mixed" })
        for (int seed = 0; seed < 10; seed++)
        {
            var (old, next) = Frames(size, shape, seed);
            var action = new[] { "ordinary", "no-listeners", "attention-reenter", "chime-reenter", "attention-throw", "chime-throw", "changed-throw" }[seed % 7];
            for (int sound = 0; sound < 4; sound++) Check($"{shape}/{size}/{seed}/{sound}", old, next, sound, action);
        }
var single = new SessionActivity("id", "claude", "old", "busy", DateTimeOffset.UnixEpoch);
foreach (var old in new SessionActivity[][] { [], [null!], [single], [single, null!] })
    foreach (var current in new SessionActivity[][] { [], [null!], [single], [single with { State = "waiting" }], [null!, single] })
        Check($"null/{old.Length}/{current.Length}/{old.FirstOrDefault()?.Name}/{current.FirstOrDefault()?.State}", old, current, 3, "ordinary");
Check("custom", [single], new OddCollection([single with { State = "idle" }]), 3, "ordinary");
Check("throws-during-enumeration", [single], new OddCollection([single], true), 3, "ordinary");
Check("zero-to-nonnil",[ ],[single],3,"ordinary");
var embedded = single with { Id = "e\u0301", State = "frozen", ProcessStartedAt = DateTimeOffset.UnixEpoch };
Check("arbitrary-state-same-instant", [embedded], [embedded with { Since = embedded.Since.AddHours(8), ProcessStartedAt = embedded.ProcessStartedAt.GetValueOrDefault().ToOffset(TimeSpan.FromHours(-8)) }], 3, "ordinary");
Check("arbitrary-state-different", [embedded], [embedded with { State = "other", Since = embedded.Since.AddHours(8) }], 3, "ordinary");

var baseTime = DateTimeOffset.UnixEpoch;
var retained = new SessionActivity("duplicate", "claude", "old first idle", "idle", baseTime.AddMinutes(-15)) { ProcessStartedAt = baseTime };
var shadow = retained with { Name = "old second idle", Since = baseTime.AddHours(2) };
var busy = retained with { State = "busy", Name = "old busy" };
var otherBusy = new SessionActivity("a", "codex", "other busy", "busy", baseTime);
var restored = retained with { Name = "restored", Since = baseTime.AddDays(1), ProcessStartedAt = baseTime.ToOffset(TimeSpan.FromHours(9)), ParentThreadId = "" };
var waiting = otherBusy with { Name = "waiting", State = "waiting", Since = baseTime.AddMinutes(-10) };
for (int flags = 0; flags < 4; flags++)
{
    Check("first-restored-before-attention/" + flags, [retained, shadow, busy, otherBusy], [restored, waiting], flags, "ordinary");
    Check("stable-attention-first/" + flags, [retained, busy, otherBusy], [waiting with { Since = retained.Since.ToOffset(TimeSpan.FromHours(-5)) }, restored], flags, "ordinary");
    Check("stable-attention-reversed/" + flags, [retained, busy, otherBusy], [restored, waiting with { Since = retained.Since }], flags, "ordinary");
    Check("both-child/" + flags, [retained, busy, otherBusy], [restored, waiting with { ParentThreadId = "" }], flags, "ordinary");
}
Check("unicode-state", [retained with { State = "e\u0301" }], [restored with { State = "é" }], 3, "ordinary");
Check("null-and-other-state", [retained with { State = null! }], [restored with { State = "other" }], 3, "ordinary");
Check("minimum-and-maximum", [retained with { Since = DateTimeOffset.MinValue }, busy, otherBusy], [restored, waiting with { Since = DateTimeOffset.MaxValue }], 3, "ordinary");

static (SessionActivity[] Old, SessionActivity[] Next) Frames(int size, string shape, int seed)
{
    var random = new Random(seed);
    var old = new SessionActivity[size];
    var next = new SessionActivity[size];
    string?[] states = ["busy", "idle", "waiting", "unavailable", "other", null, "", "frozen\uD800"];
    for (int i = 0; i < size; i++)
    {
        var id = (shape is "duplicate" or "mixed" ? i % Math.Max(1, size / 4) : i);
        var provider = i % 2 == 0 ? "claude" : "codex";
        var when = DateTimeOffset.UnixEpoch.AddTicks(i % 5).ToOffset(TimeSpan.FromHours(i % 3 - 1));
        var state = shape == "mixed" ? states[random.Next(states.Length)] : "busy";
        old[i] = new("id" + id, provider, "old-" + i, state!, when)
        {
            ProcessStartedAt = i % 3 == 0 ? null : DateTimeOffset.UnixEpoch.AddTicks(i % 4),
            ParentThreadId = i % 3 == 1 ? "parent" : i % 3 == 2 ? "" : null,
            RemoteHostId = i % 2 == 0 ? "host" : null, Detail = "detail-" + i, UsageSessionId = "usage" + i, ProcessId = i
        };
        if (shape == "mixed" && i % 8 == 0) old[i] = old[i] with { Provider = null!, Id = i % 16 == 0 ? null! : "孤\uD800" };
        next[i] = old[i] with
        {
            State = shape == "completed" || shape == "duplicate" && i % 3 != 0 ? (i % 2 == 0 ? "idle" : "waiting") : shape == "mixed" ? states[random.Next(states.Length)]! : old[i].State,
            Since = when.AddTicks(i % 3), Name = shape == "unchanged" ? old[i].Name : "incoming-" + i,
            ProcessStartedAt = old[i].ProcessStartedAt?.ToOffset(TimeSpan.FromHours(6))
        };
        if (shape == "reloaded")
            next[i] = next[i] with { Id = new string(old[i].Id.ToCharArray()), Provider = new string(old[i].Provider.ToCharArray()),
                Name = new string(old[i].Name.ToCharArray()), State = new string(old[i].State.ToCharArray()) };
    }
    if (shape is not ("unchanged" or "reloaded")) random.Shuffle(next);
    return (old, next);
}

sealed class OddCollection(SessionActivity[] values, bool fail = false) : IReadOnlyList<SessionActivity>
{
    public int Count => 0;
    public SessionActivity this[int index] => throw new NotSupportedException();
    public IEnumerator<SessionActivity> GetEnumerator()
    {
        foreach (var value in values) yield return value;
        if (fail) throw new InvalidOperationException("fixture enumerator");
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
