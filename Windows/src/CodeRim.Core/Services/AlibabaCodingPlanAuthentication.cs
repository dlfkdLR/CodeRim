using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeRim.Core.Services;

public sealed record AlibabaCodingPlanRegion(string Name, string Origin, string Gateway, string Region, string Site, string Commodity)
{
    public Uri Dashboard => new(Origin + "/" + Region + "/?tab=" + (Name == "cn" ? "model" : "coding-plan") + "#/efm/coding_plan");
    public Uri UserInfo => new(Origin + "/tool/user/info.json");
    public Uri Quota => new(Gateway + "/data/api.json?action=" + (Name == "cn" ? "BroadScopeAspnGateway" : "IntlBroadScopeAspnGateway")
        + "&product=sfm_bailian&api=" + AlibabaCodingPlanAuthentication.QuotaApi + "&_v=undefined");
}

public static class AlibabaCodingPlanAuthentication
{
    public const string QuotaApi = "zeldaEasy.broadscope-bailian.codingPlan.queryCodingPlanInstanceInfoV2";
    public static string? Source(string? raw) => raw?.Trim().ToLowerInvariant() switch
    { null or "" or "api" => "api", "web" => "web", _ => null };
    public static AlibabaCodingPlanRegion? Region(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "intl" => new("intl", "https://modelstudio.console.alibabacloud.com", "https://bailian-singapore-cs.alibabacloud.com", "ap-southeast-1", "MODELSTUDIO_ALIBABACLOUD", "sfm_codingplan_public_intl"),
        "cn" => new("cn", "https://bailian.console.aliyun.com", "https://bailian-cs.console.aliyun.com", "cn-beijing", "BAILIAN_ALIYUN", "sfm_codingplan_public_cn"),
        _ => null
    };
    public static string[] Domains(string region) => region == "cn" ? ["aliyun.com"] : ["alibabacloud.com"];
    public static Dictionary<string, string> Cookies(string? raw)
    {
        static string Unquote(string text) => text.Length >= 2 && text[0] == text[^1] && text[0] is '\'' or '"' ? text[1..^1].Trim() : text;
        var text = Unquote(raw?.Trim() ?? "");
        if (text.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) text = Unquote(text[7..].Trim());
        if (text.Length is 0 or > 65536 || text.Any(char.IsControl)) throw new InvalidDataException("Invalid Coding Plan session.");
        var pairs = text.Split(';');
        if (pairs.Length > 256) throw new InvalidDataException("Invalid Coding Plan session.");
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var part = pair.Trim(); if (part.Length == 0) continue;
            var equals = part.IndexOf('='); if (equals <= 0) throw new InvalidDataException("Invalid Coding Plan session.");
            var name = part[..equals].Trim(); var value = part[(equals + 1)..].Trim();
            if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')
                || value.Length > 32768 || value.Any(c => c < 0x20 || c is '\r' or '\n' or '"' or '\\')
                || !cookies.TryAdd(name, value)) throw new InvalidDataException("Invalid Coding Plan session.");
        }
        if (cookies.Count == 0) throw new InvalidDataException("Invalid Coding Plan session.");
        return cookies;
    }
    public static string Header(IReadOnlyDictionary<string, string> cookies) => string.Join("; ", cookies.Select(x => x.Key + "=" + x.Value));
    public static string? SecurityToken(string? token) => token?.Trim() is { Length: > 0 and <= 8192 } value
        && !value.Any(char.IsControl) ? value : null;
    public static string? HtmlToken(string html)
    {
        if (html.Length > 2 * 1024 * 1024) throw new InvalidDataException("Coding Plan page is too large.");
        var matches = Regex.Matches(html, """(?:["']?(?:SEC_TOKEN|secToken|sec_token)["']?)\s*:\s*(["'])([^"'\r\n]+)\1""",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var tokens = matches.Select(x => SecurityToken(x.Groups[2].Value)).Where(x => x is not null).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        if (tokens.Length > 1) throw new InvalidDataException("Ambiguous Coding Plan security token.");
        return tokens.FirstOrDefault();
    }
    public static string? JsonToken(JsonElement root)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal); var nodes = 0;
        void Visit(JsonElement value, int depth)
        {
            if (++nodes > 8192 || depth > 12) throw new InvalidDataException("Invalid Coding Plan user info.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in value.EnumerateObject())
                {
                    if (!names.Add(field.Name)) throw new InvalidDataException("Ambiguous Coding Plan user info.");
                    if (field.Name is "secToken" or "sec_token")
                    {
                        if (field.Value.ValueKind != JsonValueKind.String || SecurityToken(field.Value.GetString()) is not { } token)
                            throw new InvalidDataException("Invalid Coding Plan security token.");
                        tokens.Add(token);
                    }
                    else Visit(field.Value, depth + 1);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item, depth + 1);
            else if (value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 1 } text && text[0] is '{' or '[')
            { using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }); Visit(json.RootElement, depth + 1); }
        }
        Visit(root, 0);
        return tokens.Count switch { 0 => null, 1 => tokens.Single(), _ => throw new InvalidDataException("Ambiguous Coding Plan security token.") };
    }
}
