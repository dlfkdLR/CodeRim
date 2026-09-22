using System.Text.Json;
namespace CodeRim.Core.Providers;

public sealed record FactoryWorkOsProfile(string? AccessToken, string? RefreshToken, string? OrganizationId, string? ClientId, DateTimeOffset? RefreshedAt)
{
    public static IReadOnlyList<string> ClientIds { get; } = Array.AsReadOnly(new[] { "client_01HXRMBQ9BJ3E7QSTQ9X2PHVB7", "client_01HNM792M5G5G1A2THWPXKFMXB" });
    public static FactoryWorkOsProfile Parse(string json)
    {
        if (json.Length > 262144) throw new InvalidDataException("Factory session profile is too large.");
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Factory session profile.");
        string? Field(params string[] names)
        {
            string? found = null;
            foreach (var property in root.EnumerateObject().Where(p => names.Contains(p.Name, StringComparer.Ordinal)))
            {
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                if (property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid Factory session field.");
                var value = property.Value.GetString()?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                if (found is not null && found != value) throw new InvalidDataException("Ambiguous Factory session field.");
                found = value;
            }
            return found;
        }
        var access = Token(Field("access_token", "accessToken", "workos:access-token"));
        var refresh = Token(Field("refresh_token", "refreshToken", "workos:refresh-token"));
        if (access is null && refresh is null) throw new InvalidDataException("Factory session profile has no token.");
        var org = Identifier(Field("organization_id", "organizationId"));
        var client = Field("client_id", "clientId");
        if (client is not null && !ClientIds.Contains(client)) throw new InvalidDataException("Unsupported Factory client.");
        var refreshed = DateTimeOffset.TryParse(Field("refreshed_at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp) ? timestamp : (DateTimeOffset?)null;
        return new(access, refresh, org, client, refreshed);
    }
    public static string? Token(string? value)
    {
        value = value?.Trim(); if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 32768 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && !"._~+/=-".Contains(c))) throw new InvalidDataException("Invalid Factory session token.");
        return value;
    }
    public static string? Identifier(string? value)
    {
        value = value?.Trim(); if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 256 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')) throw new InvalidDataException("Invalid Factory organization.");
        return value;
    }
    public override string ToString() => "Factory session profile";
    public string Serialize() => JsonSerializer.Serialize(new { access_token = AccessToken, refresh_token = RefreshToken, organization_id = OrganizationId, client_id = ClientId, refreshed_at = RefreshedAt });
    public bool RefreshedRecently(DateTimeOffset now) => RefreshedAt is { } at && at <= now.AddMinutes(1) && at >= now.AddMinutes(-1);
    public bool AccessExpired(DateTimeOffset now)
    {
        if (AccessToken is null) return true;
        var parts = AccessToken.Split('.'); if (parts.Length != 3) return false;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            return json.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetDouble(out var seconds) && seconds <= now.ToUnixTimeSeconds();
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException) { return false; }
    }
}
