using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Core.Parsing;

var clock = DateTimeOffset.UnixEpoch;
var cases = new List<object>();
if (args[0] == "contract")
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    for (int code = 0; code <= char.MaxValue; code++)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { text = "A" + (char)code + "B" }));
        string? actual = JsonFields.Text(doc.RootElement, "text");
        hash.AppendData(Encoding.Unicode.GetBytes(actual ?? ""));
        if ((actual is null) != char.IsControl((char)code)) throw new InvalidDataException("Control character classification changed");
    }
    foreach (int count in new[] { 0, 1, 511, 512, 513 })
    {
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { text = new string('a', count) }));
        if ((JsonFields.Text(doc.RootElement, "text") is not null) != (count > 0 && count <= 512))
            throw new InvalidDataException("Text length validation changed");
    }
    cases.Add(new { codeUnits = 65536, digest = Convert.ToHexString(hash.GetHashAndReset()) });
}
foreach (var (files, perFile) in new[] { (0, 0), (4, 0), (5, 3), (4, 65), (2, 32768) })
{
    var dir = Path.Combine(args[1], $"{files}-{perFile}");
    if (!Directory.Exists(dir)) Seed(dir, files, perFile);
    var scanner = new UsageScanner("codex", [dir], new byte[32]);
    foreach (var tick in new[] { -2, -1, 0, 1, 2, 0, -2 })
    {
        var now = clock.AddTicks(tick);
        var scan = scanner.Scan(WeekStart.Monday, now);
        var observations = scan.Sessions.Sum(s => s.Attachments.Count);
        var reportingFiles = perFile == 3 ? files - 1 : files;
        var expectedCount = Enumerable.Range(0, reportingFiles).Sum(f =>
            Enumerable.Range(0, perFile).Count(i => Offset(f, perFile, i) <= tick));
        if (scan.SourceCount != files || scan.Sessions.Count != files || observations != expectedCount ||
            int.Parse(scan.ContentVersion.Split(':')[1], CultureInfo.InvariantCulture) != observations)
            throw new InvalidDataException("Session projection or content version count differs.");
        if (args[0] == "contract")
        {
            var digest = Digest(scan.Sessions);
            cases.Add(new { files, perFile, tick, scan.SourceCount, observations, images = scan.Sessions.Sum(s => s.Attachments.Sum(a => a.Count)), digest });
            var visible = scan.Sessions.Select(s => s.Attachments).OfType<AttachmentObservation[]>().FirstOrDefault(s => s.Length > 0);
            if (visible is not null)
            {
                visible[0] = new AttachmentObservation("changed-returned-array", clock.AddYears(1), 7);
                var fresh = scanner.Scan(WeekStart.Monday, now);
                if (Digest(fresh.Sessions) != digest) throw new InvalidOperationException("A previous result mutated the scanner cache");
            }
        }
        else
        {
            var ns = new double[9];
            var allocated = new long[9];
            for (int i = 0; i < ns.Length; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                var observed = scanner.Scan(WeekStart.Monday, now);
                ns[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                allocated[i] = GC.GetAllocatedBytesForCurrentThread() - before;
                GC.KeepAlive(observed);
            }
            Array.Sort(ns);
            Array.Sort(allocated);
            cases.Add(new { files, perFile, tick, observations, ns = ns[4], bytes = allocated[4] });
        }
    }
    if (args[0] == "contract" && perFile == 65)
    {
        var file = Path.Combine(dir, "source-0.jsonl");
        var contents = File.ReadAllBytes(file);
        var modified = File.GetLastWriteTimeUtc(file);
        try
        {
            var next = Encoding.UTF8.GetString(contents).Replace(".9999999", ".9999998", StringComparison.Ordinal);
            File.WriteAllText(file, next, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(file, modified);
            scanner.InvalidateCachedSource(file);
            var refreshed = scanner.Scan(WeekStart.Monday, clock.AddTicks(-2));
            var visible = refreshed.Sessions.Sum(session => session.Attachments.Count);
            if (visible != 64) throw new InvalidDataException("Reused a stale attachment window after source invalidation");
            cases.Add(new { replaced = true, visible, digest = Digest(refreshed.Sessions) });
        }
        finally
        {
            File.WriteAllBytes(file, contents);
            File.SetLastWriteTimeUtc(file, modified);
        }
    }
}
Console.WriteLine(JsonSerializer.Serialize(cases));

static string Digest(IReadOnlyList<SessionDetails> sessions)
{
    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var session in sessions)
        digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(session));
    return Convert.ToHexString(digest.GetHashAndReset());
}

static int Offset(int file, int perFile, int index)
{
    if (perFile != 65) return index % 3 - 1;
    return (file % 2 == 0) == (index < 64) ? -1 : 1;
}

void Seed(string dir, int files, int perFile)
{
    Directory.CreateDirectory(dir);
    for (int f = 0; f < files; f++)
    {
        using var writer = new StreamWriter(Path.Combine(dir, $"source-{f}.jsonl"), false, new UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = $"s-{f}", parent_thread_id = perFile == 3 && f == files - 1 ? "s-0" : null } }));
        for (var i = 0; i < perFile; i++)
        {
            var ts = clock.AddTicks(Offset(f, perFile, i)).ToOffset(TimeSpan.FromHours(i % 3 - 1)).ToString("O", CultureInfo.InvariantCulture);
            writer.WriteLine(JsonSerializer.Serialize(new { type = "response_item", timestamp = ts, id = $"m-{f}-{i}", ordinal = i % 2 == 0 ? (long?)i : null,
                payload = new { type = "message", role = "user", content = Enumerable.Range(0, i % 2 + 1).Select(n => new { type = "input_image", image_url = $"fixture-{i}-{n}" }) } }));
        }
    }
}
