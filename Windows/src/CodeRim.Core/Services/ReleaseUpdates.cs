using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

public sealed record AvailableUpdate(Version Version, Uri Download, bool IsNewer)
{
    /// <summary>Null for older assets without complete integrity metadata; their manual download link remains usable.</summary>
    public ReleasePackage? Package { get; init; }
}

/// <summary>One exact stable release asset. A digest checks integrity, not publisher authenticity.</summary>
public sealed record ReleasePackage
{
    internal ReleasePackage(long releaseId, long assetId, Version version, string architecture, long size, string sha256, Uri download)
    {
        ReleaseId = releaseId; AssetId = assetId; Version = version; Architecture = architecture;
        Size = size; Sha256 = sha256; Download = download;
    }
    public long ReleaseId { get; }
    public long AssetId { get; }
    public Version Version { get; }
    public string Architecture { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public Uri Download { get; }
    public string FileName => "CodeRim-Windows-" + Version.ToString(3) + "-" + Architecture + ".zip";
}

public static class ReleaseUpdates
{
    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789abcdefABCDEF");
    internal const int MaximumMetadataBytes = 1048576;
    internal const int ReleasePageSize = 30;
    internal const int MaximumReleasePages = 3;
    internal const string Repository = "https://github.com/dlfkdLR/CodeRim";
    public static string CurrentVersion => typeof(ReleaseUpdates).Assembly.GetName().Version!.ToString(3);

    public static async Task<AvailableUpdate> CheckAsync(string architecture, CancellationToken token = default)
    {
        using var handler = ReleasePackageDownload.CreateHandler();
        return await CheckAsync(architecture, handler, token).ConfigureAwait(false);
    }

