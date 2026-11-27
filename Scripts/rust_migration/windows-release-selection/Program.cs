using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;

var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
if (args is ["runtime"])
{
    Console.WriteLine(JsonSerializer.Serialize(new { RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString() }, options));
    return;
}
if (args.Contains("active") || args[0] == "warm")
{
    var stamp = DateTimeOffset.UnixEpoch;
    var groups = SessionPresentation.Groups(new SessionActivity[] {
        new("one", "codex", "One", "busy", stamp), new("two", "claude", "Two", "idle", stamp)
    });
    if (groups.Count != 2) throw new InvalidOperationException("Session tree precondition failed");
}
var workloads = new Dictionary<string, byte[][]>
{
    ["single"] = [Page(0, 1, 0, true)],
    ["typical"] = [Page(0, 12, 1800, true)],
    ["mixed"] = [Page(0, 30, 3000, true), Page(30, 9, 3000, true)],
    ["allWindows"] = [Page(0, 29, 2200, false)],
    ["nearLimit"] = [Page(0, 26, 30000, false)]
};
var escaped = Encoding.UTF8.GetString(workloads["typical"][0]).Replace("\"tag_name\"", "\"tag_\\u006eame\"", StringComparison.Ordinal);
workloads["escapedNames"] = [Encoding.UTF8.GetBytes(escaped)];
workloads["duplicateField"] = ["[{\"draft\":true,\"draft\":false,\"prerelease\":false}]"u8.ToArray()];
if (args[0] == "contract")
{
    var cases = new List<object>();
    foreach (var (name, pages) in workloads)
        foreach (var arch in new[] { "x64", "arm64" })
            foreach (var installer in new[] { false, true })
                cases.Add(new { name, arch, installer, result = Check(pages, arch, installer) });
    foreach (var text in new[] { "[]", "[{}]", "[null]", "[true]", "\uFEFF[]", "[", "[{\"draft\":true,\"prerelease\":false}]",
        "[{\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"CodeRim-Windows-x64.zip\"}]}]" })
        cases.Add(new { text, result = Check([Encoding.UTF8.GetBytes(text)], "x64", true) });
    cases.Add(new { invalidUtf8 = Check([[0xff, 0xfe]], "x64", false) });
    cases.Add(new { duplicateVersion = Check([Page(0, 30, 100, false), Page(0, 1, 100, false)], "x64", true) });
    cases.Add(new { limit = Check([Page(0, 30, 100, false), Page(30, 30, 100, false), Page(60, 30, 100, false)], "x64", true) });
    Console.WriteLine(JsonSerializer.Serialize(cases, options));
    return;
}
foreach (var (name, pages) in workloads.Where(p => args.Length == 1 || args[1] == p.Key))
{
    var cold = args[0] == "cold";
    if (!cold) for (var i = 0; i < 5; i++) Run(name, pages);
    var loops = cold ? 1 : name == "nearLimit" ? 14 : 50;
    var usageBefore = LoadedUsageAssembly();
    var bridgeBefore = BridgeLoaded(usageBefore);
    var before = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    for (var i = 0; i < loops; i++) Run(name, pages);
    var us = Stopwatch.GetElapsedTime(start).TotalMicroseconds / loops;
    var managedBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / loops;
    var usageAfter = LoadedUsageAssembly();
    Console.WriteLine(JsonSerializer.Serialize(new { name, us, managedBytes, bridgeBefore, bridgeAfter = BridgeLoaded(usageAfter),
        usageAssemblyBefore = usageBefore != null, usageAssemblyAfter = usageAfter != null,
        metadataBytes = pages.Sum(p => p.Length) }, options));
}

static Assembly? LoadedUsageAssembly() =>
    AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "CodeRim.UsageCore");

static bool BridgeLoaded(Assembly? assembly) => (bool?)assembly?.GetType("CodeRim.Core.Domain.RustAccountingLoadState")?
    .GetField("IsLoaded", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) ?? false;

static byte[] Page(int start, int count, int noteSize, bool mixed)
{
    var releases = new List<object>();
    var digest = "sha256:" + new string('a', 64);
    for (var i = start; i < start + count; i++)
    {
        var version = $"2.{i % 5}.{i}";
        var names = new List<string> { $"CodeRim-{version}.dmg", "README.txt", "checksums.txt" };
        if (!mixed || i % 3 == 0)
            foreach (var arch in new[] { "x64", "arm64" })
                foreach (var ending in new[] { ".zip", "-Setup.msi" })
                    names.Add($"CodeRim-Windows-{version}-{arch}{ending}");
        releases.Add(new { id = 35121 + i, tag_name = "v" + version, draft = mixed && i % 13 == 11,
            prerelease = mixed && i % 7 == 5, name = "Update " + version, author = new { login = "test", id = 123 },
            body = "Release notes\n" + new string('x', noteSize), assets = names.Select((name, n) => new {
                url = "https://api.github.com/repos/dlfkdLR/CodeRim/releases/assets/" + n, id = i * 32 + n + 7,
                name, label = (string?)null, uploader = new { login = "test", node_id = "MDQ", type = "User" },
                state = "uploaded", size = 20345, digest, download_count = 2 * i,
                browser_download_url = $"https://github.com/dlfkdLR/CodeRim/releases/download/v{version}/{name}"
            }).ToArray() });
    }
    return JsonSerializer.SerializeToUtf8Bytes(releases);
}

static void Run(string name, byte[][] pages)
{
    using var handler = new Handler(pages);
    try
    {
        _ = ReleaseUpdates.CheckAsync("x64", handler, true, default).GetAwaiter().GetResult();
    }
    catch (InvalidDataException error) when (name == "duplicateField" && error.Message == "The release metadata is ambiguous.")
    {
        return;
    }
    if (name == "duplicateField") throw new InvalidOperationException("Duplicate field was accepted");
}

static object Check(byte[][] pages, string architecture, bool preferInstaller)
{
    using var handler = new Handler(pages);
    try
    {
        var result = ReleaseUpdates.CheckAsync(architecture, handler, preferInstaller, default).GetAwaiter().GetResult();
        return new { result.Version, result.Download, result.IsNewer, result.Package, handler.Calls };
    }
    catch (Exception e)
    {
        return new { error = e.GetType().FullName, e.Message, inner = e.InnerException?.GetType().FullName, handler.Calls };
    }
}

sealed class Handler(byte[][] pages) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Headers.Authorization is not null || request.RequestUri?.AbsoluteUri !=
            "https://api.github.com/repos/dlfkdLR/CodeRim/releases?per_page=30&page=" + (Calls + 1))
            throw new InvalidOperationException("Unexpected metadata request");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pages[Calls++]) });
    }
}

