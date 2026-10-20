using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class AppServerProtocolTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "rpc-contract-" + Guid.NewGuid().ToString("N"));
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "ProcessFixture",
        OperatingSystem.IsWindows() ? "CodeRim.ProcessFixture.exe" : "CodeRim.ProcessFixture");
    private Dictionary<string, string?> ChildEnvironment()
    {
        Directory.CreateDirectory(root);
        return new()
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
            ["HOME"] = root, ["CODEX_HOME"] = Path.Combine(root, "isolated 계정"),
            ["SYNTHETIC_RUN_DIR"] = root, ["TEMP"] = root, ["TMP"] = root,
            ["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."))
        };
    }
    [Theory]
    [InlineData("account/read")]
    [InlineData("config/read")]
    [InlineData("account/rateLimits/read")]
    public async Task ProductionClientSendsMethodSpecificReadOnlyParams(string method)
    {
        var environment = ChildEnvironment();
        var response = await AppServerClient.ReadAsync(Executable, method, environment, false, TestContext.Current.CancellationToken);
        Assert.Equal(method, response.GetProperty("method").GetString());
        Assert.Equal(environment["CODEX_HOME"], response.GetProperty("home").GetString());
        var parameters = response.GetProperty("parameters");
        if (method == "account/read") Assert.False(parameters.GetProperty("refreshToken").GetBoolean());
        else if (method == "config/read") Assert.False(parameters.GetProperty("includeLayers").GetBoolean());
        else Assert.Equal(JsonValueKind.Null, parameters.ValueKind);
    }
    [Fact]
    public async Task StrictChildRejectsThePreviousAccountRequest()
    {
        var environment = ChildEnvironment();
        var result = await BoundedProcess.RunResultAsync(Executable, ["app-server"],
            input: "{\"id\":2,\"method\":\"account/read\"}\n", environment: environment,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        using var response = JsonDocument.Parse(result.Output);
        Assert.Equal(-32600, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
