using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Windows.Services;
namespace CodeRim.Windows.Views;
internal static partial class NativeSmoke
{
    private sealed class FactoryFixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return action(request); }
    }
    private static HttpResponseMessage FactoryFixtureResponse(HttpRequestMessage request) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri!.Host == "api.workos.com"
            ? """{"access_token":"native-fixture-access","refresh_token":"rotated-native-fixture"}"""
            : request.RequestUri.AbsolutePath == "/api/app/auth/me" ? """{"userProfile":{"id":"native-member"}}"""
            : """{"usesTokenRateLimitsBilling":true,"limits":{"standard":{"fiveHour":{"usedPercent":17}}}}""")
    };
    private static async Task FactorySessionRegression(CredentialVault vault, AppSettings settings)
    {
        const string key = "provider:factory";
        try
        {
            vault.Save(key, """{"refresh_token":"native-fixture-refresh"}""");
            var original = vault.LoadVersioned(key)!;
            var posts = 0;
            using (var connections = new ProviderConnections(vault, new NativeProviders(new FactoryFixtureHandler(request =>
            {
                if (request.RequestUri!.Host == "api.workos.com") posts++;
                return Task.FromResult(FactoryFixtureResponse(request));
            }))))
            {
                var firstScope = connections.Scope("factory");
                var first = await connections.FetchAsync("factory", settings, CancellationToken.None);
                Require(first.State == ReadingState.Ready && first.Headline?.UsedPercent == 17, "Factory saved WorkOS profile did not refresh and load quota");
                var changed = vault.LoadVersioned(key)!;
                Require(changed.Version != original.Version && FactoryWorkOsProfile.Parse(changed.Value).RefreshToken == "rotated-native-fixture", "Factory DPAPI rotation was not committed");
                Require(firstScope != connections.Scope("factory"), "Rotated login did not invalidate its prior cache");
                var second = await connections.FetchAsync("factory", settings, CancellationToken.None);
                Require(second.State == ReadingState.Ready && posts == 1, "A post-rotation refresh loop made another token exchange");
            }
            foreach (var remove in new[] { false, true })
            {
                vault.Save(key, """{"refresh_token":"late-fixture-refresh"}""");
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var quotaCalls = 0;
                using var connections = new ProviderConnections(vault, new NativeProviders(new FactoryFixtureHandler(async request =>
                {
                    if (request.RequestUri!.Host == "api.workos.com") { entered.SetResult(); await release.Task.ConfigureAwait(false); }
                    else quotaCalls++;
                    return FactoryFixtureResponse(request);
                })));
                var fetch = connections.FetchAsync("factory", settings, CancellationToken.None);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (remove) vault.Delete(key); else vault.Save(key, "different-native-account");
                release.SetResult(); var result = await fetch;
                Require(result.State == ReadingState.Unavailable && quotaCalls == 0, "Stale Factory refresh continued after its account changed");
                Require(vault.Load(key) == (remove ? null : "different-native-account"), "Late refresh replaced or resurrected an account");
            }
            vault.Save(key, "same-fixture-value"); var old = vault.LoadVersioned(key)!;
            vault.Save(key, "same-fixture-value");
            Require(!vault.SaveIfUnchanged(key, old.Version, "late-fixture"), "Reconnecting identical text reused a stale DPAPI revision");
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            {
                for (var n = 0; n < 8; n++) vault.Save("native.concurrent", JsonSerializer.Serialize(new { worker = i, sequence = n }));
            })));
            using var parsed = JsonDocument.Parse(vault.Load("native.concurrent")!);
            Require(parsed.RootElement.GetProperty("worker").GetInt32() is >= 0 and < 4, "Concurrent DPAPI writes did not preserve a complete value");
            vault.Delete("native.concurrent");
        }
        finally { vault.Delete(key); }
    }
}
