using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class UpdateAuthenticatedRecoveryTests
{
    [Theory]
    [InlineData("old")][InlineData("new")]
    public void AuthenticatedRecoveryRejectsJournalWithDifferentSignedPairWithoutMutatingIt(string mismatch)
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); var next = InstallFixture.Manifest("2.2.1");
        _ = InstallTransaction.Recover(fixture.InstallRoot, TestContext.Current.CancellationToken); // Create the private engine work area only.
        var work = InstallTransaction.WorkPath(fixture.InstallRoot); var path = Path.Combine(work, "journal.json");
        var journal = JsonSerializer.Serialize(new { Id = Guid.NewGuid().ToString("N"), State = "Extracting", NewManifest = next.ToJson(), OldManifest = previous.ToJson() }); File.WriteAllText(path, journal);
        var wrong = InstallFixture.Manifest(mismatch == "old" ? "2.1.9" : "2.2.2");
        Assert.Throws<InvalidDataException>(() => InstallTransaction.RecoverAuthenticated(fixture.InstallRoot, mismatch == "new" ? wrong : next, mismatch == "old" ? wrong : previous, TestContext.Current.CancellationToken));
        Assert.Equal(journal, File.ReadAllText(path)); previous.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken);
    }
    [Fact]
    public void AuthenticatedApplyDoesNotRecoverAnUnrelatedPendingOperation()
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); var next = InstallFixture.Manifest("2.2.1");
        _ = InstallTransaction.Recover(fixture.InstallRoot, TestContext.Current.CancellationToken);
        var path = Path.Combine(InstallTransaction.WorkPath(fixture.InstallRoot), "journal.json");
        var other = InstallFixture.Manifest("9.9.9"); var text = JsonSerializer.Serialize(new { Id = Guid.NewGuid().ToString("N"), State = "Extracting", NewManifest = other.ToJson(), OldManifest = previous.ToJson() }); File.WriteAllText(path, text);
        Assert.Throws<InvalidDataException>(() => InstallTransaction.ApplyAuthenticated(fixture.InstallRoot, fixture.Archive(), next, previous, TestContext.Current.CancellationToken));
        Assert.Equal(text, File.ReadAllText(path)); previous.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken);
    }
    [Fact]
    public void MatchingAuthenticatedPairRecoversThePreviousTree()
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); var next = InstallFixture.Manifest("2.2.1");
        _ = InstallTransaction.Recover(fixture.InstallRoot, TestContext.Current.CancellationToken);
        var path = Path.Combine(InstallTransaction.WorkPath(fixture.InstallRoot), "journal.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Id = Guid.NewGuid().ToString("N"), State = "Extracting", NewManifest = next.ToJson(), OldManifest = previous.ToJson() }));
        Assert.Equal(InstallRecoveryResult.RolledBack, InstallTransaction.RecoverAuthenticated(fixture.InstallRoot, next, previous, TestContext.Current.CancellationToken));
        previous.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken); Assert.False(File.Exists(path));
    }
}
