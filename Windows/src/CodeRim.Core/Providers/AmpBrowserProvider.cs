using System.Net;
using System.Text;
using CodeRim.Core.Domain;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static readonly Uri AmpSettingsUri = new("https://ampcode.com/settings");
    public async Task<ProviderReading> FetchAmpBrowserAsync(string? credential, Func<Uri, string?>? cookies = null, CancellationToken token = default)
    {
        if (retryAfter.TryGetValue("amp", out var retry) && retry > DateTimeOffset.Now)
            return new("amp", ReadingState.Unavailable, [], Message: "Provider rate limit reached. Waiting before retrying.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var uri = AmpSettingsUri; var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var redirect = 0; redirect <= 3; redirect++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!visited.Add(uri.AbsoluteUri)) throw new InvalidDataException("Amp redirected repeatedly.");
                var raw = cookies is null ? credential : cookies(uri);
                var cookie = AmpSessionCookie(raw);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
                request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                request.Headers.Referrer = AmpSettingsUri;
                request.Headers.Add("Origin", "https://ampcode.com");
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    retryAfter["amp"] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.Now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null || !Uri.TryCreate(uri, location, out var next) || next.Scheme != "https"
                        || !next.IsDefaultPort || next.UserInfo.Length != 0) throw new InvalidDataException("Amp returned an unsupported redirect.");
                    var loginPath = next.AbsolutePath is "/login" or "/login/" or "/signin" or "/signin/" or "/sign-in" or "/sign-in/" or "/auth" or "/auth/"
                        || next.AbsolutePath.StartsWith("/auth/", StringComparison.Ordinal);
                    if (next.Host == "auth.ampcode.com" || loginPath && next.Host == "ampcode.com") throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                    if (next.Host != AmpSettingsUri.Host) throw new InvalidDataException("Amp returned an unsupported redirect.");
                    if (!(next.AbsolutePath == "/settings" || next.AbsolutePath.StartsWith("/settings/", StringComparison.Ordinal)))
                        throw new InvalidDataException("Amp returned an unsupported redirect.");
                    uri = next; continue;
                }
                if (!response.IsSuccessStatusCode) throw new ProviderRequestException(response.StatusCode);
                if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Amp page is too large.");
                using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var output = new MemoryStream(); var buffer = new byte[16384]; int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Amp page is too large.");
                    output.Write(buffer, 0, count);
                }
                deadline.Token.ThrowIfCancellationRequested();
                var reading = AmpBrowserUsage.Parse(new UTF8Encoding(false, true).GetString(output.ToArray()), DateTimeOffset.UtcNow, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                return reading;
            }
            throw new InvalidDataException("Amp redirected too many times.");
        }
        catch (ProviderRequestException error)
        {
            var state = error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ReadingState.NeedsAuth
                : error.Status == HttpStatusCode.TooManyRequests ? ReadingState.Unavailable : ReadingState.Error;
            return new("amp", state, [], Message: state == ReadingState.NeedsAuth ? "Sign in to ampcode.com and reconnect the Web source." : "Amp Web usage could not be refreshed. Retry later.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or DecoderFallbackException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); return new("amp", ReadingState.Error, [], Message: "Amp Web usage could not be read. Retry later."); }
    }
    private static string AmpSessionCookie(string? value)
    {
        string Unquote(string s) => s.Length >= 2 && s[0] == s[^1] && (s[0] == (char)34 || s[0] == (char)39) ? s[1..^1].Trim() : s;
        var raw = Unquote(value?.Trim() ?? "");
        if (raw.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) raw = Unquote(raw[7..].Trim());
        if (raw.Length is 0 or > 65536 || raw.Any(char.IsControl)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        if (!raw.Contains('=') && !raw.Contains(';')) raw = "session=" + raw;
        string? session = null; var pairs = raw.Split(';');
        if (pairs.Length > 512) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        foreach (var pair in pairs)
        {
            var at = pair.IndexOf('='); if (at <= 0) continue;
            if (pair[..at].Trim() != "session") continue;
            var found = pair[(at + 1)..].Trim();
            if (found.Length is 0 or > 32768 || found.Any(c => c < 0x21 || c > 0x7e || c is '"' or ',' or '\\')) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (session is not null && session != found) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            session = found;
        }
        return session is null ? throw new ProviderRequestException(HttpStatusCode.Unauthorized) : "session=" + session;
    }
}
