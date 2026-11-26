using System.Diagnostics;
using System.Globalization;
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
        cancellation.CancelAfter(TimeSpan.FromSeconds(8));
        var clock = Stopwatch.StartNew();
        var response = AppServerClient.ReadAsync(executable, "account/read", environment, false, cancellation.Token);
        string receipt = Path.Combine(root, "rpc-owner");
        Process? child = null;
        bool observed = false;
        try
        {
            // Exercise cancellation of a running RPC writer, including a cold fixture launch.
            // The startup receipt is atomic; no PID lookup is needed before the protocol ends.
            if (mode == "repeat-initialize")
            {
                while (!File.Exists(receipt) && !response.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(2))
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                Assert.True(File.Exists(receipt), "The synthetic RPC writer did not start.");
                clock.Restart();
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
            }
            if (mode == "stderr-overflow")
                await Assert.ThrowsAsync<IOException>(() => response.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            else
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"RPC {mode} cleanup took {clock.Elapsed}.");
            // The expected protocol failure is complete. Check before the test issues any
            // cancellation or cleanup, even if the child has already been reaped.
            child = ReadOwnedProcess(receipt);
            observed = true;
            Assert.True(child is null || child.HasExited, "The owned synthetic RPC child was left running.");
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                try { await response.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None); }
                catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { }
                if (!observed && File.Exists(receipt)) child = ReadOwnedProcess(receipt);
                if (child is not null)
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
                }
            }
            finally { child?.Dispose(); Directory.Delete(root, true); }
        }
    }

    private static Process? ReadOwnedProcess(string receipt)
    {
        var owner = File.ReadAllText(receipt).Split('|');
        Assert.Equal(2, owner.Length);
        var pid = int.Parse(owner[0], CultureInfo.InvariantCulture);
        var born = long.Parse(owner[1], CultureInfo.InvariantCulture);
        Assert.True(pid > 0 && born > 0);
        return FindOwnedProcess(pid, born);
    }

    private static Process? FindOwnedProcess(int pid, long born)
    {
        Process? observed;
        try { observed = Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
        try
        {
            if (observed.HasExited || observed.StartTime.ToUniversalTime().Ticks != born) return null;
            var owned = observed;
            observed = null;
            return owned;
        }
        catch (InvalidOperationException) when (observed is { HasExited: true }) { return null; }
        finally { observed?.Dispose(); }
    }

    [Fact]
    public void ExitObservationMatchesTheOwnedStartTime()
    {
        using var self = Process.GetCurrentProcess();
        var born = self.StartTime.ToUniversalTime().Ticks;
        using var same = FindOwnedProcess(self.Id, born);
        Assert.NotNull(same);
        Assert.False(same.HasExited);
        Assert.Null(FindOwnedProcess(self.Id, born + 1));
    }
}
