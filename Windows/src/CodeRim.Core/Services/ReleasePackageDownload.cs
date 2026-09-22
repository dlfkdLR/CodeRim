using System.Buffers;
using System.Net;
using System.Security.Cryptography;

namespace CodeRim.Core.Services;

/// <summary>A completed file with matching release metadata and digest. Publisher verification is still required before execution or installation.</summary>
public sealed record DownloadedReleasePackage
{
    internal DownloadedReleasePackage(ReleasePackage package, string path) { Package = package; Path = path; }
    public ReleasePackage Package { get; }
    public string Path { get; }
}

public static class ReleasePackageDownload
{
    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789abcdef");
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const int MaximumRedirects = 3;

    public static async Task<DownloadedReleasePackage> DownloadAsync(ReleasePackage package, string destinationDirectory, CancellationToken token = default)
    {
        using var handler = CreateHandler();
        return await DownloadAsync(package, destinationDirectory, handler, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
    }

    internal static HttpClientHandler CreateHandler(IWebProxy? proxy = null) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null,
        UseProxy = true, Proxy = new AnonymousProxy(proxy ?? HttpClient.DefaultProxy), DefaultProxyCredentials = null, AutomaticDecompression = DecompressionMethods.None
    };

    // Preserve system proxy routing without carrying environment/system proxy credentials into update requests.
    private sealed class AnonymousProxy(IWebProxy inner) : IWebProxy
    {
        public ICredentials? Credentials { get => null; set { if (value is not null) throw new NotSupportedException("Update proxy authentication is disabled."); } }
        public bool IsBypassed(Uri host) => inner.IsBypassed(host);
        public Uri? GetProxy(Uri destination)
        {
            var proxy = inner.GetProxy(destination);
            return proxy is null || proxy.UserInfo.Length == 0 ? proxy : new UriBuilder(proxy) { UserName = "", Password = "" }.Uri;
        }
    }

    internal static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeRim/" + ReleaseUpdates.CurrentVersion);
        return client;
    }

    internal static async Task<DownloadedReleasePackage> DownloadAsync(ReleasePackage package, string destinationDirectory,
        HttpMessageHandler handler, TimeSpan timeout, TimeSpan idleTimeout, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(package); ArgumentNullException.ThrowIfNull(destinationDirectory);
        Validate(package);
        if (!Path.IsPathFullyQualified(destinationDirectory)) throw new ArgumentException("Use an absolute download directory.", nameof(destinationDirectory));
        if (timeout <= TimeSpan.Zero || idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        deadline.Token.ThrowIfCancellationRequested();
        using var client = CreateClient(handler);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        using var response = await GetAsync(client, package.Download, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0
            || response.Content.Headers.ContentRange is not null)
            throw new InvalidDataException("The release download response is invalid.");
        if (response.Content.Headers.ContentLength is { } length && length != package.Size)
            throw new InvalidDataException("The release download length does not match.");
        using var source = await OpenStreamAsync(response.Content, deadline.Token).ConfigureAwait(false);
        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(destinationDirectory, Path.GetFileNameWithoutExtension(package.FileName) + "." + Guid.NewGuid().ToString("N") + ".zip");
        var partial = path + ".partial";
        var committed = false;
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536]; long total = 0;
                while (true)
                {
                    var capacity = (int)Math.Min(buffer.Length, package.Size - total + 1);
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); idle.CancelAfter(idleTimeout);
                    var count = await ReadAsync(source, buffer.AsMemory(0, capacity), idle.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (count == 0) break;
                    total += count;
                    if (total > package.Size || total > MaximumPackageBytes) throw new InvalidDataException("The release download is too large.");
                    digest.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                }
                if (total != package.Size || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(package.Sha256)))
                    throw new InvalidDataException("The release download does not match its digest and size.");
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            deadline.Token.ThrowIfCancellationRequested();
            File.Move(partial, path); // Same directory, no overwrite: only a fully checked file becomes visible as the result.
            committed = true;
            return new(package, path);
        }
        finally
        {
            if (!committed && File.Exists(partial)) File.Delete(partial);
        }
    }

    internal static async Task<int> ReadAsync(Stream source, Memory<byte> buffer, CancellationToken token)
    {
        var pending = source.ReadAsync(buffer, token).AsTask();
        try { return await pending.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        var pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try { return await pending.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    internal static async Task<Stream> OpenStreamAsync(HttpContent content, CancellationToken token)
    {
        var pending = content.ReadAsStreamAsync(token);
        try { return await pending.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri initial, CancellationToken token)
    {
        var current = initial; var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var redirects = 0; ; redirects++)
        {
            if (!Allowed(current, initial) || !seen.Add(current.AbsoluteUri)) throw new InvalidDataException("The release redirect is not allowed.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await SendAsync(client, request, token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            using (response)
            {
                if (redirects >= MaximumRedirects || response.Headers.Location is not { } location)
                    throw new InvalidDataException("The release redirect limit was reached.");
                if (!Uri.TryCreate(current, location, out var next)) throw new InvalidDataException("The release redirect is invalid.");
                current = next;
            }
        }
    }

    private static bool Allowed(Uri uri, Uri initial) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri == initial || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com");

    private static void Validate(ReleasePackage package)
    {
        ReleaseUpdates.ValidateArchitecture(package.Architecture);
        if (package.Version.Build < 0 || package.Version.Revision >= 0 || package.ReleaseId <= 0 || package.AssetId <= 0
            || package.Size <= 0 || package.Size > MaximumPackageBytes || package.Sha256.Length != 64
            || package.Sha256.AsSpan().ContainsAnyExcept(HexCharacters)
            || package.Download.AbsoluteUri != ReleaseUpdates.Repository + "/releases/download/v" + package.Version.ToString(3) + "/" + package.FileName)
            throw new InvalidDataException("The release package contract is invalid.");
    }
}
