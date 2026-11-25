using System.Diagnostics;
using System.Runtime.InteropServices;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

[Collection("Native process execution")]
public sealed class BoundedProcessRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedOutputStopsTheCommandBeforeItsDeadline(bool sendUnreadInput)
    {
        var root = Path.Combine(Path.GetTempPath(), "coderim-process-bound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "ProcessFixture",
                OperatingSystem.IsWindows() ? "CodeRim.ProcessFixture.exe" : "CodeRim.ProcessFixture");
            var environment = new Dictionary<string, string?>
            {
                ["SYNTHETIC_RUN_DIR"] = root,
                ["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."))
            };
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<IOException>(() => BoundedProcess.RunResultAsync(executable, ["flood"],
                input: sendUnreadInput ? new string('x', 1024 * 1024) : null,
                timeout: TimeSpan.FromSeconds(8), maximumBytes: 1024,
                environment: environment, cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Output rejection waited {clock.Elapsed} for process exit.");
            Assert.Equal("started", File.ReadAllText(Path.Combine(root, "flood.started")));
        }
        finally { Directory.Delete(root, true); }
    }
}
