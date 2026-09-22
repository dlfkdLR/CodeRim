using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CodeRim.Core.Tests;

public sealed class InstallerUpdateTests : IDisposable
{
    private readonly Ed25519PrivateKeyParameters key = new(RandomNumberGenerator.GetBytes(32), 0);
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "coderim-installer-test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("MZ synthetic installer fixture, never executable");
    private static ReleasePackage Package(string architecture = "x64", string version = "2.1.9") => new(1, 2, Version.Parse(version), architecture, Payload.Length,
        Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant(), new Uri($"https://github.com/dlfkdLR/CodeRim/releases/download/v{version}/CodeRim-Windows-{version}-{architecture}-Setup.msi"), true);
    private static byte[] Manifest(ReleasePackage p) => JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, product = "CodeRim.Windows", version = p.Version.ToString(3), architecture = p.Architecture, file = p.FileName, size = p.Size, sha256 = p.Sha256 });
    private string Sign(byte[] bytes) { var signer = new Ed25519Signer(); signer.Init(true, key); signer.BlockUpdate(bytes, 0, bytes.Length); return Convert.ToBase64String(signer.GenerateSignature()); }
    private void Verify(ReleasePackage p, byte[] bytes, string? signature = null) => InstallerUpdates.VerifyManifest(p, bytes, signature ?? Sign(bytes), key.GeneratePublicKey().GetEncoded());
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public void AuthenticatesExactArchitectureAndVersion(string arch) { var p = Package(arch); Verify(p, Manifest(p)); }

    [Theory]
    [InlineData("product", "Other")]
    [InlineData("architecture", "arm64")]
    [InlineData("version", "2.1.8")]
    [InlineData("file", "../malware.exe")]
    [InlineData("sha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    public void RejectsCorrectlySignedButMismatchedContract(string field, string value)
    {
        var p = Package(); var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Manifest(p))!;
        values[field] = JsonSerializer.SerializeToElement(value); var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
        Assert.Throws<InvalidDataException>(() => Verify(p, bytes));
    }
    [Fact]
    public void RejectsTamperingAndWrongPublisher()
    {
        var p = Package(); var bytes = Manifest(p); var signature = Sign(bytes); bytes[^2] ^= 1;
        Assert.Throws<InvalidDataException>(() => Verify(p, bytes, signature));
        bytes = Manifest(p);
        Assert.Throws<InvalidDataException>(() => InstallerUpdates.VerifyManifest(p, bytes, Sign(bytes), new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0).GeneratePublicKey().GetEncoded()));
        Assert.Throws<InvalidDataException>(() => Verify(p, bytes, "not-base64"));
        Assert.Throws<InvalidDataException>(() => Verify(p, bytes, Convert.ToBase64String(new byte[63])));
    }
    [Fact]
    public void RejectsAmbiguousUnknownOrOversizedManifests()
    {
        var p = Package(); var text = Encoding.UTF8.GetString(Manifest(p));
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes(text.Replace("{", "{\"schema\":1,", StringComparison.Ordinal)), Encoding.UTF8.GetBytes(text.Replace("{", "{\"unknown\":0,", StringComparison.Ordinal)), new byte[4097] })
            Assert.Throws<InvalidDataException>(() => Verify(p, bytes));
    }
    [Fact]
    public void SignedWrongLengthIsRejected()
    {
        var p = Package(); var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Manifest(p)).Replace("\"size\":" + Payload.Length, "\"size\":1", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => Verify(p, bytes));
    }
    [Fact]
    public async Task InstallerDownloadUsesExecutableExtensionAndRechecksBytesBeforeExecution()
    {
        var p = Package(); Verify(p, Manifest(p)); using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        var downloaded = await ReleasePackageDownload.DownloadAsync(p, root, handler, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.EndsWith(".msi", downloaded.Path, StringComparison.Ordinal); using (var lease = InstallerUpdates.OpenVerifiedInstaller(downloaded)) Assert.Equal(Payload.Length, lease.Length);
        await File.WriteAllBytesAsync(downloaded.Path, new byte[Payload.Length], TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => InstallerUpdates.OpenVerifiedInstaller(downloaded));
    }
    [Fact]
    public async Task RefusesUnsignedManifestEvenIfGitHubDigestMatches()
    {
        var p = Package(); using var handler = new Handler(request => new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal) ? Manifest(p) : Encoding.ASCII.GetBytes(Sign(Manifest(p)))) });
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallerUpdates.AuthenticateAsync(p, handler, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData(404)]
    [InlineData(206)]
    [InlineData(302)]
    public async Task MetadataCannotBeMissingPartialOrRedirectOutsideGitHub(int status)
    {
        using var handler = new Handler(_ => { var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(Manifest(Package())) }; if (status == 302) response.Headers.Location = new Uri("https://example.com/manifest"); return response; });
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallerUpdates.AuthenticateAsync(Package(), handler, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ManifestLimitsAndCancellationAreEnforced()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4097]) });
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallerUpdates.AuthenticateAsync(Package(), handler, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstallerUpdates.AuthenticateAsync(Package(), handler, cancelled.Token));
    }
    [Fact]
    public void ReleasePrefersInstallerOverLegacyZipAndComparesVersion()
    {
        var p = Package(); var installer = new { id = 2, name = p.FileName, browser_download_url = p.Download.AbsoluteUri, state = "uploaded", size = p.Size, digest = "sha256:" + p.Sha256 };
        var zip = new { id = 3, name = "CodeRim-Windows-2.1.9-x64.zip", browser_download_url = "https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.9/CodeRim-Windows-2.1.9-x64.zip", state = "uploaded", size = p.Size, digest = "sha256:" + p.Sha256 };
        var json = JsonSerializer.Serialize(new { id = 1, tag_name = "v2.1.9", draft = false, prerelease = false, assets = new[] { zip, installer } });
        var update = ReleaseUpdates.Parse(json, "x64", new Version(2, 1, 8)); Assert.True(update.IsNewer); Assert.True(update.Package!.IsInstaller); Assert.Equal(p.FileName, update.Package.FileName);
        Assert.False(ReleaseUpdates.Parse(json, "x64", new Version(2, 1, 8), preferInstaller: false).Package!.IsInstaller);
        Assert.False(ReleaseUpdates.Parse(json, "x64", new Version(2, 1, 9)).IsNewer);
        Assert.False(ReleaseUpdates.Parse(json, "x64", new Version(2, 2, 0)).IsNewer);
        Assert.Throws<InvalidDataException>(() => ReleaseUpdates.Parse(json, "arm64", new Version(2, 1, 8)));
    }
    [Fact]
    public async Task PersistentCacheReusesExactBytesAndRejectsTampering()
    {
        var p = Package(); using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        var download = await ReleasePackageDownload.DownloadAsync(p, root, handler, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var cached = InstallerUpdates.Cache(download); Assert.False(File.Exists(download.Path));
        Assert.Equal(cached, InstallerUpdates.FindCached(p, root));
        var keep = Path.Combine(root, "user-file.exe"); await File.WriteAllTextAsync(keep, "preserve", TestContext.Current.CancellationToken);
        InstallerUpdates.CleanCache(root, cached.Path); Assert.True(File.Exists(cached.Path)); Assert.True(File.Exists(keep));
        await File.WriteAllTextAsync(cached.Path, "tampered", TestContext.Current.CancellationToken);
        Assert.Null(InstallerUpdates.FindCached(p, root)); Assert.False(File.Exists(cached.Path)); Assert.True(File.Exists(keep));
    }
    [Fact]
    public async Task CompletedUpgradeRemovesOnlyOwnedInstallerCache()
    {
        Directory.CreateDirectory(root);
        foreach (var name in new[] { Package().FileName, "CodeRim-Windows-2.1.9-x64-Setup.0123456789abcdef0123456789abcdef.msi", "keep.exe", "CodeRim-Windows-malformed-Setup.msi", "old.zip" })
            await File.WriteAllTextAsync(Path.Combine(root, name), "data", TestContext.Current.CancellationToken);
        InstallerUpdates.CleanCache(root, null);
        Assert.Equal((string[]) ["CodeRim-Windows-malformed-Setup.msi", "keep.exe", "old.zip"], Directory.GetFiles(root).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));
    }
    public static bool IsWindows => OperatingSystem.IsWindows();
    [Theory(Skip = "Requires Windows updater result classification", SkipUnless = nameof(IsWindows))]
    [InlineData(0, false, true, MsiUpdateStatus.Applied)]
    [InlineData(0, true, false, MsiUpdateStatus.RecoveryRequired)]
    [InlineData(3010, false, true, MsiUpdateStatus.RebootRequired)]
    [InlineData(1641, false, true, MsiUpdateStatus.RebootRequired)]
    [InlineData(1602, true, false, MsiUpdateStatus.Cancelled)]
    [InlineData(1603, true, false, MsiUpdateStatus.FailedRestored)]
    [InlineData(1618, true, false, MsiUpdateStatus.Busy)]
    [InlineData(1603, false, false, MsiUpdateStatus.RecoveryRequired)]
    [InlineData(1618, false, false, MsiUpdateStatus.RecoveryRequired)]
    public void MsiResultsNeverConfuseRebootBusyOrUnknownWithSuccess(int code, bool oldVerified, bool newVerified, MsiUpdateStatus expected)
    {
        if (OperatingSystem.IsWindows()) Assert.Equal(expected, MsiUpdateExecution.Classify(code, oldVerified, newVerified));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
