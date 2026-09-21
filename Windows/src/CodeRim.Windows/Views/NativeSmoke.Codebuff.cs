using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task CodebuffAuthenticationRegression(AppSettings settings, CredentialVault vault, string directory)
    {
        var environment = Environment.GetEnvironmentVariable("CODEBUFF_API_KEY");
        var root = Path.Combine(Path.GetTempPath(), "CodeRim-Codebuff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); CredentialVault.RestrictDirectory(root);
        var file = Path.Combine(root, "credentials.json"); var calls = new List<string>(); var replaceDuringFetch = false;
        try
        {
            GuardedFile.WritePrivate(file, """{"default":{"authToken":"local"}}""");
            Environment.SetEnvironmentVariable("CODEBUFF_API_KEY", " 'environment' ");
            using var connections = new ProviderConnections(vault, new NativeProviders(new AmpFixtureHandler(request =>
            {
                calls.Add(request.RequestUri!.AbsolutePath + "|" + request.Headers.Authorization!.Parameter);
                if (replaceDuringFetch) File.WriteAllText(file, """{"authToken":"replacement"}""");
                return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath == "/api/v1/usage"
                    ? """{"usage":12,"quota":100}""" : """{"subscription":{"displayName":"Pro"}}""") };
            })), nativeCredentialReader: id => id == "codebuff" ? CodebuffAuthentication.Read(file) : null);
            vault.Save("provider:codebuff", " 'saved' ");
            Require((await connections.FetchAsync("codebuff", settings, CancellationToken.None)).State == ReadingState.Ready
                && calls.Count == 1 && calls[0] == "/api/v1/usage|saved", "Saved Codebuff API key did not take precedence.");
            calls.Clear(); vault.Delete("provider:codebuff");
            Require((await connections.FetchAsync("codebuff", settings, CancellationToken.None)).State == ReadingState.Ready
                && calls.Count == 1 && calls[0] == "/api/v1/usage|environment", "Codebuff environment key was not cleaned or requested subscription.");
            Environment.SetEnvironmentVariable("CODEBUFF_API_KEY", null); calls.Clear();
            var scope = connections.Scope("codebuff");
            Require((await connections.FetchAsync("codebuff", settings, CancellationToken.None)).Plan == "Pro"
                && calls.Count == 2 && calls[0] == "/api/v1/usage|local" && calls[1] == "/api/user/subscription|local", "Codebuff local login did not enrich subscription.");
            replaceDuringFetch = true;
            var changed = await connections.FetchAsync("codebuff", settings, CancellationToken.None);
            Require(changed.State == ReadingState.Unavailable && changed.Windows.Count == 0 && connections.Scope("codebuff") != scope,
                "Codebuff login replacement published the previous account's reading.");
            File.WriteAllText(Path.Combine(directory, "windows-codebuff-auth-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, requests = "in-memory only", savedEnvironmentFilePrecedence = "PASS",
                localSubscriptionOnly = "PASS", credentialReplacementInvalidation = "PASS", dpapi = "PASS"
            }, JsonOptions));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEBUFF_API_KEY", environment); vault.Delete("provider:codebuff");
            Directory.Delete(root, recursive: true);
        }
    }
}
