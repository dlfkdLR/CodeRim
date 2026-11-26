using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class CompanionStagingFileTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows rename sharing semantics", SkipUnless = nameof(IsWindows))]
    public void StagingLeasePreventsALateIncompatibleReaderAndAllowsImmediatePublishedReads()
    {
        var directory = Directory.CreateTempSubdirectory("coderim-staging-");
        try
        {
            var temporary = Path.Combine(directory.FullName, "complete.tmp");
            var path = Path.Combine(directory.FullName, "snapshot.json");
            var snapshot = new CompanionSnapshot(1, DateTimeOffset.Now, []);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, CompanionFile.JsonOptions);
            using (var lease = CompanionFile.Stage(temporary, bytes))
            {
                Assert.NotNull(lease);
                Assert.False(lease.IsInvalid);
                var blocked = Assert.Throws<IOException>(() =>
                {
                    using var incompatible = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);
                });
                Assert.Equal(32, blocked.HResult & 0xffff);
                using var compatible = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                CompanionFile.Publish(temporary, path);
                Assert.Equal(snapshot.GeneratedAt, CompanionFile.Read(path).GeneratedAt);
                Assert.False(File.Exists(temporary));
            }
            using var ordinary = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(bytes.Length, ordinary.Length);
        }
        finally { directory.Delete(true); }
    }
}
