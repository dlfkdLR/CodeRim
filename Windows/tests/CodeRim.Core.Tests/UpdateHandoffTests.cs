using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class UpdateHandoffTests
{
    [Fact]
    public void MissingPublisherConfigurationCannotStartAnAutomaticUpdate() => Assert.False(UpdateBootstrap.IsSigningConfigured(typeof(UpdateHandoffTests).Assembly));
    [Theory]
    [InlineData("{}")][InlineData("{\"Id\":0,\"StartedUtcTicks\":1}")][InlineData("{\"Id\":1,\"StartedUtcTicks\":0}")]
    [InlineData("{\"Id\":1,\"StartedUtcTicks\":1,\"Path\":\"other.exe\"}")][InlineData("{\"Id\":1,\"Id\":2,\"StartedUtcTicks\":1}")]
    [InlineData("{\"Id\":1,\"StartedUtcTicks\":3155378976000000000}")]
    public void InvalidParentIdentityCannotRequestAWait(string json) => Assert.Throws<InvalidDataException>(() => UpdateParent.Parse(json));
    [Fact]
    public async Task CurrentProcessDoesNotLookExitedAndItsWaitIsBounded()
    {
        var parent = UpdateParent.Parse(JsonSerializer.Serialize(UpdateParent.Current())); var timer = Stopwatch.StartNew();
        Assert.False(await parent.WaitAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
    }
    [Fact]
    public async Task ReusedPidDoesNotWaitForAnotherProcess()
    {
        var parent = UpdateParent.Current() with { StartedUtcTicks = 1 };
        Assert.True(await parent.WaitAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task CancelledParentWaitDoesNotAuthorizeCommit()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateParent.Current().WaitAsync(TimeSpan.FromMinutes(1), cancellation.Token));
    }
    [Theory]
    [InlineData("missing")][InlineData("extra")][InlineData("modified")][InlineData("receipt")]
    public void ExtractedFirstInstallMustBeCompleteAndContainOnlySignedFiles(string scenario)
    {
        using var fixture = new InstallFixture(); var manifest = fixture.InstallOld();
        File.Delete(Path.Combine(fixture.InstallRoot, InstallPayloadManifest.ReceiptName));
        switch (scenario)
        {
            case "missing": File.Delete(Path.Combine(fixture.InstallRoot, "CodeRim.exe")); break;
            case "extra": File.WriteAllText(Path.Combine(fixture.InstallRoot, "unknown.txt"), "user data"); break;
            case "modified": File.WriteAllText(Path.Combine(fixture.InstallRoot, "CodeRim.exe"), "modified"); break;
            case "receipt": File.WriteAllText(Path.Combine(fixture.InstallRoot, InstallPayloadManifest.ReceiptName), manifest.ToJson()); break;
        }
        var before = InstallFixture.HashTree(fixture.InstallRoot);
        Assert.Throws<InvalidDataException>(() => manifest.VerifyUninstalledTree(fixture.InstallRoot, TestContext.Current.CancellationToken));
        Assert.Equal(before, InstallFixture.HashTree(fixture.InstallRoot));
    }
    [Fact]
    public void CompleteExtractedPayloadIsVerifiedWithoutInventingAnInstalledReceipt()
    {
        using var fixture = new InstallFixture(); var manifest = fixture.InstallOld(); var receipt = Path.Combine(fixture.InstallRoot, InstallPayloadManifest.ReceiptName);
        File.Delete(receipt); manifest.VerifyUninstalledTree(fixture.InstallRoot, TestContext.Current.CancellationToken); Assert.False(File.Exists(receipt));
    }
    [Fact]
    public void AuthenticatedInitialApplyCreatesReceiptButCannotAdoptAnUnknownDirectory()
    {
        using var fixture = new InstallFixture(); var manifest = InstallFixture.Manifest();
        InstallTransaction.ApplyAuthenticated(fixture.InstallRoot, fixture.Archive(), manifest, null, TestContext.Current.CancellationToken);
        manifest.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => InstallTransaction.ApplyAuthenticated(fixture.InstallRoot, fixture.Archive(), manifest, null, TestContext.Current.CancellationToken));
        manifest.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken);
    }
    [Fact]
    public void InitialRecoveryCannotReplaceAnUpdateJournalOldIdentityWithNull()
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); var next = InstallFixture.Manifest("2.2.1");
        _ = InstallTransaction.Recover(fixture.InstallRoot, TestContext.Current.CancellationToken);
        var path = Path.Combine(InstallTransaction.WorkPath(fixture.InstallRoot), "journal.json");
        var json = JsonSerializer.Serialize(new { Id = Guid.NewGuid().ToString("N"), State = "Prepared", NewManifest = next.ToJson(), OldManifest = previous.ToJson() }); File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => InstallTransaction.RecoverAuthenticated(fixture.InstallRoot, next, null, TestContext.Current.CancellationToken));
        Assert.Equal(json, File.ReadAllText(path)); previous.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken);
    }
    [Fact]
    public void HandoffResponseMustBeBoundedAndMatchItsExactOperation()
    {
        using var fixture = new InstallFixture(); var path = Path.Combine(fixture.Root, "result.json"); var id = Guid.NewGuid().ToString("N");
        UpdateBootstrap.WriteResult(path, new(UpdateExecutionStatus.Prepared, id));
        Assert.Equal(UpdateExecutionStatus.Prepared, UpdateBootstrap.ReadResult(path, id).Status);
        Assert.Throws<InvalidDataException>(() => UpdateBootstrap.ReadResult(path, Guid.NewGuid().ToString("N")));
        Assert.Throws<IOException>(() => UpdateBootstrap.WriteResult(path, new(UpdateExecutionStatus.Applied, id)));
        File.WriteAllBytes(path, [0xff]); Assert.Throws<DecoderFallbackException>(() => UpdateBootstrap.ReadResult(path, id));
        File.WriteAllText(path, new string('a', UpdateWorkerSupervisor.MaximumOutput + 1)); Assert.Throws<InvalidDataException>(() => UpdateBootstrap.ReadResult(path, id));
    }
}

