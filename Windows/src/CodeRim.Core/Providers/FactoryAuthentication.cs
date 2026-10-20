using System.Net;
namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    private sealed record FactoryCredential(string? Cookie, string? Bearer);
    // Authentication changes restart the whole transaction, so its profile and billing
    // documents cannot come from different credential modes.
    private sealed class FactoryAuthenticationRetryException : Exception;
    private sealed class FactoryRequestContext
    {
        private int cookieFilter;
        private bool omitBearer;
        private bool recoveringConflict;
        public string? AcceptedBearer { get; set; }
        public FactoryCredential Apply(FactoryCredential original) =>
            new(Filter(original.Cookie, cookieFilter), omitBearer ? null : original.Bearer);
        public bool Retry(HttpStatusCode status, FactoryCredential original, FactoryCredential sent)
        {
            if (sent.Cookie is null || status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Conflict)) return false;
            recoveringConflict |= status == HttpStatusCode.Conflict;
            if (sent.Bearer is not null && !omitBearer)
            {
                omitBearer = true; AcceptedBearer = null; return true;
            }
            if (!recoveringConflict) return false;
            // These names and bounded variants follow the pinned Factory browser reader.
            // Apply each variant after URI scoping and keep the saved profile unchanged.
            for (var next = cookieFilter + 1; next <= 4; next++)
            {
                var filtered = Filter(original.Cookie, next);
                if (filtered is null || filtered == sent.Cookie) continue;
                cookieFilter = next; omitBearer = true; AcceptedBearer = null; return true;
            }
            return false;
        }
        private static string? Filter(string? cookie, int mode)
        {
            if (cookie is null || mode == 0) return cookie;
            var pairs = cookie.Split("; ", StringSplitOptions.RemoveEmptyEntries).Where(pair =>
            {
                var name = pair[..pair.IndexOf('=')];
                var stale = name is "access-token" or "__recent_auth";
                var session = name is "session" or "wos-session";
                return mode switch
                {
                    1 => !stale,
                    2 => !session,
                    3 => !stale && !session,
                    _ => name is "__Secure-next-auth.session-token" or "next-auth.session-token"
                        or "__Secure-authjs.session-token" or "authjs.session-token" or "__Host-authjs.csrf-token"
                };
            });
            var result = string.Join("; ", pairs);
            return result.Length == 0 ? null : result;
        }
    }
    private static FactoryCredential FactoryAuthentication(string raw, bool browser = false)
    {
        string Unquote(string value) => value.Length >= 2 && value[0] == value[^1] && (value[0] == (char)34 || value[0] == (char)39) ? value[1..^1].Trim() : value;
        var input = Unquote(raw.Trim());
        if (input.Length is 0 or > 65536 || input.Any(char.IsControl)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        var authorization = input.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase);
        if (authorization) input = Unquote(input[14..].Trim());
        if (input.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = input[7..].Trim();
            if (browser || bearer.Length is 0 or > 32768 || bearer.Any(c => !char.IsAsciiLetterOrDigit(c) && !"._~+/=-".Contains(c)))
                throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            return new(null, bearer);
        }
        if (authorization || input.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        var explicitCookie = input.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase);
        if (explicitCookie) input = Unquote(input[7..].Trim());
        if (browser || explicitCookie || input.Contains('=') || input.Contains(';'))
        {
            var pairs = new List<string>(); string? access = null;
            foreach (var piece in input.Split(';'))
            {
                var pair = piece.Trim(); if (pair.Length == 0) continue;
                var at = pair.IndexOf('='); if (at <= 0) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                var name = pair[..at].Trim(); var value = pair[(at + 1)..].Trim();
                if (name.Length > 256 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != (char)96 && !"!#$%&'*+-.^_|~".Contains(c)) || value.Length > 32768 || value.Any(char.IsControl))
                    throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                pairs.Add(name + "=" + value);
                if (name == "access-token" && value.Length > 0)
                {
                    if (access is not null && access != value) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                    access = value;
                }
            }
            if (pairs.Count is 0 or > 512) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (access?.Any(c => !char.IsAsciiLetterOrDigit(c) && !"._~+/=-".Contains(c)) == true) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            return new(string.Join("; ", pairs), access);
        }
        if (input.Length > 32768 || input.Any(c => !char.IsAsciiLetterOrDigit(c) && !"._~+/-".Contains(c)))
            throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        return new(null, input);
    }
}
