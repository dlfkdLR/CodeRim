using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeRim.Core.Services;

/// <summary>A sign-in CodeRim runs itself, so a provider connects without its command-line tool.</summary>
public enum InAppKind
{
    /// <summary>GitHub's device flow: a short code approved on github.com yields a Copilot token.</summary>
    GitHubDevice,
    /// <summary>Google account consent for Antigravity through a loopback redirect on 127.0.0.1.</summary>
    AntigravityGoogle,
}

public sealed record InAppOutcome(bool SignedIn, string? Reason = null)
{
    public static InAppOutcome Success { get; } = new(true);
    public static InAppOutcome Failed(string reason) => new(false, reason);
}

/// <summary>
/// GitHub's OAuth device flow, as CodexBar runs it (MIT): request a code, show it, poll until it
/// is approved. The token is handed to <c>save</c>; nothing is logged.
/// </summary>
public static class GitHubDeviceFlow
{
    public const string ClientId = "Iv1.b507a08c87ecfe98";

    public static async Task<InAppOutcome> RunAsync(HttpClient http, Action<string> update, Action<Uri> open, Action<string> copy,
        Action<string> save, CancellationToken cancellation, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        try
        {
            using var codeRequest = Form("https://github.com/login/device/code", new() { ["client_id"] = ClientId, ["scope"] = "read:user" });
            using var codeResponse = await http.SendAsync(codeRequest, cancellation).ConfigureAwait(false);
            if (!codeResponse.IsSuccessStatusCode) return InAppOutcome.Failed($"GitHub did not issue a sign-in code (HTTP {(int)codeResponse.StatusCode}). Choose Try again.");
            using var code = JsonDocument.Parse(await codeResponse.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            var userCode = Text(code.RootElement, "user_code"); var deviceCode = Text(code.RootElement, "device_code");
            var verify = Text(code.RootElement, "verification_uri");
            if (userCode is null || deviceCode is null || verify is null || !Uri.TryCreate(verify, UriKind.Absolute, out var verifyUri) || verifyUri.Scheme != Uri.UriSchemeHttps)
                return InAppOutcome.Failed("GitHub's sign-in code was incomplete. Choose Try again.");
            var interval = TimeSpan.FromSeconds(Math.Clamp(Number(code.RootElement, "interval") ?? 5, 1, 60));
            var expires = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(Number(code.RootElement, "expires_in") ?? 900, 60, 1800));
            copy(userCode);
            update($"Enter the code {userCode} on GitHub (it is already copied), approve CodeRim, and it connects on its own.");
            open(verifyUri);
            while (DateTime.UtcNow < expires)
            {
                await delay(interval, cancellation).ConfigureAwait(false);
                using var poll = Form("https://github.com/login/oauth/access_token", new()
                {
                    ["client_id"] = ClientId, ["device_code"] = deviceCode, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                });
                using var response = await http.SendAsync(poll, cancellation).ConfigureAwait(false);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
                if (Text(body.RootElement, "access_token") is { Length: > 0 } token)
                {
                    try { save(token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
                    { return InAppOutcome.Failed("GitHub approved the sign-in, but CodeRim could not store it. Choose Try again."); }
                    return InAppOutcome.Success;
                }
                switch (Text(body.RootElement, "error"))
                {
                    case "authorization_pending": continue;
                    case "slow_down": interval += TimeSpan.FromSeconds(5); continue;
                    case "expired_token": return InAppOutcome.Failed("The GitHub code expired before it was approved. Choose Try again for a new code.");
                    case "access_denied": return InAppOutcome.Failed("GitHub sign-in was declined.");
                    default: return InAppOutcome.Failed("GitHub refused the sign-in. Choose Try again for a new code.");
                }
            }
            return InAppOutcome.Failed("The GitHub code expired before it was approved. Choose Try again for a new code.");
        }
        catch (OperationCanceledException) { return InAppOutcome.Failed("Sign-in was cancelled."); }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
        { return InAppOutcome.Failed("GitHub sign-in did not finish — check the connection, then choose Try again."); }
    }

    internal static HttpRequestMessage Form(string url, Dictionary<string, string> values)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(values) };
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }
    internal static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static int? Number(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}

public sealed record GoogleOAuthClient(string ClientId, string ClientSecret);

/// <summary>Finds the OAuth client the installed Antigravity app signs in with, as CodexBar does on macOS.</summary>
public static class AntigravityOAuthClientLocator
{
    private const string Marker = "vs/platform/cloudCode/common/oauthClient.js";
    private static readonly Regex ClientIdPattern = new(@"[0-9]+-[A-Za-z0-9_-]+\.apps\.googleusercontent\.com", RegexOptions.CultureInvariant);
    private static readonly Regex SecretPattern = new(@"GOCSPX-[A-Za-z0-9_-]{28}", RegexOptions.CultureInvariant);