    internal static async Task<AvailableUpdate> CheckAsync(string architecture, HttpMessageHandler handler, CancellationToken token)
    {
        ValidateArchitecture(architecture);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = ReleasePackageDownload.CreateClient(handler);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var remaining = MaximumMetadataBytes;
        AvailableUpdate? selected = null; var versions = new HashSet<Version>();
        for (var page = 1; page <= MaximumReleasePages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/dlfkdLR/CodeRim/releases?per_page=" + ReleasePageSize + "&page=" + page);
            using var response = await ReleasePackageDownload.SendAsync(client, request, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentRange is not null
                || response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("The release metadata response is incomplete or encoded.");
            var bytes = await ReadMetadataAsync(response.Content, remaining, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            remaining -= bytes.Length;
            string json;
            try { json = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Release metadata is not valid UTF-8.", error); }
            using var document = JsonDocument.Parse(json);
            deadline.Token.ThrowIfCancellationRequested();
            var releases = document.RootElement;
            if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() > ReleasePageSize)
                throw new InvalidDataException("The release list is invalid.");
            foreach (var release in releases.EnumerateArray())
            {
                deadline.Token.ThrowIfCancellationRequested();
                UniqueObject(release);
                if (!release.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("The release channel is invalid.");
                if (draft.GetBoolean() || prerelease.GetBoolean()) continue;
                if (!HasWindowsPackage(release, architecture)) continue;
                // A matching Windows release must validate fully. Never hide corrupt newer metadata by trying an older release.
                var update = ParseRelease(release, architecture, Version.Parse(CurrentVersion));
                if (!versions.Add(update.Version)) throw new InvalidDataException("The Windows release version is ambiguous.");
                if (selected is null || update.Version > selected.Version) selected = update;
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (releases.GetArrayLength() < ReleasePageSize)
                return selected ?? throw new InvalidDataException("No matching Windows package is available.");
        }
        // A bounded search cannot claim that its best candidate is the latest when more releases may remain.
        throw new InvalidDataException("The Windows release search limit was reached. Use the releases page.");
    }

    private static async Task<byte[]> ReadMetadataAsync(HttpContent content, int remaining, CancellationToken token)
    {
        if (content.Headers.ContentLength > remaining) throw new InvalidDataException("Release metadata is too large.");
        using var source = await ReleasePackageDownload.OpenStreamAsync(content, token).ConfigureAwait(false);
        using var target = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await ReleasePackageDownload.ReadAsync(source, buffer, token).ConfigureAwait(false)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (target.Length + count > remaining) throw new InvalidDataException("Release metadata is too large.");
            target.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return target.ToArray();
    }

    private static bool HasWindowsPackage(JsonElement release, string architecture)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The release asset list is invalid.");
        var matching = false;
        foreach (var asset in assets.EnumerateArray())
        {
            UniqueObject(asset);
            var name = Text(asset, "name") ?? throw new InvalidDataException("The release asset name is invalid.");
            if (name.StartsWith("CodeRim-Windows-", StringComparison.Ordinal) && name.EndsWith("-" + architecture + ".zip", StringComparison.Ordinal)) matching = true;
        }
        return matching;
    }

    public static AvailableUpdate Parse(string json, string architecture, Version current)
    {
        ArgumentNullException.ThrowIfNull(json); ArgumentNullException.ThrowIfNull(current);
        ValidateArchitecture(architecture);
        if (Encoding.UTF8.GetByteCount(json) > MaximumMetadataBytes) throw new InvalidDataException("Release metadata is too large.");
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        return ParseRelease(root, architecture, current);
    }

    private static AvailableUpdate ParseRelease(JsonElement root, string architecture, Version current)
    {
        UniqueObject(root);
        var tag = Text(root, "tag_name");
        if (tag is not { Length: > 1 } || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version)
            || version.Build < 0 || version.Revision >= 0 || tag != "v" + version.ToString(3)
            || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("No stable release was returned.");
        var fileName = "CodeRim-Windows-" + version.ToString(3) + "-" + architecture + ".zip";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("No Windows package is available.");
        JsonElement? matching = null;
        foreach (var asset in assets.EnumerateArray())
        {
            UniqueObject(asset);
            if (Text(asset, "name") != fileName) continue;
            if (matching is not null) throw new InvalidDataException("The Windows package is ambiguous.");
            matching = asset;
        }
        if (matching is not { } selected) throw new InvalidDataException("No matching Windows package is available.");
        var expected = Repository + "/releases/download/" + tag + "/" + fileName;
        if (Text(selected, "browser_download_url") != expected)
            throw new InvalidDataException("The download is outside the release repository.");
        if (selected.TryGetProperty("state", out var state) && (state.ValueKind != JsonValueKind.String || state.GetString() != "uploaded"))
            throw new InvalidDataException("The Windows package is not ready.");
        var releaseId = OptionalPositiveInteger(root, "id");
        var assetId = OptionalPositiveInteger(selected, "id");
        var size = OptionalPositiveInteger(selected, "size");
        if (size > ReleasePackageDownload.MaximumPackageBytes) throw new InvalidDataException("The Windows package is too large.");
        string? hash = null;
        if (selected.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null)
        {
            var value = digest.ValueKind == JsonValueKind.String ? digest.GetString() : null;
            if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal)
                || value.AsSpan(7).ContainsAnyExcept(HexCharacters))
                throw new InvalidDataException("The release digest is invalid.");
            hash = value[7..].ToLowerInvariant();
        }
        var download = new Uri(expected);
        // Older GitHub assets can lack a digest. Do not break their existing manual update flow.
        var package = releaseId is { } rid && assetId is { } aid && size is { } bytes && hash is not null
            ? new ReleasePackage(rid, aid, version, architecture, bytes, hash, download) : null;
        return new(version, download, version > current) { Package = package };
    }

    internal static void ValidateArchitecture(string architecture)
    {
        if (architecture is not ("x64" or "arm64")) throw new ArgumentException("Unsupported Windows architecture.", nameof(architecture));
    }
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static long? OptionalPositiveInteger(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number) || number <= 0)
            throw new InvalidDataException("The release asset metadata is invalid.");
        return number;
    }
    private static void UniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The release metadata is invalid.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("The release metadata is ambiguous.");
    }
}
