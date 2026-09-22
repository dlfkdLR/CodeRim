using System.Text;

namespace CodeRim.Core.Services;

public sealed record MiniMaxWebCredential(string Region, string Cookie, string? Bearer = null, string? Group = null, string? BrowserState = null);

public static class MiniMaxAuthentication
{
    public static string? Region(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "global" => "global", "cn" => "cn", _ => null };
    public static string? Source(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "auto" => "auto", "api" => "api", "web" => "web", _ => null };
    public static string Domain(string region) => region == "cn" ? "minimaxi.com" : "minimax.io";
    public static Uri PlanUri(string region) => new("https://platform." + Domain(region) + "/user-center/payment/coding-plan?cycle_type=3");
    private static string Unquote(string text) => text.Length >= 2 && (text[0] == '"' && text[^1] == '"' || text[0] == '\'' && text[^1] == '\'') ? text[1..^1].Trim() : text;
    public static MiniMaxWebCredential? Parse(string? input, string region)
    {
        if (Region(region) != region || input is null || input.Length > 65536) return null;
        var raw = input.Trim(); if (raw.Length == 0) return null;
        string? cookie = null, bearer = null, group = null;
        var cookieSeen = false; var bearerSeen = false;
        bool Group(string? value)
        {
            if (value is null || value.Length is 0 or > 256 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')) return false;
            if (group is not null && group != value) return false;
            group = value; return true;
        }
        bool Header(string header)
        {
            var colon = header.IndexOf(':'); if (colon <= 0) return true;
            var name = header[..colon].Trim(); var value = header[(colon + 1)..].Trim();
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            { if (cookieSeen) return false; cookieSeen = true; cookie = Unquote(value); }
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                if (bearerSeen || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
                bearerSeen = true; bearer = KimiAuthentication.Clean(value[7..]); if (bearer is null) return false;
            }
            else if (name.Equals("x-group-id", StringComparison.OrdinalIgnoreCase) && !Group(value)) return false;
            return true;
        }
        if (raw.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("curl\n", StringComparison.OrdinalIgnoreCase))
        {
            var args = Words(raw); if (args is null) return null;
            var urls = 0;
            for (var i = 1; i < args.Count; i++)
            {
                var arg = args[i];
                if (arg is "-H" or "--header" or "-b" or "--cookie")
                {
                    if (++i == args.Count) return null;
                    if (arg is "-b" or "--cookie") { if (!Header("Cookie: " + args[i])) return null; }
                    else if (!Header(args[i])) return null;
                }
                else if (arg.StartsWith("--header=", StringComparison.Ordinal))
                { if (!Header(arg[9..])) return null; }
                else if (arg.StartsWith("--cookie=", StringComparison.Ordinal))
                { if (!Header("Cookie: " + arg[9..])) return null; }
                else if (arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (++urls != 1 || !Uri.TryCreate(arg, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
                        || uri.Host != "platform." + Domain(region) && uri.Host != "www." + Domain(region)) return null;
                    foreach (var item in uri.Query.TrimStart('?').Split('&'))
                    {
                        var pair = item.Split('=', 2);
                        if (pair.Length == 2 && (pair[0].Equals("GroupId", StringComparison.OrdinalIgnoreCase) || pair[0] == "group_id")
                            && !Group(Uri.UnescapeDataString(pair[1]))) return null;
                    }
                }
            }
            if (urls != 1) return null;
        }
        else if (raw.Contains('\n') && raw.Split('\n').Any(line => line.TrimStart().StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var line in raw.Split('\n'))
                if (line.Trim() is { Length: > 0 } header && !Header(header)) return null;
        }
        else
        {
            raw = Unquote(raw);
            if (raw.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) raw = Unquote(raw[7..].Trim());
            cookie = raw;
        }
        if (cookie is null || cookie.Length == 0 || cookie.Any(c => c < 0x20 || c > 0x7e)) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new List<string>();
        foreach (var part in cookie.Split(';'))
        {
            var pair = part.Trim(); if (pair.Length == 0) continue;
            var at = pair.IndexOf('='); if (at <= 0) return null;
            var name = pair[..at]; var value = pair[(at + 1)..];
            if (name.Length > 256 || !name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_|~".Contains(c))
                || value.Length > 32768 || value.Any(c => c < 0x21 || c > 0x7e || c is '"' or ',' or ';' or '\\') || !names.Add(name)) return null;
            if (name == "minimax_group_id_v2" && !Group(value)) return null;
            pairs.Add(name + "=" + value);
        }
        return pairs.Count == 0 ? null : new(region, string.Join("; ", pairs), bearer, group);
    }
    // Tokenizes a pasted command as data. No shell, expansion, executable, or URL is ever run.
    private static List<string>? Words(string input)
    {
        var result = new List<string>(); var word = new StringBuilder(); char quote = '\0'; var started = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\\' && quote != '\'' && i + 1 < input.Length)
            { var next = input[++i]; if (next == '\n') continue; if (next == '\r' && i + 1 < input.Length && input[i + 1] == '\n') { i++; continue; } word.Append(next); started = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; else word.Append(c); started = true; continue; }
            if (c is '"' or '\'') { quote = c; started = true; continue; }
            if (char.IsWhiteSpace(c)) { if (started) { result.Add(word.ToString()); word.Clear(); started = false; } }
            else { word.Append(c); started = true; }
            if (result.Count > 256) return null;
        }
        if (quote != '\0') return null;
        if (started) result.Add(word.ToString());
        return result;
    }
}
