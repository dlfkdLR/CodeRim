using System.Net;
using System.Text.RegularExpressions;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

/// <summary>
/// Every catalogue provider through every connection outcome a user can hit: nothing configured,
/// a refused credential, a broken server, no network, a timeout and unreadable answers. Each
/// must end in a state that tells the user what to do — never a crash, never a fake success,
/// and never a sign-in prompt for what is really a network problem.
/// </summary>
public sealed class ProviderMatrixTests
{
    public enum Scenario
    {
        Unconfigured, Unauthorized, Forbidden, NotFound, RateLimited, ServerError, BadGateway, Unavailable, GatewayTimeout,
        Offline, DnsFailure, TlsFailure, Timeout, ConnectionReset, EmptyJson, EmptyBody, JsonArray, NullJson, Truncated, Html, LoginRedirect,
    }

    private sealed class Server(Scenario scenario) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return scenario switch
            {
                Scenario.Offline => throw new HttpRequestException("No route to host", new System.Net.Sockets.SocketException(10065)),
                Scenario.DnsFailure => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."),
                Scenario.TlsFailure => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.",
                    new System.Security.Authentication.AuthenticationException("certificate")),
                Scenario.ConnectionReset => throw new HttpRequestException("Connection reset", new IOException("reset", new System.Net.Sockets.SocketException(10054))),
                Scenario.Timeout => throw new TaskCanceledException("The request timed out.", new TimeoutException()),
                Scenario.Unauthorized => Task.FromResult(Reply(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""")),
                Scenario.Forbidden => Task.FromResult(Reply(HttpStatusCode.Forbidden, """{"error":"forbidden"}""")),
                Scenario.ServerError => Task.FromResult(Reply(HttpStatusCode.InternalServerError, """{"error":"internal"}""")),
                Scenario.NotFound => Task.FromResult(Reply(HttpStatusCode.NotFound, """{"error":"not found"}""")),
                Scenario.RateLimited => Task.FromResult(Reply(HttpStatusCode.TooManyRequests, """{"error":"rate limited"}""")),
                Scenario.BadGateway => Task.FromResult(Reply(HttpStatusCode.BadGateway, "<html>502</html>", "text/html")),
                Scenario.Unavailable => Task.FromResult(Reply(HttpStatusCode.ServiceUnavailable, """{"error":"maintenance"}""")),
                Scenario.GatewayTimeout => Task.FromResult(Reply(HttpStatusCode.GatewayTimeout, "")),
                Scenario.EmptyBody => Task.FromResult(Reply(HttpStatusCode.OK, "")),
                Scenario.JsonArray => Task.FromResult(Reply(HttpStatusCode.OK, "[]")),
                Scenario.NullJson => Task.FromResult(Reply(HttpStatusCode.OK, "null")),
                Scenario.Truncated => Task.FromResult(Reply(HttpStatusCode.OK, """{"data":{"usage":[{"used":""")),
                Scenario.LoginRedirect => Task.FromResult(Redirect()),
                Scenario.Html => Task.FromResult(Reply(HttpStatusCode.OK, "<!doctype html><html><body>Maintenance</body></html>", "text/html")),
                _ => Task.FromResult(Reply(HttpStatusCode.OK, "{}")),
            };
        }
        private static HttpResponseMessage Redirect()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
            response.Headers.Location = new Uri("https://login.example.invalid/signin");
            return response;
        }
        private static HttpResponseMessage Reply(HttpStatusCode status, string body, string type = "application/json")
            => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, type) };
    }

    private static readonly Regex Secret = new("KEY|TOKEN|SECRET|PASSWORD|COOKIE|SESSION|JWT", RegexOptions.CultureInvariant);

    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in ProviderCatalog.All)
        {
            if (provider.Id is "codex" or "claude" or "jetbrains") continue; // local files and app-server; covered by their own suites
            foreach (var scenario in Enum.GetValues<Scenario>()) yield return [provider.Id, scenario];
        }
    }

    internal sealed record Outcome(string Id, Scenario Scenario, ReadingState? State, string? Message, int Windows, int Requests, string? Crash)
    {
        public double[] Values { get; init; } = [];
    }

    internal static async Task<Outcome> RunAsync(string id, Scenario scenario)
    {
        var server = new Server(scenario);
        var configured = scenario != Scenario.Unconfigured;
        var definition = ProviderCatalog.Find(id)!;
        // Shapes a real user would paste: each reader rejects malformed input before any request,
        // so the network outcomes are only exercised with credentials of the right form.
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0IiwiZXhwIjo0MTAyNDQ0ODAwLCJodHRwczovL2dyb3EuY29tL29yZ2FuaXphdGlvbiI6eyJpZCI6Im9yZ190ZXN0In19.c2lnbmF0dXJl";
        string? Setting(string key) => !configured ? null : key switch
        {
            "XAI_TEAM_ID" => "team-test", "FIREWORKS_ACCOUNT_SLUG" => "account-test", "GROQ_SESSION_JWT" => jwt,
            "MINIMAX_USAGE_SOURCE" => "api", "MINIMAX_REGION" => "global", "WAYFINDER_GATEWAY_URL" => "http://127.0.0.1:8088",
            _ when key.EndsWith("_URL", StringComparison.Ordinal) || key.EndsWith("_HOST", StringComparison.Ordinal) || key.EndsWith("_PATH", StringComparison.Ordinal) => null,
            _ when Secret.IsMatch(key) => "test-credential-0123456789",
            _ => null,
        };
        var credential = !configured ? null : id switch
        {
            "groq" => jwt,
            "mimo" => "api-platform_serviceToken=test-credential; userId=1001",
            "notion" => "token_v2=test-credential-0123456789",
            "opencode-zen" => "auth=test-credential-0123456789",
            _ => "test-credential-0123456789",
        };
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            ProviderReading reading;
            if (HttpProviders.Supported.Contains(id))
            { using var http = new HttpProviders(server); reading = await http.FetchAsync(id, credential, Setting, cancellation.Token); }
            else if (NativeProviders.Supported.Contains(id))
            { using var native = new NativeProviders(server);
                Func<Uri, string?>? cookies = !configured ? null : id switch
                {
                    "mimo" or "notion" or "opencode-zen" => _ => credential,
                    "groq" => _ => "stytch_session_jwt=" + jwt,
                    _ => null,
                };
                reading = await native.FetchAsync(id, credential, Setting, cookies, cancellation.Token); }
            else if (ScriptProviders.Catalog.ContainsKey(id))
            { using var scripts = new ScriptProviders(server); reading = await scripts.FetchAsync(id, Setting, configured ? "session=test-credential" : null, cancellation.Token); }
            else return new(id, scenario, null, "no connector", 0, server.Requests, "No Windows connector");
            _ = definition;
            return new(id, scenario, reading.State, reading.Message, reading.Windows.Count, server.Requests, null)
                { Values = reading.Windows.Where(w => w.UsedPercent is not null).Select(w => w.UsedPercent!.Value).ToArray() };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(id, scenario, null, null, 0, server.Requests, error.GetType().Name + ": " + error.Message); }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryProviderEndsEveryConnectionOutcomeInAnActionableState(string id, Scenario scenario)
    {
        var outcome = await RunAsync(id, scenario);
        Assert.True(outcome.Crash is null, $"{id}/{scenario} threw {outcome.Crash}");
        var state = outcome.State!.Value;
        // A broken answer must never look like a connected account with usage.
        Assert.False(state is ReadingState.Ready or ReadingState.Partial && outcome.Windows == 0 && scenario != Scenario.EmptyJson,
            $"{id}/{scenario} reported {state} without any reading");
        Assert.False(scenario is not (Scenario.EmptyJson or Scenario.JsonArray or Scenario.NullJson or Scenario.EmptyBody) && state is ReadingState.Ready or ReadingState.Partial,
            $"{id}/{scenario} reported {state} from a failed or refused request");
        // Whatever is shown must say something, so the user knows what to do next.
        if (state is not (ReadingState.Ready or ReadingState.Partial or ReadingState.Loading))
            Assert.False(string.IsNullOrWhiteSpace(outcome.Message), $"{id}/{scenario} ended {state} with no message");
        // Network trouble is not a sign-in problem: a sign-in prompt would send the user on a useless detour.
        if (scenario is Scenario.Offline or Scenario.Timeout or Scenario.ServerError or Scenario.BadGateway or Scenario.Unavailable
            or Scenario.GatewayTimeout or Scenario.DnsFailure or Scenario.TlsFailure or Scenario.ConnectionReset or Scenario.RateLimited)
            Assert.False(state == ReadingState.NeedsAuth, $"{id}/{scenario} asked to sign in again for a network/server failure after {outcome.Requests} request(s): {outcome.Message}");
        // An unexpected answer must not leave a reading the user takes for a quota: no windows without a value.
        if (state is ReadingState.Ready or ReadingState.Partial)
            Assert.DoesNotContain(outcome.Values, value => value is < 0 or > 1000);
        // A refused credential is a sign-in problem: say so, so the user knows to reconnect rather than wait.
        if (scenario == Scenario.Unauthorized && outcome.Requests > 0)
            Assert.True(state == ReadingState.NeedsAuth, $"{id}/{scenario} ended {state} instead of asking to reconnect: {outcome.Message}");
        // A provider with nothing configured asks to be connected rather than showing an error.
        if (scenario == Scenario.Unconfigured)
            Assert.False(state == ReadingState.Error, $"{id} with nothing configured shows an error instead of a connect prompt: {outcome.Message}");
    }
}
