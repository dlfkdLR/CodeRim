using System.Text.Json.Nodes;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ClaudeHookLifecycleTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "coderim-claude-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(directory, "settings.json");
    private string State => Settings + ".coderim-state.json";
    private const string OldHelper = @"C:\Old folder\CodeRimCLI.exe";
    private const string NewHelper = @"D:\New ' folder\CodeRimCLI.exe";
    public ClaudeHookLifecycleTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private void Write(string json) => File.WriteAllText(Settings, json);
    private JsonObject Read() => JsonNode.Parse(File.ReadAllText(Settings))!.AsObject();

    [Fact]
    public void MigrationRequiresOwnedJournalAndExactInstalledCommand()
    {
        Write(ClaudeHookInstaller.Configure(null, OldHelper));
        Assert.False(ClaudeHookInstaller.HasManagedInstallationAt(Settings));
        ClaudeHookInstaller.InstallAt(Settings, OldHelper);
        Assert.True(ClaudeHookInstaller.HasManagedInstallationAt(Settings));
        var changed = Read(); changed["statusLine"]!["command"] = ClaudeHookInstaller.Command("claude-status", NewHelper); Write(changed.ToJsonString());
        Assert.False(ClaudeHookInstaller.HasManagedInstallationAt(Settings));
        Write("[]"); Assert.False(ClaudeHookInstaller.HasManagedInstallationAt(Settings));
        File.WriteAllText(State, "{}"); Assert.False(ClaudeHookInstaller.HasManagedInstallationAt(Settings));
    }
    [Fact]
    public void OriginalStatusAndUnrelatedSettingsSurviveRepeatedInstallMoveAndDisconnect()
    {
        const string initial = """{"statusLine":{"type":"command","command":"my-status","padding":2},"keep":42,"hooks":{"Stop":[{"hooks":[{"command":"my-stop"}]}],"SessionStart":[{"matcher":"startup","hooks":[{"command":"my-start"}]}]}}""";
        Write(initial);
        ClaudeHookInstaller.InstallAt(Settings, OldHelper, true);
        var first = File.ReadAllText(Settings); var state = File.ReadAllText(State);
        ClaudeHookInstaller.InstallAt(Settings, OldHelper);
        Assert.Equal(first, File.ReadAllText(Settings)); Assert.Equal(state, File.ReadAllText(State));
        ClaudeHookInstaller.InstallAt(Settings, NewHelper);
        var changed = Read(); changed["keep"] = 99; Write(changed.ToJsonString());
        ClaudeHookInstaller.UninstallAt(Settings);
        var expected = JsonNode.Parse(initial)!; expected["keep"] = 99;
        Assert.True(JsonNode.DeepEquals(expected, Read())); Assert.False(File.Exists(State));
        var uninstalled = File.ReadAllText(Settings);
        ClaudeHookInstaller.UninstallAt(Settings);
        Assert.Equal(uninstalled, File.ReadAllText(Settings));
    }
    [Fact]
    public void UserEditedStatusLineWinsOverRestoration()
    {
        Write("""{"statusLine":{"type":"command","command":"original"}}""");
        ClaudeHookInstaller.InstallAt(Settings, OldHelper, true);
        var changed = Read(); changed["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = "user-changed", ["padding"] = 8 };
        var expected = changed["statusLine"]!.DeepClone(); Write(changed.ToJsonString());
        ClaudeHookInstaller.UninstallAt(Settings);
        Assert.True(JsonNode.DeepEquals(expected, Read()["statusLine"])); Assert.False(Read().ContainsKey("hooks"));
    }
    [Fact]
    public void ExplicitReconnectKeepsFirstOriginalAndRequiresApprovalForUserReplacement()
    {
        Write("""{"statusLine":{"type":"command","command":"first"}}""");
        ClaudeHookInstaller.InstallAt(Settings, OldHelper, true);
        var changed = Read(); changed["statusLine"]!["command"] = "second"; Write(changed.ToJsonString());
        var before = File.ReadAllText(Settings); var state = File.ReadAllText(State);
        Assert.Throws<InvalidOperationException>(() => ClaudeHookInstaller.InstallAt(Settings, NewHelper));
        Assert.Equal(before, File.ReadAllText(Settings)); Assert.Equal(state, File.ReadAllText(State));
        ClaudeHookInstaller.InstallAt(Settings, NewHelper, true); ClaudeHookInstaller.UninstallAt(Settings);
        Assert.Equal("first", Read()["statusLine"]!["command"]!.GetValue<string>());
    }
    [Fact]
    public void StateDoesNotAuthorizeADifferentManagedCommand()
    {
        ClaudeHookInstaller.InstallAt(Settings, OldHelper);
        var changed = Read(); changed["statusLine"]!["command"] = ClaudeHookInstaller.Command("claude-status", NewHelper); Write(changed.ToJsonString());
        ClaudeHookInstaller.UninstallAt(Settings);
        Assert.Equal(ClaudeHookInstaller.Command("claude-status", NewHelper), Read()["statusLine"]!["command"]!.GetValue<string>());
    }
    [Fact]
    public void LegacyDisconnectRemovesOnlyOwnedCommandsWithoutGuessingBackups()
    {
        Write(ClaudeHookInstaller.Configure("""{"statusLine":{"command":"lost-original"},"keep":42} """, OldHelper, true));
        File.WriteAllText(Settings + ".coderim-backup-old", """{"statusLine":{"command":"unrelated-backup"}}""");
        ClaudeHookInstaller.UninstallAt(Settings);
        Assert.Equal(42, Read()["keep"]!.GetValue<int>()); Assert.False(Read().ContainsKey("statusLine")); Assert.False(Read().ContainsKey("hooks"));
        Assert.True(File.Exists(Settings + ".coderim-backup-old"));
    }
    [Fact]
    public void InterruptedMoveRetainsRestorableOriginalForTheOldCommand()
    {
        Write("""{"statusLine":{"type":"command","command":"original"}}""");
        ClaudeHookInstaller.InstallAt(Settings, OldHelper, true);
        Assert.Throws<IOException>(() => ClaudeHookInstaller.InstallAt(Settings, NewHelper, beforeCommit: () => throw new IOException("fixture stop")));
        Assert.Equal(ClaudeHookInstaller.Command("claude-status", OldHelper), Read()["statusLine"]!["command"]!.GetValue<string>());
        ClaudeHookInstaller.UninstallAt(Settings);
        Assert.Equal("original", Read()["statusLine"]!["command"]!.GetValue<string>());
    }
    [Fact]
    public void ConcurrentSettingsUpdateFailsWithoutOverwriteAndCanRetry()
    {
        Write("""{"keep":1} """);
        Assert.Throws<IOException>(() => ClaudeHookInstaller.InstallAt(Settings, OldHelper, beforeCommit: () => Write("""{"keep":2} """)));
        Assert.Equal(2, Read()["keep"]!.GetValue<int>()); Assert.False(Read().ContainsKey("statusLine"));
        ClaudeHookInstaller.InstallAt(Settings, OldHelper);
        Assert.Throws<IOException>(() => ClaudeHookInstaller.UninstallAt(Settings, () => { var root = Read(); root["keep"] = 3; Write(root.ToJsonString()); }));
        Assert.True(File.Exists(State)); Assert.Equal(3, Read()["keep"]!.GetValue<int>());
        ClaudeHookInstaller.UninstallAt(Settings);
        Assert.Equal(3, Read()["keep"]!.GetValue<int>()); Assert.False(Read().ContainsKey("statusLine"));
    }
    [Fact]
    public void BusyCooperatingInstallerDoesNotChangeSettings()
    {
        ClaudeHookInstaller.InstallAt(Settings, OldHelper);
        var before = File.ReadAllText(Settings);
        using var lease = new FileStream(Settings + ".coderim.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => ClaudeHookInstaller.UninstallAt(Settings));
        Assert.Equal(before, File.ReadAllText(Settings)); Assert.True(File.Exists(State));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":99,\"Commands\":[\"user-command\"]}")]
    public void CorruptRecoveryRecordFailsClosed(string corrupt)
    {
        ClaudeHookInstaller.InstallAt(Settings, OldHelper);
        File.WriteAllText(State, corrupt); var before = File.ReadAllText(Settings);
        Assert.Throws<InvalidDataException>(() => ClaudeHookInstaller.UninstallAt(Settings));
        Assert.Equal(before, File.ReadAllText(Settings)); Assert.Equal(corrupt, File.ReadAllText(State));
    }
    [Fact]
    public void ExplicitNullStatusAndMissingSettingsRemainValid()
    {
        Write("""{"statusLine":null,"keep":true}""");
        ClaudeHookInstaller.InstallAt(Settings, OldHelper); ClaudeHookInstaller.UninstallAt(Settings);
        Assert.True(Read().ContainsKey("statusLine")); Assert.Null(Read()["statusLine"]);
        File.Delete(Settings);
        ClaudeHookInstaller.InstallAt(Settings, OldHelper); ClaudeHookInstaller.UninstallAt(Settings);
        Assert.Empty(Read());
    }
    [Fact]
    public void NonCommandStatusRequiresApprovalAndIsRestoredVerbatim()
    {
        const string initial = """{"statusLine":{"type":"custom","text":"preserve"}}""";
        Write(initial);
        Assert.Throws<InvalidOperationException>(() => ClaudeHookInstaller.InstallAt(Settings, OldHelper));
        Assert.Equal(initial, File.ReadAllText(Settings)); Assert.False(File.Exists(State));
        ClaudeHookInstaller.InstallAt(Settings, OldHelper, true); ClaudeHookInstaller.UninstallAt(Settings);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(initial), Read()));
    }
    [Fact]
    public void AddedCommandsCannotMakeSettingsTooLargeToReadBack()
    {
        var initial = new JsonObject { ["keep"] = new string('x', 262000) }.ToJsonString(); Write(initial);
        Assert.Throws<InvalidDataException>(() => ClaudeHookInstaller.InstallAt(Settings, OldHelper));
        Assert.Equal(initial, File.ReadAllText(Settings)); Assert.False(File.Exists(State));
    }
    [Fact]
    public void PrivateRecoveryRecordDoesNotExpandBeyondItsReadLimit()
    {
        // JSON serialization expands these literal Unicode characters. Refuse
        // the operation before installing a command whose recovery is unreadable.
        var initial = "{\"statusLine\":{\"command\":\"" + new string('한', 50000) + "\"}}"; Write(initial);
        Assert.Throws<InvalidDataException>(() => ClaudeHookInstaller.InstallAt(Settings, OldHelper, true));
        Assert.Equal(initial, File.ReadAllText(Settings)); Assert.False(File.Exists(State));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    public void NonObjectSettingsFailClosedWithHandledDataError(string invalid)
    {
        Write(invalid);
        Assert.Throws<InvalidDataException>(() => ClaudeHookInstaller.InstallAt(Settings, OldHelper));
        Assert.Throws<InvalidDataException>(() => ClaudeHookInstaller.UninstallAt(Settings));
        Assert.Equal(invalid, File.ReadAllText(Settings)); Assert.False(File.Exists(State));
    }
}
