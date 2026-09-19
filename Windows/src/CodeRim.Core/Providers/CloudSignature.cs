using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace CodeRim.Core.Providers;
public static class CloudSignature
{
    public static void Sign(HttpRequestMessage request, byte[] body, string accessKey, string secret, string region, string service,
        bool volcengine = false, string? sessionToken = null, DateTimeOffset? time = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secret) || accessKey.Length > 512 || secret.Length > 32768
            || accessKey.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_')
            || region.Length is 0 or > 64 || !region.All(x => char.IsAsciiLetterOrDigit(x) || x == '-')) throw new InvalidDataException("Invalid cloud signing credential.");
        var date = (time ?? DateTimeOffset.UtcNow).UtcDateTime; var timestamp = date.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture); var uri = request.RequestUri ?? throw new InvalidDataException();
        static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
        static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
        var payloadHash = Hash(body); var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"] = request.Content!.Headers.ContentType!.ToString(), ["host"] = uri.IsDefaultPort ? uri.Host : uri.Authority,
            [volcengine ? "x-date" : "x-amz-date"] = timestamp
        };
        if (volcengine) headers["x-content-sha256"] = payloadHash;
        else
        {
            headers["x-amz-content-sha256"] = payloadHash;
            if (request.Headers.TryGetValues("X-Amz-Target", out var targets)) headers["x-amz-target"] = targets.Single();
            if (!string.IsNullOrWhiteSpace(sessionToken)) headers["x-amz-security-token"] = sessionToken;
        }
        foreach (var (name, value) in headers)
            if (name != "content-type") { request.Headers.Remove(name); request.Headers.Add(name, value); }
        var query = string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2))
            .Select(x => (Key: Uri.EscapeDataString(Uri.UnescapeDataString(x[0])), Value: Uri.EscapeDataString(Uri.UnescapeDataString(x.Length > 1 ? x[1] : ""))))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        var signed = string.Join(';', headers.Keys);
        var canonicalPath = string.Join('/', uri.AbsolutePath.Split('/').Select(x => Uri.EscapeDataString(Uri.UnescapeDataString(x))));
        var canonical = string.Join('\n', request.Method.Method, canonicalPath, query,
            string.Join("", headers.Select(x => x.Key + ":" + x.Value.Trim() + "\n")), signed, payloadHash);
        var terminator = volcengine ? "request" : "aws4_request"; var algorithm = volcengine ? "HMAC-SHA256" : "AWS4-HMAC-SHA256";
        var scope = day + "/" + region + "/" + service + "/" + terminator;
        var toSign = string.Join('\n', algorithm, timestamp, scope, Hash(Encoding.UTF8.GetBytes(canonical)));
        var key = Hmac(Encoding.UTF8.GetBytes((volcengine ? "" : "AWS4") + secret), day);
        key = Hmac(key, region); key = Hmac(key, service); key = Hmac(key, terminator);
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", algorithm + " Credential=" + accessKey + "/" + scope + ", SignedHeaders=" + signed + ", Signature=" + Convert.ToHexStringLower(Hmac(key, toSign)));
    }
}
