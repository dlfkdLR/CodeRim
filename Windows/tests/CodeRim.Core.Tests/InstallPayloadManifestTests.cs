using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class InstallPayloadManifestTests
{
    [Fact]
    public void CanonicalManifestRoundTripsWithBoundIdentity()
    {
        var manifest = InstallFixture.Manifest(); var parsed = InstallPayloadManifest.Parse(manifest.ToJson());
        Assert.Equal(new Version(2, 2, 0), parsed.Version); Assert.Equal("arm64", parsed.Architecture);
        Assert.Equal(manifest.ToJson(), parsed.ToJson()); Assert.Equal(2, parsed.Files.Count);
    }
    [Theory]
    [InlineData("")][InlineData("/absolute")][InlineData("C:/absolute")][InlineData("C:relative")][InlineData("//host/share")]
    [InlineData("../outside")][InlineData("bin/../outside")][InlineData("bin//file")][InlineData("bin/./file")][InlineData("bin\\file")]
    [InlineData("file:stream")][InlineData("file.")][InlineData("file ")][InlineData(" file")][InlineData("CON")][InlineData("nul.exe")]
    [InlineData("com1.txt")][InlineData("LPT².log")][InlineData(".coderim-install.json")][InlineData("bin/.coderim-install.json")]
    [InlineData("file\nname")][InlineData("file*name")][InlineData("file?name")][InlineData("file|name")]
    public void RejectsNonPortableOrEscapingPaths(string path) => Assert.Throws<InvalidDataException>(() => InstallFixture.Manifest(files: new() { [path] = [1] }));
    [Theory]
    [InlineData("version", "\"2.2.0.0\"")][InlineData("version", "\"02.2.0\"")][InlineData("version", "\"2.2.0-beta\"")]
    [InlineData("architecture", "\"x86\"")][InlineData("product", "\"OtherProduct\"")][InlineData("schema", "\"1\"")]
    public void RejectsWrongIdentity(string field, string value)
    {
        var root = JsonNode.Parse(InstallFixture.Manifest().ToJson())!; root[field] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => InstallPayloadManifest.Parse(root.ToJsonString()));
    }
    [Fact]
    public void RejectsDuplicatePathsPropertiesAndFileDirectoryCollision()
    {
        var root = JsonNode.Parse(InstallFixture.Manifest().ToJson())!;
        var duplicate = root["files"]![0]!.DeepClone(); duplicate["path"] = duplicate["path"]!.GetValue<string>().ToUpperInvariant(); root["files"]!.AsArray().Add(duplicate);
        Assert.Throws<InvalidDataException>(() => InstallPayloadManifest.Parse(root.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => InstallPayloadManifest.Parse(InstallFixture.Manifest().ToJson().Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => InstallFixture.Manifest(files: new() { ["bin"] = [1], ["bin/file"] = [2] }));
    }
    [Theory]
    [InlineData("negative")][InlineData("file-limit")][InlineData("total-limit")][InlineData("bad-hash")][InlineData("extra-field")][InlineData("empty")]
    public void EnforcesManifestLimits(string scenario)
    {
        var root = JsonNode.Parse(InstallFixture.Manifest().ToJson())!;
        if (scenario == "negative") root["files"]![0]!["size"] = -1;
        if (scenario == "file-limit") root["files"]![0]!["size"] = InstallPayloadManifest.MaximumFileBytes + 1;
        if (scenario == "bad-hash") root["files"]![0]!["sha256"] = new string('z', 64);
        if (scenario == "extra-field") root["untrusted"] = true;
        if (scenario == "empty") root["files"] = new JsonArray();
        if (scenario == "total-limit")
        {
            var files = root["files"]!.AsArray(); var extra = files[0]!.DeepClone(); extra["path"] = "third.bin"; files.Add(extra);
            foreach (var file in files) file!["size"] = InstallPayloadManifest.MaximumFileBytes;
        }
        Assert.Throws<InvalidDataException>(() => InstallPayloadManifest.Parse(root.ToJsonString()));
    }
    [Theory]
    [InlineData("unknown")][InlineData("missing")][InlineData("hash")][InlineData("size")][InlineData("duplicate")][InlineData("symlink")]
    [InlineData("reparse")][InlineData("traversal")][InlineData("unknown-directory")][InlineData("compression-bomb")][InlineData("central-count")]
    public void RejectsUnsafeOrMismatchedArchives(string scenario)
    {
        using var fixture = new InstallFixture(); var files = InstallFixture.Bytes(); var manifest = InstallFixture.Manifest(files: files);
        if (scenario == "compression-bomb") { files = new() { ["zeros.bin"] = new byte[100000] }; manifest = InstallFixture.Manifest(files: files); }
        var archive = fixture.Archive(files, zip =>
        {
            if (scenario == "unknown") InstallFixture.Entry(zip, "unknown.txt", [1]);
            if (scenario == "duplicate") InstallFixture.Entry(zip, "CODERIM.EXE", files["CodeRim.exe"]);
            if (scenario == "traversal") InstallFixture.Entry(zip, "../escape.txt", [1]);
            if (scenario == "unknown-directory") zip.CreateEntry("unknown/");
        }, scenario);
        var stage = fixture.NewStage();
        Assert.Throws<InvalidDataException>(() => manifest.Extract(archive, stage, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.txt"))); Assert.False(Directory.Exists(fixture.InstallRoot));
    }
}

internal sealed class InstallFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "coderim-install-fixture-" + Guid.NewGuid().ToString("N"));
    internal string InstallRoot => Path.Combine(Root, "App");
    internal InstallFixture() => InstallFileSystem.CreatePrivateDirectory(Root);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    internal static Dictionary<string, byte[]> Bytes(string suffix = "old") => new() { ["CodeRim.exe"] = Encoding.UTF8.GetBytes("synthetic binary " + suffix), ["bin/coderim.cmd"] = Encoding.UTF8.GetBytes("synthetic command " + suffix) };
    internal static InstallPayloadManifest Manifest(string version = "2.2.0", string architecture = "arm64", Dictionary<string, byte[]>? files = null) => InstallPayloadManifest.Parse(JsonSerializer.Serialize(new
    {
        schema = 1, product = "CodeRim", version, architecture,
        files = (files ?? Bytes()).Select(pair => new { path = pair.Key, size = pair.Value.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)) })
    }));
    internal string NewStage() { var path = Path.Combine(Root, "stage-" + Guid.NewGuid().ToString("N")); InstallFileSystem.CreatePrivateDirectory(path); return path; }
    internal string Archive(Dictionary<string, byte[]>? files = null, Action<ZipArchive>? extra = null, string? scenario = null)
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
        using (var output = File.Create(path))
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
        {
            foreach (var pair in files ?? Bytes())
            {
                if (scenario == "missing" && pair.Key == "CodeRim.exe") continue;
                var value = scenario == "hash" ? pair.Value.Select(b => (byte)(b ^ 1)).ToArray() : scenario == "size" ? pair.Value.Append((byte)1).ToArray() : pair.Value;
                var entry = Entry(zip, pair.Key, value);
                if (scenario == "symlink") entry.ExternalAttributes = (0xa000 | 0x1ff) << 16;
                if (scenario == "reparse") entry.ExternalAttributes = (int)FileAttributes.ReparsePoint;
            }
            extra?.Invoke(zip);
        }
        if (scenario == "central-count")
        {
            var bytes = File.ReadAllBytes(path); bytes[^12] = 0xff; bytes[^11] = 0xff; File.WriteAllBytes(path, bytes);
        }
        return path;
    }
    internal static ZipArchiveEntry Entry(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal); using var output = entry.Open(); output.Write(bytes); return entry;
    }
    internal InstallPayloadManifest InstallOld()
    {
        var manifest = Manifest(); InstallTransaction.Apply(InstallRoot, Archive(), manifest, token: TestContext.Current.CancellationToken); return manifest;
    }
    internal static Dictionary<string, string> HashTree(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
    internal static void CopyTree(string from, string to)
    {
        InstallFileSystem.CreatePrivateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from)) CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
        foreach (var file in Directory.EnumerateFiles(from)) if (Path.GetFileName(file) != "commit.lock") File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }
}
