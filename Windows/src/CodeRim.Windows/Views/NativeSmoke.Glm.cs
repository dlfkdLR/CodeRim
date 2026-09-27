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
    private static async Task GlmAuthenticationRegression(AppSettings settings, CredentialVault vault, string directory)
    {
        var keys = ScriptProviders.Catalog["glm"].Settings.Select(setting => setting.Key).ToArray();
        var environment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var profile = GlmAuthentication.Serialize(new GlmCredential("local-cn", "bigmodel-cn", "OpenCode"));
        var calls = new List<(string Host, string? Key)>(); var replaceDuringFetch = false;
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            using var connections = new ProviderConnections(vault, nativeCredentialReader: id => id == "glm" ? profile : null,
                scripts: new ScriptProviders(new AmpFixtureHandler(request =>
                {
                    calls.Add((request.RequestUri!.Host, request.Headers.Authorization!.Parameter));
                    if (replaceDuringFetch) profile = GlmAuthentication.Serialize(new GlmCredential("replacement", "global", "Claude Code"));
                    return new(HttpStatusCode.OK) { Content = new StringContent("""{"success":true,"code":200,"data":{"limits":[{"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":33}]}}""") };
                })));
            vault.Save("setting:glm:Z_AI_QUOTA_ENDPOINT", "https://external.invalid/quota");
            vault.Save("setting:glm:Z_AI_REGION", "global");
            var scope = connections.Scope("glm");
            var local = await connections.FetchAsync("glm", settings, CancellationToken.None);
            Require(local.Headline?.UsedPercent == 33 && local.Account is { Source: "OpenCode", Region: "bigmodel-cn" } && calls.Count > 0
                && calls.All(call => call.Key == "local-cn" && call.Host is "open.bigmodel.cn" or "www.bigmodel.cn"),
                "Borrowed GLM key lost its region or reached a custom host.");
            vault.Delete("setting:glm:Z_AI_QUOTA_ENDPOINT");
            vault.Save("setting:glm:Z_AI_API_KEY", "manual-global"); calls.Clear();
            var manual = await connections.FetchAsync("glm", settings, CancellationToken.None);
            Require(manual.Headline?.UsedPercent == 33 && manual.Account is { Source: "api", Region: "global" }
                && calls.All(call => call == ("api.z.ai", "manual-global")), "Explicit GLM key/region did not override discovery.");
            vault.Save("setting:glm:Z_AI_API_KEY", " "); Environment.SetEnvironmentVariable("Z_AI_API_KEY", "environment-global"); calls.Clear();
            Require((await connections.FetchAsync("glm", settings, CancellationToken.None)).Headline?.UsedPercent == 33
                && calls.All(call => call == ("api.z.ai", "environment-global")), "Blank saved GLM key suppressed the environment key.");
            Environment.SetEnvironmentVariable("Z_AI_API_KEY", null); replaceDuringFetch = true;
            var changed = await connections.FetchAsync("glm", settings, CancellationToken.None);
            Require(changed.State == ReadingState.Unavailable && changed.Windows.Count == 0 && changed.Account is null && connections.Scope("glm") != scope,
                "GLM account replacement published stale quota.");
            File.WriteAllText(Path.Combine(directory, "windows-glm-auth-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, requests = "in-memory only", regionAndHostIsolation = "PASS",
                explicitEnvironmentLocalPrecedence = "PASS", credentialReplacementInvalidation = "PASS", dpapi = "PASS"
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in keys) { Environment.SetEnvironmentVariable(key, environment[key]); vault.Delete("setting:glm:" + key); }
        }
    }
}