    public static IEnumerable<string> CandidatePaths(Func<string, string?> environment)
    {
        foreach (var root in new[] { environment("LOCALAPPDATA") is { } local ? Path.Combine(local, "Programs") : null, environment("ProgramFiles"), environment("ProgramFiles(x86)") })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var app in new[] { "Antigravity", "Google Antigravity", "Antigravity IDE" })
            {
                yield return Path.Combine(root, app, "resources", "app", "out", "main.js");
                yield return Path.Combine(root, "Google", app, "resources", "app", "out", "main.js");
            }
        }
    }

    public static GoogleOAuthClient? Discover(IEnumerable<string> paths, Func<string, string?>? read = null)
    {
        read ??= path => { try { return File.Exists(path) && new FileInfo(path).Length < 64 * 1024 * 1024 ? File.ReadAllText(path) : null; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; } };
        foreach (var path in paths) if (read(path) is { } text && Parse(text) is { } client) return client;
        return null;
    }

    public static GoogleOAuthClient? Parse(string text)
    {
        var start = text.IndexOf(Marker, StringComparison.Ordinal);
        var window = start >= 0 ? text.Substring(start, Math.Min(4000, text.Length - start)) : text;
        var id = ClientIdPattern.Match(window); var secret = SecretPattern.Match(window);
        return id.Success && secret.Success ? new(id.Value, secret.Value) : null;
    }
}

/// <summary>Google consent for Antigravity through a one-shot loopback redirect.</summary>
public static class AntigravityGoogleSignIn
{
    public static readonly string[] Scopes = ["https://www.googleapis.com/auth/cloud-platform", "https://www.googleapis.com/auth/userinfo.email"];

    public static async Task<InAppOutcome> RunAsync(GoogleOAuthClient? client, HttpClient http, Action<string> update, Func<Uri, bool> open,
        Action<string> save, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        if (client is null)
            return InAppOutcome.Failed("Install the Antigravity app first — CodeRim signs in with the same Google sign-in it uses. Then choose Try again.");
        var state = Guid.NewGuid().ToString("N");
        using var server = new OAuthLoopbackServer(state);
        var callback = server.Start();
        var query = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["redirect_uri"] = callback.AbsoluteUri, ["response_type"] = "code",
            ["scope"] = string.Join(' ', Scopes), ["access_type"] = "offline", ["prompt"] = "select_account consent", ["state"] = state,
        };
        var auth = new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', query.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value))));
        if (!open(auth)) return InAppOutcome.Failed("The Google sign-in page could not be opened.");
        update("Choose your Google account in the browser and allow access. CodeRim connects as soon as you finish.");
        OAuthCallback result;
        try { result = await server.WaitAsync(timeout ?? TimeSpan.FromMinutes(5), cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { return InAppOutcome.Failed("Google sign-in timed out or was cancelled."); }
        if (result.Error is { Length: > 0 } error)
            return InAppOutcome.Failed(error == "access_denied" ? "Google sign-in was cancelled." : "Google sign-in failed: " + error);
        if (result.State != state || string.IsNullOrEmpty(result.Code)) return InAppOutcome.Failed("Google did not return a sign-in code. Try again.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = result.Code, ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret,
                    ["redirect_uri"] = callback.AbsoluteUri, ["grant_type"] = "authorization_code",
                }),
            };
            using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return InAppOutcome.Failed("Google refused the sign-in code. Try again.");
            using var tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            var access = GitHubDeviceFlow.Text(tokens.RootElement, "access_token");
            if (access is null) return InAppOutcome.Failed("Google did not return an access token. Try again.");
            var expires = DateTimeOffset.UtcNow.AddSeconds(GitHubDeviceFlow.Number(tokens.RootElement, "expires_in") ?? 3600).ToUnixTimeMilliseconds();
            save(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["access_token"] = access, ["refresh_token"] = GitHubDeviceFlow.Text(tokens.RootElement, "refresh_token"),
                ["expiry_date"] = expires, ["id_token"] = GitHubDeviceFlow.Text(tokens.RootElement, "id_token"),
                ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret,
            }));
            return InAppOutcome.Success;
        }
        catch (OperationCanceledException) { return InAppOutcome.Failed("Google sign-in was cancelled."); }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        { return InAppOutcome.Failed("Google sign-in did not finish — check the connection, then choose Try again."); }
    }
}

