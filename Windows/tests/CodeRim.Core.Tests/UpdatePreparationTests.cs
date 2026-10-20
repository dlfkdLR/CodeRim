using System.Security.Cryptography;
using System.Text;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class UpdatePreparationTests
{
    private static readonly string[] InitialNames = ["next-worker.exe", "package.zip"];
    [Fact]
    public void RepeatedRejectedPreparationDoesNotConsumeTheEightActiveCapsuleSlots()
    {
        using var fixture = new InstallFixture();
        for (var i = 0; i < 12; i++)
        {
            var capsule = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N")); InstallFileSystem.CreatePrivateDirectory(capsule);
            using (var preparation = new UpdatePreparation(capsule))
            {
                preparation.Write("previous-worker.exe", output => output.Write("owned synthetic bytes"u8));
                Assert.Throws<IOException>(() => preparation.Write("package.zip", output => { output.Write("partial transfer"u8); throw new IOException("fixture read failure"); }));
            }
            Assert.False(Directory.Exists(capsule));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }
    [Theory]
    [InlineData("unknown")][InlineData("changed")][InlineData("replaced")][InlineData("retained")]
    public void PrecommitCleanupPreservesRecoveryAndFilesItCannotStillIdentify(string scenario)
    {
        using var fixture = new InstallFixture(); var capsule = fixture.NewStage();
        using (var preparation = new UpdatePreparation(capsule))
        {
            preparation.Write("package.zip", output => output.Write("owned"u8));
            var path = Path.Combine(capsule, "package.zip");
            if (scenario == "unknown") File.WriteAllText(Path.Combine(capsule, "personal.txt"), "not updater data");
            if (scenario == "changed") File.WriteAllText(path, "different content");
            if (scenario == "replaced") { File.Delete(path); File.WriteAllText(path, "replacement content"); }
            if (scenario == "retained") preparation.KeepForRecovery();
        }
        Assert.True(Directory.Exists(capsule)); Assert.True(File.Exists(Path.Combine(capsule, "package.zip")));
        if (scenario == "unknown") Assert.Equal("not updater data", File.ReadAllText(Path.Combine(capsule, "personal.txt")));
    }
    [Fact]
    public void CreateNewCollisionCannotMakeAnotherFileOwnedByThisInvocation()
    {
        using var fixture = new InstallFixture(); var capsule = fixture.NewStage(); var path = Path.Combine(capsule, "package.zip");
        File.WriteAllText(path, "existing unrelated bytes");
        using (var preparation = new UpdatePreparation(capsule))
            Assert.Throws<IOException>(() => preparation.Write("package.zip", output => output.Write("new"u8)));
        Assert.Equal("existing unrelated bytes", File.ReadAllText(path));
    }
    [Fact]
    public void CopyHashMismatchRemovesOnlyThePartialFileItCreated()
    {
        using var fixture = new InstallFixture(); var capsule = fixture.NewStage(); var source = Path.Combine(fixture.Root, "download.zip");
        File.WriteAllText(source, "synthetic input");
        using (var preparation = new UpdatePreparation(capsule))
            Assert.Throws<InvalidDataException>(() => preparation.Copy(source, "package.zip", new FileInfo(source).Length, new string('0', 64)));
        Assert.False(Directory.Exists(capsule)); Assert.Equal("synthetic input", File.ReadAllText(source));
    }
    [Theory]
    [InlineData("complete")][InlineData("partial")][InlineData("changed")][InlineData("unknown")]
    public void InitialInstallTemporaryFilesUseTheirOwnExactOwnershipSet(string scenario)
    {
        using var fixture = new InstallFixture(); var capsule = fixture.NewStage();
        var files = InitialNames.Select(name =>
        {
            var bytes = Encoding.UTF8.GetBytes("initial synthetic " + name); File.WriteAllBytes(Path.Combine(capsule, name), bytes);
            return new InstallPayloadFile(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }).ToArray();
        File.WriteAllBytes(Path.Combine(capsule, InstallPayloadManifest.ReceiptName), UpdateCapsuleCleanup.InitialReceiptBytes(InstallFixture.Manifest(), files));
        if (scenario == "partial") File.Delete(Path.Combine(capsule, files[0].Path));
        if (scenario == "changed") File.WriteAllText(Path.Combine(capsule, files[0].Path), "preserve replacement");
        if (scenario == "unknown") File.WriteAllText(Path.Combine(capsule, "personal.txt"), "preserve unknown");
        var before = InstallFixture.HashTree(capsule);
        Assert.Equal(scenario is "complete" or "partial", UpdateCapsuleCleanup.TryRemoveCompleted(capsule));
        if (scenario is "changed" or "unknown") Assert.Equal(before, InstallFixture.HashTree(capsule));
        else Assert.False(Directory.Exists(capsule));
    }
    [Fact]
    public void InitialOwnershipCannotAdoptTheUpdateOrArbitraryFileSet()
    {
        var files = new[] { new InstallPayloadFile("personal.txt", 0, Convert.ToHexStringLower(SHA256.HashData([]))) };
        Assert.Throws<InvalidDataException>(() => UpdateCapsuleCleanup.InitialReceiptBytes(InstallFixture.Manifest(), files));
    }
}
