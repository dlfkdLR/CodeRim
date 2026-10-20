using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Services;

/// <summary>An explicitly imported session snapshot, never an ambient live-browser selector.</summary>
public sealed record ChromiumProviderCredential(string Provider, string Region, string ProfileDirectory, string Origin,
    string? Secret, string? Cookies = null, string? Group = null)
{
    public override string ToString() => "Imported Chromium sign-in";
    public string Serialize()
    {
        ChromiumProviderAuthentication.Validate(this);
        var json = JsonSerializer.Serialize(new { version = 1, provider = Provider, region = Region,
            profile = ProfileDirectory, origin = Origin, secret = Secret, cookies = Cookies, group = Group });
        if (Encoding.UTF8.GetByteCount(json) > 262144) throw new InvalidDataException("The imported sign-in is too large.");
        return json;
    }
    public static ChromiumProviderCredential Parse(string json)
    {
        if (json.Length > 262144 || Encoding.UTF8.GetByteCount(json) > 262144) throw new InvalidDataException("The imported sign-in is too large.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid imported sign-in.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (root.EnumerateObject().Any(p => !names.Add(p.Name)) || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
            throw new InvalidDataException("Unsupported imported sign-in.");
        string? Field(string name)
        { if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null; if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid imported sign-in field."); return value.GetString(); }
        var credential = new ChromiumProviderCredential(Field("provider") ?? "", Field("region") ?? "", Field("profile") ?? "", Field("origin") ?? "", Field("secret"), Field("cookies"), Field("group"));
        ChromiumProviderAuthentication.Validate(credential); return credential;
    }
    public MiniMaxWebCredential MiniMax()
    {
        ChromiumProviderAuthentication.Validate(this);
        if (Provider != "minimax") throw new InvalidDataException("This is not a MiniMax sign-in.");
        var jar = BrowserCookieJar.Parse(Cookies!, [MiniMaxAuthentication.Domain(Region)]);
        var parsed = MiniMaxAuthentication.Parse(jar.Header(MiniMaxAuthentication.PlanUri(Region), DateTimeOffset.UtcNow), Region)
            ?? throw new InvalidDataException("The imported MiniMax cookies expired.");
        var result = parsed with { Bearer = Secret, Group = Group, BrowserState = jar.Serialize() };
        return result with { ImportedTupleFingerprint = ChromiumProviderAuthentication.MiniMaxFingerprint(result) };
    }
}

public static class ChromiumProviderAuthentication
{
    private static readonly string[] DeepSeekKeys = ["userToken"];
    private static readonly string[] FactoryKeys = ["workos:access-token", "workos:refresh-token"];
    private static readonly string[] MiniMaxKeys = ["access_token", "accessToken", "id_token", "idToken", "token", "authToken", "authorization", "bearer", "user_detail", "persist:root", "group_id", "groupId", "groupID"];
    private static readonly string[] TokenFields = ["value", "token", "access_token", "accessToken", "userToken"];
    private static readonly string[] MiniMaxTokenFields = ["access_token", "accessToken", "id_token", "idToken", "token", "authToken", "authorization", "bearer"];
    private static readonly string[] GroupFields = ["group_id", "groupId", "groupID", "GroupID", "gid"];
    public static string[] Origins(string id, string region) => id switch
    {
        "deepseek" when region == "default" => ["https://platform.deepseek.com"],
        "factory" when region == "default" => ["https://app.factory.ai", "https://auth.factory.ai"],
        "minimax" when region is "global" or "cn" => ["https://platform." + MiniMaxAuthentication.Domain(region), "https://www." + MiniMaxAuthentication.Domain(region), "https://" + MiniMaxAuthentication.Domain(region)],
        _ => throw new InvalidDataException("Select a supported provider region.")
    };
    private static InvalidDataException Invalid() => new("The selected browser sign-in is missing, ambiguous or unsupported.");
    internal static string Profile(string path)
    {
        if (path.Length is 0 or > 4096 || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) throw Invalid();
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows() && normalized[Path.GetPathRoot(normalized)!.Length..].Contains(':')) throw Invalid();
        return normalized;
    }
    private static string? Clean(string? text)
    {
        text = text?.Trim(); if (string.IsNullOrEmpty(text)) return null;
        if (text.Length is < 20 or > 32768 || text.Any(c => c < 0x21 || c > 0x7e || c is '"' or '\'' or '\\')) throw Invalid(); return text;
    }
    private static JsonElement Json(string raw)
    {
        if (raw.Length > 65536) throw Invalid(); using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var item in value.EnumerateObject()) { if (!names.Add(item.Name)) throw Invalid(); Unique(item.Value); } }
            else if (value.ValueKind == JsonValueKind.Array) { if (value.GetArrayLength() > 256) throw Invalid(); foreach (var item in value.EnumerateArray()) Unique(item); }
        }
        Unique(root); return root.Clone();
    }
    private static string? Merge(string? current, string? next)
    { if (current is not null && next is not null && current != next) throw Invalid(); return next ?? current; }
    public static string DeepSeekToken(string raw)
    {
        if (raw.Length > 65536) throw Invalid(); raw = raw.Trim();
        if (raw.StartsWith('{') || raw.StartsWith('[') || raw.StartsWith('"'))
        {
            var root = Json(raw); if (root.ValueKind == JsonValueKind.String) return Clean(root.GetString()) ?? throw Invalid();
            if (root.ValueKind != JsonValueKind.Object) throw Invalid(); string? token = null;
            foreach (var name in TokenFields)
                if (root.TryGetProperty(name, out var value))
                { if (value.ValueKind != JsonValueKind.String) throw Invalid(); token = Merge(token, Clean(value.GetString()) ?? throw Invalid()); }
            return token ?? throw Invalid();
        }
        if (raw.Length > 1 && raw[0] == '\'' && raw[^1] == '\'') raw = raw[1..^1];
        return Clean(raw) ?? throw Invalid();
    }
    public static ChromiumProviderCredential Read(string id, string region, string profileDirectory, string origin, DateTimeOffset now, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var profile = Profile(profileDirectory);
        if (!Origins(id, region).Contains(origin, StringComparer.Ordinal)) throw Invalid();
        var keys = id == "deepseek" ? DeepSeekKeys : id == "factory" ? FactoryKeys : MiniMaxKeys;
        var data = ChromiumLocalStorageSnapshot.Read(Path.Combine(profile, "Local Storage", "leveldb"), origin, keys, token);
        ChromiumProviderCredential credential;
        if (id == "deepseek") credential = new(id, region, profile, origin, DeepSeekToken(data.GetValueOrDefault("userToken") ?? ""));
        else if (id == "factory")
        {
            string? Field(string name) => data.TryGetValue(name, out var value) ? FactoryWorkOsProfile.Token(Unquote(value)) ?? throw Invalid() : null;
            var access = Field("workos:access-token"); var refresh = Field("workos:refresh-token");
            if (access is null && refresh is null) throw Invalid();
            credential = new(id, region, profile, origin, new FactoryWorkOsProfile(access, refresh, null, null, null).Serialize());
        }
        else
        {
            var jar = ChromiumPlaintextCookies.Read(profile, MiniMaxAuthentication.Domain(region), now, token);
            var header = jar.Header(MiniMaxAuthentication.PlanUri(region), now); var cookie = MiniMaxAuthentication.Parse(header, region) ?? throw Invalid();
            var pair = MiniMaxFields(data); var secret = pair.Token; var storageGroup = Merge(pair.Group, MiniMaxJwtGroup(secret));
            var hertz = CookieSession(cookie.Cookie);
            if (secret is not null && secret != hertz && (storageGroup is null || storageGroup != cookie.Group)) throw Invalid();
            var group = Merge(storageGroup, cookie.Group);
            credential = new(id, region, profile, origin, secret, jar.Serialize(), group);
        }
        Validate(credential); token.ThrowIfCancellationRequested(); return credential;
    }
    private static string Unquote(string raw)
    { raw = raw.Trim(); return raw.StartsWith('"') ? Json(raw) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? throw Invalid() : throw Invalid() : raw; }
    private static string? CookieSession(string header) => header.Split(';').Select(x => x.Trim())
        .FirstOrDefault(x => x.StartsWith("HERTZ-SESSION=", StringComparison.Ordinal))?["HERTZ-SESSION=".Length..];
    private static string GroupValue(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return FactoryWorkOsProfile.Identifier(text) ?? throw Invalid();
    }
    private static string? MiniMaxJwtGroup(string? token)
    {
        if (token is null || token.Count(c => c == '.') != 2) return null;
        var payload = token.Split('.')[1];
        if (payload.Length == 0 || payload.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return null;
        try
        {
            var base64 = payload.Replace('-', '+').Replace('_', '/'); base64 += new string('=', (4 - base64.Length % 4) % 4);
            var value = Json(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64)));
            if (value.ValueKind != JsonValueKind.Object) throw Invalid(); string? group = null;
            foreach (var name in GroupFields) if (value.TryGetProperty(name, out var field)) group = Merge(group, GroupValue(field));
            return group;
        }
        catch (Exception error) when (error is FormatException or JsonException or DecoderFallbackException) { throw Invalid(); }
    }
    internal static (string? Token, string? Group) MiniMaxFields(IReadOnlyDictionary<string, string> values)
    {
        string? token = null, group = null;
        void Visit(JsonElement root, int depth)
        {
            if (depth > 4 || root.ValueKind != JsonValueKind.Object) throw Invalid();
            foreach (var property in root.EnumerateObject())
            {
                if (MiniMaxTokenFields.Contains(property.Name, StringComparer.Ordinal))
                { if (property.Value.ValueKind != JsonValueKind.String) throw Invalid(); token = Merge(token, Clean(property.Value.GetString()) ?? throw Invalid()); }
                else if (GroupFields.Contains(property.Name, StringComparer.Ordinal))
                { group = Merge(group, GroupValue(property.Value)); }
                else if (property.Name is "user_detail" or "user" or "auth")
                { var nested = property.Value.ValueKind == JsonValueKind.String ? Json(property.Value.GetString()!) : property.Value; Visit(nested, depth + 1); }
            }
        }
        foreach (var item in values)
        {
            if (MiniMaxTokenFields.Contains(item.Key, StringComparer.Ordinal)) token = Merge(token, Clean(Unquote(item.Value)) ?? throw Invalid());
            else if (GroupFields.Contains(item.Key, StringComparer.Ordinal)) group = Merge(group, FactoryWorkOsProfile.Identifier(Unquote(item.Value)) ?? throw Invalid());
            else if (item.Key is "user_detail" or "persist:root") Visit(Json(item.Value), 0);
        }
        return (token, group);
    }
    internal static void Validate(ChromiumProviderCredential value)
    {
        if (Profile(value.ProfileDirectory) != value.ProfileDirectory || !Origins(value.Provider, value.Region).Contains(value.Origin, StringComparer.Ordinal)) throw Invalid();
        if (value.Provider == "deepseek") { if (Clean(value.Secret) != value.Secret || value.Secret is null || value.Cookies is not null || value.Group is not null) throw Invalid(); }
        else if (value.Provider == "factory") { _ = FactoryWorkOsProfile.Parse(value.Secret ?? ""); if (value.Cookies is not null || value.Group is not null) throw Invalid(); }
        else
        {
            if (Clean(value.Secret) != value.Secret || FactoryWorkOsProfile.Identifier(value.Group) != value.Group) throw Invalid();
            var jar = BrowserCookieJar.Parse(value.Cookies ?? "", [MiniMaxAuthentication.Domain(value.Region)]);
            var parsed = MiniMaxAuthentication.Parse(jar.Header(MiniMaxAuthentication.PlanUri(value.Region), DateTimeOffset.UtcNow), value.Region) ?? throw Invalid();
            var hertz = CookieSession(parsed.Cookie);
            if (string.IsNullOrEmpty(hertz) || parsed.Group is not null && parsed.Group != value.Group) throw Invalid();
            // Distinct localStorage bearer requires the same explicit group in both stores.
            if (value.Secret is not null && value.Secret != hertz && (value.Group is null || parsed.Group != value.Group)) throw Invalid();
        }
    }
    internal static string MiniMaxFingerprint(MiniMaxWebCredential value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { value.Region, value.Cookie, value.Bearer, value.Group, value.BrowserState }))));
}
