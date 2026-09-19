using System.Text.Json.Nodes;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;

public sealed class ClaudeHookInstallerTests
{
    [Fact]
    public void MovingPackageReplacesOnlyOwnedHooks()
    {
        const string initial = """{"keep":42,"hooks":{"SessionStart":[{"matcher":"startup","hooks":[{"type":"command","command":"user-command"}]}],"Stop":[{"hooks":[{"command":"other-command"}]}]}}""";
        var first = ClaudeHookInstaller.Configure(initial, @"C:\Old folder\CodeRimCLI.exe");
        var moved = ClaudeHookInstaller.Configure(first, @"D:\New ' folder\CodeRimCLI.exe");
        Assert.Equal(moved, ClaudeHookInstaller.Configure(moved, @"D:\New ' folder\CodeRimCLI.exe"));
        var root = JsonNode.Parse(moved)!; Assert.Equal(42, root["keep"]!.GetValue<int>());
        var commands = root["hooks"]!["SessionStart"]!.AsArray().SelectMany(x => x!["hooks"]!.AsArray()).Select(x => x!["command"]!.GetValue<string>()).ToArray();
        Assert.Equal(2, commands.Length); Assert.Contains("user-command", commands);
        Assert.Contains(ClaudeHookInstaller.Command("claude-session-start", @"D:\New ' folder\CodeRimCLI.exe"), commands);
        Assert.Single(root["hooks"]!["Stop"]!.AsArray());
    }
    [Fact]
    public void OtherStatusLineRequiresExplicitReplacement()
    {
        const string initial = """{"statusLine":{"type":"command","command":"user-status-line"}}""";
        Assert.Throws<InvalidOperationException>(() => ClaudeHookInstaller.Configure(initial, @"C:\CodeRimCLI.exe"));
        Assert.NotEmpty(ClaudeHookInstaller.Configure(initial, @"C:\CodeRimCLI.exe", true));
    }
}