public sealed record OAuthCallback(string? Code, string? State, string? Error);

/// <summary>
/// A one-request HTTP listener on 127.0.0.1 for an OAuth redirect. Only <c>/callback</c> settles
/// it; a request without the expected state is refused; oversized requests are cut off.
/// </summary>
public sealed class OAuthLoopbackServer : IDisposable
{
    public const int MaxRequestBytes = 16 * 1024;
    private readonly string expectedState;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource<OAuthCallback> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource stopping = new();

    public OAuthLoopbackServer(string state) => expectedState = state;

    public Uri Start()
    {
        listener.Start();
        _ = AcceptLoopAsync();
        return new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback");
    }

    public async Task<OAuthCallback> WaitAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(timeout);
        return await result.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient connection;
            try { connection = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = HandleAsync(connection);
        }
    }

    private async Task HandleAsync(TcpClient connection)
    {
        using (connection)
        {
            try
            {
                var stream = connection.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var buffer = new byte[4096]; var received = new MemoryStream();
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    received.Write(buffer, 0, read);
                    if (received.Length > MaxRequestBytes) { await Reply(stream, "431 Request Header Fields Too Large", null).ConfigureAwait(false); return; }
                    if (Contains(received, "\r\n\r\n"u8)) break;
                }
                var callback = Parse(Encoding.UTF8.GetString(received.ToArray()));
                if (callback is null) { await Reply(stream, "404 Not Found", null).ConfigureAwait(false); return; }
                var ok = callback.Error is null && !string.IsNullOrEmpty(callback.Code);
                await Reply(stream, ok ? "200 OK" : "400 Bad Request", ok
                    ? "<h2>Signed in</h2><p>You can close this tab and return to CodeRim.</p>"
                    : "<h2>Sign-in failed</h2><p>Close this tab and choose Try again in CodeRim.</p>").ConfigureAwait(false);
                result.TrySetResult(callback);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    internal OAuthCallback? Parse(string request)
    {
        var line = request.Split("\r\n", 2)[0].Split(' ');
        if (line.Length < 2 || !Uri.TryCreate("http://127.0.0.1" + line[1], UriKind.Absolute, out var url) || url.AbsolutePath != "/callback") return null;
        var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).GroupBy(p => Uri.UnescapeDataString(p[0]), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Length > 1 ? Uri.UnescapeDataString(g.First()[1].Replace('+', ' ')) : "", StringComparer.Ordinal);
        if (!query.TryGetValue("state", out var state)) return new(null, null, "missing state");
        if (state != expectedState) return new(null, state, "state mismatch");
        return new(query.GetValueOrDefault("code"), state, query.GetValueOrDefault("error") is { Length: > 0 } error ? error : null);
    }

    private static bool Contains(MemoryStream stream, ReadOnlySpan<byte> marker) => stream.GetBuffer().AsSpan(0, (int)stream.Length).IndexOf(marker) >= 0;

    private static async Task Reply(NetworkStream stream, string status, string? html)
    {
        var body = html is null ? [] : Encoding.UTF8.GetBytes($"<html><body style=\"font-family:sans-serif;padding:40px;text-align:center\">{html}</body></html>");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body).ConfigureAwait(false);
    }

    public void Cancel() => result.TrySetCanceled();

    public void Dispose()
    {
        stopping.Cancel();
        try { listener.Stop(); } catch (SocketException) { }
        stopping.Dispose();
    }
}