public sealed class UpdateRecoveryRequestTests
{
    [Fact]
    public void RecoveryUsesDistinctExactOperationAndLauncherIdentities()
    {
        var operation = Guid.NewGuid().ToString("N"); var launcher = Guid.NewGuid().ToString("N");
        var value = new UpdateRecoveryRequest(operation, launcher, UpdateParent.Current());
        Assert.Equal(value, UpdateRecoveryRequest.Parse(JsonSerializer.Serialize(value)));
        Assert.Throws<ArgumentException>(() => UpdateRecoveryRequest.Parse(JsonSerializer.Serialize(value with { LauncherId = "../other" })));
        Assert.Throws<ArgumentException>(() => UpdateRecoveryRequest.Parse(JsonSerializer.Serialize(value with { OperationId = "../other" })));
    }
}

public sealed class UpdateCapsuleCleanupTests
{
    [Theory]
    [InlineData("complete")][InlineData("partial")][InlineData("changed")][InlineData("unknown")]
    public void CompletedTemporaryFilesAreRemovedOnlyWhenTheirExactOwnershipMatches(string scenario)
    {
        using var fixture = new InstallFixture(); var capsule = fixture.NewStage();
        var files = UpdateCapsuleCleanup.OwnedNames.Select(name =>
        {
            var bytes = Encoding.UTF8.GetBytes("temporary synthetic " + name); File.WriteAllBytes(Path.Combine(capsule, name), bytes);
            return new InstallPayloadFile(name, bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        }).ToArray();
        UpdateCapsuleCleanup.WriteReceipt(capsule, UpdateWorkerContractTests.ValidRequest(), files);
        if (scenario == "partial") File.Delete(Path.Combine(capsule, files[0].Path));
        if (scenario == "changed") File.WriteAllText(Path.Combine(capsule, files[0].Path), "replacement must be preserved");
        if (scenario == "unknown") File.WriteAllText(Path.Combine(capsule, "personal.txt"), "must be preserved");
        var before = InstallFixture.HashTree(capsule);
        Assert.Equal(scenario is "complete" or "partial", UpdateCapsuleCleanup.TryRemoveCompleted(capsule));
        if (scenario is "changed" or "unknown") Assert.Equal(before, InstallFixture.HashTree(capsule));
        else Assert.False(Directory.Exists(capsule));
    }
}

public sealed class UpdateRestartHandshakeTests
{
    [Fact]
    public async Task CancellationBeforeConfirmationCannotLeaveAnInstallSignal()
    {
        using var fixture = new InstallFixture(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateRestartHandshake.ConfirmAsync(fixture.Root, Guid.NewGuid().ToString("N"), cancelled.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }
    [Fact]
    public async Task CancellingWhileAwaitingAcknowledgementWithdrawsTheConfirmation()
    {
        using var fixture = new InstallFixture(); var id = Guid.NewGuid().ToString("N"); using var cancelled = new CancellationTokenSource();
        var confirming = UpdateRestartHandshake.ConfirmAsync(fixture.Root, id, cancelled.Token);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "restart.confirmed"))); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirming);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "restart.confirmed")));
        Assert.False(await UpdateRestartHandshake.WaitAsync(fixture.Root, id, UpdateParent.Current() with { StartedUtcTicks = 1 }, TimeSpan.FromMilliseconds(60), TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task SupervisorObservesWithdrawnConsentWhileOriginalParentStillRuns()
    {
        using var fixture = new InstallFixture(); var id = Guid.NewGuid().ToString("N"); var path = Path.Combine(fixture.Root, "restart.confirmed");
        File.WriteAllText(path, id); var waiting = UpdateRestartHandshake.WaitAsync(fixture.Root, id, UpdateParent.Current(), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "restart.accepted"))); File.Delete(path);
        Assert.False(await waiting);
    }
    [Fact]
    public async Task ConfirmedOriginalExitAllowsCommitButWrongOperationDoesNot()
    {
        using var fixture = new InstallFixture(); var id = Guid.NewGuid().ToString("N"); var exited = UpdateParent.Current() with { StartedUtcTicks = 1 };
        File.WriteAllText(Path.Combine(fixture.Root, "restart.confirmed"), id);
        Assert.False(await UpdateRestartHandshake.WaitAsync(fixture.Root, Guid.NewGuid().ToString("N"), exited, TimeSpan.FromMilliseconds(60), TestContext.Current.CancellationToken));
        Assert.True(await UpdateRestartHandshake.WaitAsync(fixture.Root, id, exited, TimeSpan.FromMilliseconds(60), TestContext.Current.CancellationToken));
    }
}
