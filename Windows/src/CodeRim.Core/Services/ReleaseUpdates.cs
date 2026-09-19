using System.Text.Json;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Services;

public sealed record AvailableUpdate(Version Version, Uri Download, bool IsNewer);
public static class ReleaseUpdates
{
    public static string CurrentVersion => typeof(ReleaseUpdates).Assembly.GetName().Version!.ToString(3);
    public static async Task<AvailableUpdate> CheckAsync(string architecture, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeRim/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.GetAsync("https://api.github.com/repos/dlfkdLR/CodeRim/releases/latest", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1048576) throw new InvalidDataException();
        using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var target = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
        { if (target.Length + count > 1048576) throw new InvalidDataException(); target.Write(buffer, 0, count); }
        return Parse(System.Text.Encoding.UTF8.GetString(target.ToArray()), architecture, Version.Parse(CurrentVersion));
    }
    public static AvailableUpdate Parse(string json, string architecture, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (architecture is not ("x64" or "arm64")) throw new ArgumentException("Unsupported Windows architecture.", nameof(architecture));
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var tag = ProviderParsers.Text(root, "tag_name");
        if (tag is not { Length: > 1 } || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Build < 0
            || ProviderParsers.Get(root, "draft").ValueKind != JsonValueKind.False || ProviderParsers.Get(root, "prerelease").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("No stable release was returned.");
        var fileName = "CodeRim-Windows-" + version.ToString(3) + "-" + architecture + ".zip";
        var assets = ProviderParsers.Get(root, "assets");
        if (assets.ValueKind != JsonValueKind.Array) throw new InvalidDataException("No Windows package is available.");
        foreach (var asset in assets.EnumerateArray())
        {
            if (ProviderParsers.Text(asset, "name") != fileName) continue;
            var expected = "https://github.com/dlfkdLR/CodeRim/releases/download/" + tag + "/" + fileName;
            if (ProviderParsers.Text(asset, "browser_download_url") != expected) throw new InvalidDataException("The download is outside the release repository.");
            return new(version, new Uri(expected), version > current);
        }
        throw new InvalidDataException("No matching Windows package is available.");
    }
}
