using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CodeRim.Core.Services;

/// <summary>Authenticates small release manifests with the existing offline/Keychain release key.
/// GitHub's checksum alone is never permission to execute a downloaded installer.</summary>
public sealed record InstallerAuthorization(ReleasePackage Package, byte[] Manifest, string Signature);

public static class InstallerUpdates
{
    public const string PublicKey = "QzPdedAiOhC1Go03slQo9j+4TNqvw62c+3OYX10/mik=";
    internal const int MaximumManifestBytes = 4096;

    public static async Task<InstallerAuthorization> AuthenticateAsync(ReleasePackage package, CancellationToken token = default)
    {
        using var handler = ReleasePackageDownload.CreateHandler();
        return await AuthenticateAsync(package, handler, token).ConfigureAwait(false);
    }

    internal static async Task<InstallerAuthorization> AuthenticateAsync(ReleasePackage package, HttpMessageHandler handler, CancellationToken token)
    {
        if (!package.IsInstaller) throw new InvalidDataException("An installer release is required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = ReleasePackageDownload.CreateClient(handler);
        var manifest = await ReadAsync(client, new Uri(package.Download.AbsoluteUri + ".manifest.json"), MaximumManifestBytes, deadline.Token).ConfigureAwait(false);
        var signature = await ReadAsync(client, new Uri(package.Download.AbsoluteUri + ".manifest.sig"), 128, deadline.Token).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(signature).Trim();
        VerifyManifest(package, manifest, text, Convert.FromBase64String(PublicKey));
        return new(package, manifest, text);
    }

    internal static void VerifyManifest(ReleasePackage package, byte[] manifest, string signature, byte[] key)
    {
        if (!package.IsInstaller || manifest.Length is 0 or > MaximumManifestBytes || key.Length != 32)
            throw new InvalidDataException("Invalid installer manifest.");
        byte[] signed;
        try { signed = Convert.FromBase64String(signature); }
        catch (FormatException error) { throw new InvalidDataException("Invalid installer signature.", error); }
        if (signed.Length != 64) throw new InvalidDataException("Invalid installer signature.");
        var verifier = new Ed25519Signer(); verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
        verifier.BlockUpdate(manifest, 0, manifest.Length);
        if (!verifier.VerifySignature(signed)) throw new InvalidDataException("The installer publisher signature does not match.");
        using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(manifest));
        var root = document.RootElement;
        InstallPayloadManifest.ExactObject(root, "schema", "product", "version", "architecture", "file", "size", "sha256");
        if (!root.GetProperty("schema").TryGetInt32(out var schema) || schema != 1
            || root.GetProperty("product").GetString() != "CodeRim.Windows"
            || root.GetProperty("version").GetString() != package.Version.ToString(3)
            || root.GetProperty("architecture").GetString() != package.Architecture
            || root.GetProperty("file").GetString() != package.FileName
            || !root.GetProperty("size").TryGetInt64(out var size) || size != package.Size
            || root.GetProperty("sha256").GetString() != package.Sha256)
            throw new InvalidDataException("The signed manifest does not describe this installer.");
    }

    private static async Task<byte[]> ReadAsync(HttpClient client, Uri url, int maximum, CancellationToken token)
    {
        using var response = await ReleasePackageDownload.GetAsync(client, url, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null
            || response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > maximum)
            throw new InvalidDataException("The installer signature response is invalid.");
        using var source = await ReleasePackageDownload.OpenStreamAsync(response.Content, token).ConfigureAwait(false);
        using var target = new MemoryStream(); var buffer = new byte[1024];
        int count;
        while ((count = await ReleasePackageDownload.ReadAsync(source, buffer, token).ConfigureAwait(false)) != 0)
        {
            if (target.Length + count > maximum) throw new InvalidDataException("The installer signature response is too large.");
            target.Write(buffer, 0, count);
        }
        return target.ToArray();
    }

    internal static ReleasePackage ParseAuthorization(byte[] manifest, string signature, string architecture, Version current)
    {
        if (manifest.Length is 0 or > MaximumManifestBytes) throw new InvalidDataException("Invalid installer manifest.");
        using var document = JsonDocument.Parse(manifest); var root = document.RootElement;
        var versionText = root.GetProperty("version").GetString();
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != versionText || version <= current)
            throw new InvalidDataException("The update version must be newer than this installation.");
        ReleaseUpdates.ValidateArchitecture(architecture);
        var size = root.GetProperty("size").GetInt64(); var hash = root.GetProperty("sha256").GetString() ?? "";
        if (size <= 0 || size > ReleasePackageDownload.MaximumPackageBytes || hash.Length != 64 || !hash.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c)))
            throw new InvalidDataException("Invalid installer size or digest.");
        var file = $"CodeRim-Windows-{versionText}-{architecture}-Setup.msi";
        var package = new ReleasePackage(1, 1, version, architecture, size, hash, new Uri(ReleaseUpdates.Repository + "/releases/download/v" + versionText + "/" + file), true);
        VerifyManifest(package, manifest, signature, Convert.FromBase64String(PublicKey));
        return package;
    }

    public static DownloadedReleasePackage? FindCached(ReleasePackage package, string directory)
    {
        ReleaseUpdates.ValidateArchitecture(package.Architecture);
        if (!package.IsInstaller) throw new InvalidDataException("An installer release is required.");
        var path = Path.Combine(directory, package.FileName); InstallFileSystem.CheckPath(path);
        if (!File.Exists(path)) return null;
        var cached = new DownloadedReleasePackage(package, path);
        try { using var lease = OpenVerifiedInstaller(cached); return cached; }
        catch (InvalidDataException) { File.Delete(path); return null; }
    }

    public static DownloadedReleasePackage Cache(DownloadedReleasePackage downloaded)
    {
        var path = Path.Combine(Path.GetDirectoryName(downloaded.Path)!, downloaded.Package.FileName);
        InstallFileSystem.CheckPath(path);
        try
        {
            using (var lease = OpenVerifiedInstaller(downloaded)) { }
            File.Move(downloaded.Path, path, overwrite: true);
            return new(downloaded.Package, path);
        }
        catch { if (File.Exists(downloaded.Path)) File.Delete(downloaded.Path); throw; }
    }

    public static void CleanCache(string directory, string? keep)
    {
        if (!Directory.Exists(directory)) return;
        InstallFileSystem.CheckPath(directory);
        // Only files created by this installer updater. Legacy ZIPs/receipts/user files are untouched.
        foreach (var path in Directory.EnumerateFiles(directory, "CodeRim-Windows-*-Setup*.msi").Take(128))
        {
            if (path == keep || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),
                @"^CodeRim-Windows-[0-9]+\.[0-9]+\.[0-9]+-(x64|arm64)-Setup(\.[0-9a-f]{32})?\.msi$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) continue;
            try { InstallFileSystem.CheckPath(path); File.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Keep this lease until the installer process has been started. Rehash the exact file before execution.</summary>
    public static FileStream OpenVerifiedInstaller(DownloadedReleasePackage downloaded)
    {
        if (!downloaded.Package.IsInstaller) throw new InvalidDataException("An installer release is required.");
        InstallFileSystem.CheckPath(downloaded.Path);
        var stream = new FileStream(downloaded.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != downloaded.Package.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(downloaded.Package.Sha256)))
                throw new InvalidDataException("The installer changed after download.");
            stream.Position = 0; return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
