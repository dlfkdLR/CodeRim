using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class LoginIdentityTests
{
    private static string Login(string organization)
    {
        var claims = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["email"] = "fixture@example.invalid", ["sub"] = "same-subject",
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = organization }
        });
        var token = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
        return JsonSerializer.Serialize(new { tokens = new { account_id = organization, id_token = token, access_token = "fixture-access", refresh_token = "fixture-refresh" } });
    }
    [Fact]
    public void SameEmailDifferentWorkspacesHaveDifferentIdentity()
    {
        var a = LoginIdentity.Codex(Login("A")); var b = LoginIdentity.Codex(Login("B"));
        Assert.Equal(a.Email, b.Email); Assert.NotEqual(a.Id, b.Id);
    }
    [Theory]
    [InlineData("keyring")]
    [InlineData("auto")]
    [InlineData("ephemeral")]
    public void NonFileStorageCannotBeSwitched(string storage)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { cli_auth_credentials_store = storage }));
        Assert.Throws<InvalidOperationException>(() => LoginIdentity.ValidateCodexPolicy(document.RootElement, "A"));
    }
    [Fact]
    public void ManagedWorkspaceAllowlistIsEnforced()
    {
        using var document = JsonDocument.Parse("""{"cli_auth_credentials_store":"file","forced_chatgpt_workspace_id":["A","B"]}""");
        LoginIdentity.ValidateCodexPolicy(document.RootElement, "B");
        Assert.Throws<InvalidOperationException>(() => LoginIdentity.ValidateCodexPolicy(document.RootElement, "C"));
    }
    [Fact]
    public void ApiKeyLoginIsNotASavedSubscription()
    {
        Assert.Throws<InvalidDataException>(() => LoginIdentity.Codex("""{"OPENAI_API_KEY":"fixture-key"}"""));
    }
}
