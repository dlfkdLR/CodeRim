using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Providers;

internal static class ProviderRetryScope
{
    public static string Create(string id, string? credential, string endpoint, object? settings = null)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, credential, endpoint = StableEndpoint(id, endpoint), settings }))));
    private static string StableEndpoint(string id, string endpoint)
    {
        if (id is not ("vertexai" or "fireworks")) return endpoint;
        var uri = new UriBuilder(endpoint);
        uri.Query = string.Join("&", uri.Query.TrimStart('?').Split('&').Where(pair => {
            var name = Uri.UnescapeDataString(pair.Split('=', 2)[0]);
            return id == "vertexai" ? name is not ("interval.startTime" or "interval.endTime")
                : name is not ("startTime" or "endTime");
        }));
        return uri.Uri.AbsoluteUri;
    }
}
