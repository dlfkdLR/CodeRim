using System.Net;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static void ConfigureWindsurf(HttpRequestMessage request, string credential)
    {
        using var auth = JsonDocument.Parse(credential);
        string Field(string key, string alias, string camel, string? extra = null)
        {
            var value = new[] { key, alias, camel, extra }.Where(x => x is not null).Select(x => Text(auth.RootElement, x!)?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            return value is { Length: > 0 and <= 32768 } && !value.Any(char.IsControl) ? value : throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        }
        var session = Field("devin_session_token", "sessionToken", "devinSessionToken");
        request.Method = HttpMethod.Post;
        request.Headers.Add("Origin", "https://windsurf.com"); request.Headers.Referrer = new Uri("https://windsurf.com/profile");
        request.Headers.Add("Connect-Protocol-Version", "1"); request.Headers.Add("x-auth-token", session); request.Headers.Add("x-devin-session-token", session);
        request.Headers.Add("x-devin-auth1-token", Field("devin_auth1_token", "auth1Token", "devinAuth1Token"));
        request.Headers.Add("x-devin-account-id", Field("devin_account_id", "accountID", "devinAccountId", "accountId"));
        request.Headers.Add("x-devin-primary-org-id", Field("devin_primary_org_id", "primaryOrgID", "devinPrimaryOrgId", "primaryOrgId"));
        var bytes = Encoding.UTF8.GetBytes(session); var body = new List<byte> { 10 };
        var length = (uint)bytes.Length;
        while (length >= 128) { body.Add((byte)((length & 127) | 128)); length >>= 7; }
        body.Add((byte)length); body.AddRange(bytes); body.Add(16); body.Add(1);
        request.Content = new ByteArrayContent(body.ToArray()); request.Content.Headers.ContentType = new("application/proto");
    }
    private sealed record ProtoValue(int Number, ulong? Integer, byte[]? Bytes);
    private static List<ProtoValue> ProtoFields(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException();
        var index = 0; var fields = new List<ProtoValue>();
        ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (index >= bytes.Length) throw new InvalidDataException("Truncated protobuf.");
                var b = bytes[index++]; if (shift == 63 && b > 1) throw new InvalidDataException("Invalid protobuf integer.");
                value |= (ulong)(b & 127) << shift; if (b < 128) return value;
            }
            throw new InvalidDataException();
        }
        while (index < bytes.Length)
        {
            if (fields.Count >= 10000) throw new InvalidDataException("Too many protobuf fields.");
            var key = Varint(); if (key >> 3 is 0 or > 536870911) throw new InvalidDataException("Invalid protobuf field.");
            var number = (int)(key >> 3); var wire = key & 7;
            if (wire == 0) { fields.Add(new(number, Varint(), null)); continue; }
            var length = wire switch { 1 => 8UL, 2 => Varint(), 5 => 4UL, _ => throw new InvalidDataException("Unsupported protobuf wire type.") };
            if (length > (ulong)(bytes.Length - index)) throw new InvalidDataException("Truncated protobuf field.");
            if (wire == 2) fields.Add(new(number, null, bytes.AsSpan(index, (int)length).ToArray()));
            index += (int)length;
        }
        return fields;
    }
    private static ProviderReading ParseWindsurf(JsonElement payload)
    {
        var root = ProtoFields(Convert.FromBase64String(Text(payload, "protobuf") ?? ""));
        var body = root.LastOrDefault(x => x.Number == 1 && x.Bytes is not null)?.Bytes;
        if (body is null) throw new InvalidDataException("Missing Windsurf plan status.");
        var fields = ProtoFields(body);
        ulong? Int(int key) => fields.LastOrDefault(x => x.Number == key && x.Integer.HasValue)?.Integer;
        DateTimeOffset? Reset(int key) => Int(key) is > 0 and <= 253402300799 and var value ? DateTimeOffset.FromUnixTimeSeconds((long)value) : null;
        string? plan = null;
        if (fields.LastOrDefault(x => x.Number == 1 && x.Bytes is not null)?.Bytes is { } info)
        {
            var name = ProtoFields(info).LastOrDefault(x => x.Number == 2 && x.Bytes is not null)?.Bytes;
            if (name is { Length: <= 1024 })
            {
                try { plan = new UTF8Encoding(false, true).GetString(name); }
                catch (DecoderFallbackException) { throw new InvalidDataException("Invalid Windsurf plan name."); }
            }
        }
        var windows = new List<LimitWindow>();
        if (Int(14) is <= 100 and var daily) windows.Add(new("daily", "Daily", 100 - (double)daily, Reset(17), 1440));
        if (Int(15) is <= 100 and var weekly) windows.Add(new("weekly", "Weekly", 100 - (double)weekly, Reset(18), 10080));
        return Metered("windsurf", windows, plan);
    }
}
