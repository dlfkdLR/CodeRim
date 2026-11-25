using System.Diagnostics;
using System.Runtime.InteropServices;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;
[Collection("Native process execution")]
public sealed class AppServerIORegressionTests
{
    [Theory]
    [InlineData("stderr-overflow")]
    [InlineData("repeat-initialize")]
    public async Task EveryRpcPipeObservesFailureAndCancellation(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "coderim-rpc-io-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(AppContext.BaseDirectory, "ProcessFixture",
            OperatingSystem.IsWindows() ? "CodeRim.ProcessFixture.exe" : "CodeRim.ProcessFixture");
        var environment = new Dictionary<string, string?>
        {
            ["SYNTHETIC_RUN_DIR"] = root, ["SYNTHETIC_RPC_MODE"] = mode,
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
            ["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."))
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(mode == "repeat-initialize" ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(8));
        var startedAt = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var response = AppServerClient.ReadAsync(executable, "account/read", environment, false, cancellation.Token);
        Process? child = null;
        try
        {
            var pidFile = Path.Combine(root, "root.pid");
            var processID = 0;
            while (processID <= 0 && clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                try
                {
                    int.TryParse(File.ReadAllText(pidFile), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out processID);
                }
                catch (IOException) { }
                if (processID <= 0) await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            Assert.True(processID > 0, "The owned RPC child did not publish a complete PID marker.");
            child = Process.GetProcessById(processID);
            Assert.True(child.StartTime.ToUniversalTime() >= startedAt.AddSeconds(-1));
            if (mode == "stderr-overflow")
                await Assert.ThrowsAsync<IOException>(() => response.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            else
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"RPC {mode} cleanup took {clock.Elapsed}.");
            Assert.True(child.HasExited, "The owned synthetic RPC child was left running.");
        }
        finally
        {
            cancellation.Cancel();
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
                child.Dispose();
            }
            try { await response.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken); } catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { }
            Directory.Delete(root, true);
        }
    }
}
