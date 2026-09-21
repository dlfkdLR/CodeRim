using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;

public sealed class GlmAuthenticationTests
{
    [Theory]
    [InlineData("https://api.z.ai/api/anthropic", "global")]
    [InlineData("https://open.bigmodel.cn/api/anthropic", "bigmodel-cn")]
    [InlineData("https://edge.z.ai/path", "global")]
    [InlineData("https://api.anthropic.com", null)]
    [InlineData("https://api.z.ai.evil.invalid", null)]
    [InlineData("https://user@api.z.ai", null)]
    [InlineData("file://api.z.ai/path", null)]
    [InlineData("", null)]
    public void ClaimsOnlyItsVendorAndKeepsRegion(string address, string? expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { env = new { ANTHROPIC_AUTH_TOKEN = "primary", ANTHROPIC_API_KEY = "secondary", ANTHROPIC_BASE_URL = address } });
        var result = GlmAuthentication.Parse("claude", json);
        Assert.Equal(expected, result?.Region); if (expected is not null) Assert.Equal("primary", result!.Token);
    }
    [Fact]
    public void ZCodeSkipsDisabledForeignMalformedAndEncryptedEntries()
    {
        var result = GlmAuthentication.Parse("zcode-config", """
            {"provider":{
              "a-coding-plan":{"enabled":false,"options":{"apiKey":"disabled"}},
              "b-coding-plan":{"options":{"apiKey":"foreign","baseURL":"https://example.invalid"}},
              "c-coding-plan":{"options":{"apiKey":"broken","baseURL":":bad"}},
              "d-coding-plan":{"options":{"apiKey":"cn","baseURL":"https://open.bigmodel.cn/path"}}
            }}
            """);
        Assert.Equal(new("cn", "bigmodel-cn", "ZCode"), result);
        Assert.Equal(new("plain", "global", "ZCode"), GlmAuthentication.Parse("zcode-credentials", """{"oauth:zai:access_token":"plain"}"""));
        Assert.Null(GlmAuthentication.Parse("zcode-credentials", """{"oauth:zai:access_token":"enc:v1:opaque"}"""));
        Assert.Null(GlmAuthentication.Parse("zcode-config", """{"provider":{"coding-plan":{"options":{"apiKey":"key","baseURL":123}}}}"""));
    }
    [Theory]
    [InlineData("zai-coding-plan", "global")]
    [InlineData("glm", "global")]
    [InlineData("zhipu", "bigmodel-cn")]
    [InlineData("zhipuai", "bigmodel-cn")]
    public void OpenCodeKeepsProviderRegionAndAliases(string id, string region)
    {
        foreach (var field in new[] { "apiKey", "api_key", "token", "key", "accessToken", "auth_token" })
            Assert.Equal(new("opaque", region, "OpenCode"), GlmAuthentication.Parse("opencode", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new Dictionary<string, string> { [field] = "opaque" } })));
    }
    [Fact]
    public void AmbiguousInvalidAndOversizedProfilesAreRejected()
    {
        Assert.Null(GlmAuthentication.Parse("opencode", """{"zai":"first","zai":"second"}"""));
        Assert.Null(GlmAuthentication.Parse("opencode", """{"zai":{"key":"a","key":"b"}}"""));
        Assert.Null(GlmAuthentication.Parse("opencode", """{"zai":"line\nbreak"}"""));
        Assert.Null(GlmAuthentication.Parse("opencode", new string(' ', 262145)));
        Assert.Null(GlmAuthentication.Profile("""{"Token":"key","Region":"other","Source":"OpenCode"}"""));
        var expected = new GlmCredential("key", "bigmodel-cn", "OpenCode");
        Assert.Equal(expected, GlmAuthentication.Profile(GlmAuthentication.Serialize(expected)));
    }
    [Fact]
    public void FilesAreReadInOrderAndReplacementIsObserved()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "CodeRim-GLM-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".zcode", "v2"));
        Directory.CreateDirectory(Path.Combine(root, ".local", "share", "opencode"));
        try
        {
            var zcode = Path.Combine(root, ".zcode", "v2", "credentials.json");
            File.WriteAllText(zcode, """{"oauth:zai:access_token":"first"}""");
            File.WriteAllText(Path.Combine(root, ".local", "share", "opencode", "auth.json"), """{"zhipu":"second"}""");
            Assert.Equal("first", GlmAuthentication.Read(root)!.Token);
            File.WriteAllText(zcode, """{"oauth:zai:access_token":"enc:v1:opaque"}""");
            Assert.Equal(new("second", "bigmodel-cn", "OpenCode"), GlmAuthentication.Read(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
