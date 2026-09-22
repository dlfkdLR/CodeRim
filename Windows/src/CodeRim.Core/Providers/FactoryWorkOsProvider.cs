using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    /// <summary>Refresh one explicit Factory profile. The callback commits only to the version originally read.</summary>
    public async Task<ProviderReading> FetchFactorySessionAsync(string credential, Func<string, string?> setting,
        Func<string, bool>? saveRotated = null, CancellationToken token = default)
    {
        FactoryWorkOsProfile profile;
        try { profile = FactoryWorkOsProfile.Parse(credential); }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        { return new("factory", ReadingState.NeedsAuth, [], Message: "Enter a valid Factory session JSON or reconnect."); }
        token.ThrowIfCancellationRequested();
        foreach (var expired in retryAfter.Where(pair => pair.Value <= DateTimeOffset.Now)) retryAfter.TryRemove(expired.Key, out _);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (profile.AccessToken is not null && (!profile.AccessExpired(now) || profile.RefreshedRecently(now)))
            {
                var current = await FetchAsync("factory", "Bearer " + profile.AccessToken, setting, deadline.Token).ConfigureAwait(false);
                if (current.State != ReadingState.NeedsAuth || profile.RefreshToken is null || profile.RefreshedRecently(now)) return current;
            }
            if (profile.RefreshToken is null) return new("factory", ReadingState.NeedsAuth, [], Message: "The Factory session expired. Reconnect or provide its refresh token.");
            profile = await RefreshFactorySession(profile, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (saveRotated is not null && !saveRotated(profile.Serialize()))
                return new("factory", ReadingState.Unavailable, [], Message: "The connection changed during refresh. Refresh the selected account.");
            return await FetchAsync("factory", "Bearer " + profile.AccessToken, setting, deadline.Token).ConfigureAwait(false);
        }
        catch (ProviderRequestException error)
        {
            var state = error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest ? ReadingState.NeedsAuth
                : error.Status == HttpStatusCode.TooManyRequests ? ReadingState.Unavailable : ReadingState.Error;
            return new("factory", state, [], Message: state == ReadingState.NeedsAuth ? "The Factory session could not be renewed. Reconnect this account." : "Factory session refresh failed. Retry later.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); return new("factory", ReadingState.Error, [], Message: "Factory session refresh failed. Retry later."); }
    }
    private async Task<FactoryWorkOsProfile> RefreshFactorySession(FactoryWorkOsProfile profile, CancellationToken token)
    {
        var clients = profile.ClientId is { } selected ? new[] { selected } : FactoryWorkOsProfile.ClientIds;
        for (var index = 0; index < clients.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            const string endpoint = "https://api.workos.com/user_management/authenticate";
            var scope = ProviderRetryScope.Create("factory", profile.RefreshToken, endpoint,
                new { profile.OrganizationId, ClientId = clients[index] });
            if (retryAfter.TryGetValue(scope, out var retry) && retry > DateTimeOffset.Now)
                throw new ProviderRequestException(HttpStatusCode.TooManyRequests);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var body = new Dictionary<string, string> { ["client_id"] = clients[index], ["grant_type"] = "refresh_token", ["refresh_token"] = profile.RefreshToken! };
            if (profile.OrganizationId is not null) body["organization_id"] = profile.OrganizationId;
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                retryAfter[scope] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.Now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            if (!response.IsSuccessStatusCode)
            {
                // Only rejected public-client combinations try the other pinned Factory client.
                if (index + 1 < clients.Count && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) continue;
                throw new ProviderRequestException((int)response.StatusCode is >= 300 and < 400 ? HttpStatusCode.Unauthorized : response.StatusCode);
            }
            if (response.Content.Headers.ContentLength > 262144) throw new InvalidDataException("Factory refresh response is too large.");
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > 262144) throw new InvalidDataException("Factory refresh response is too large.");
                bytes.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(bytes.ToArray()); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Factory refresh response.");
            string? ResponseField(string key)
            {
                var seen = false; string? value = null;
                foreach (var property in root.EnumerateObject().Where(p => p.Name == key))
                {
                    if (seen) throw new InvalidDataException("Duplicate Factory refresh field.");
                    seen = true;
                    if (property.Value.ValueKind == JsonValueKind.Null) continue;
                    if (property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid Factory refresh field.");
                    value = property.Value.GetString()?.Trim();
                    if (string.IsNullOrEmpty(value)) throw new InvalidDataException("Empty Factory refresh field.");
                }
                return value;
            }
            var access = FactoryWorkOsProfile.Token(ResponseField("access_token")) ?? throw new InvalidDataException("Factory refresh returned no access token.");
            var refresh = FactoryWorkOsProfile.Token(ResponseField("refresh_token")) ?? profile.RefreshToken;
            var org = FactoryWorkOsProfile.Identifier(ResponseField("organization_id")) ?? profile.OrganizationId;
            if (profile.OrganizationId is not null && org != profile.OrganizationId) throw new InvalidDataException("Factory refreshed another organization.");
            var previousUser = FactorySubject(profile.AccessToken ?? ""); var nextUser = FactorySubject(access);
            if (previousUser is not null && nextUser is not null && previousUser != nextUser) throw new InvalidDataException("Factory refreshed another user.");
            return new(access, refresh, org, clients[index], DateTimeOffset.UtcNow);
        }
        throw new ProviderRequestException(HttpStatusCode.Unauthorized);
    }
}
