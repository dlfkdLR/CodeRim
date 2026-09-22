using System.Diagnostics;
using System.Globalization;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

[CollectionDefinition("Native process execution", DisableParallelization = true)]
public sealed class NativeProcessExecutionDefinition;

[Collection("Native process execution")]
public sealed class WindowsIsolatedProcessTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "isolated command " + Guid.NewGuid().ToString("N"));
    private bool completed;
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "CodeRim.ProcessFixture.exe");
    public void Dispose() { if (completed && Directory.Exists(root)) Directory.Delete(root, true); }
    private Dictionary<string, string?> EnvironmentForChild() => new()
    {
        ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"), ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        ["TEMP"] = root, ["TMP"] = root, ["SYNTHETIC_RUN_DIR"] = root, ["SYNTHETIC_FIXTURE_BIN"] = Executable,
        ["SYNTHETIC_TEST_ID"] = Path.GetFileName(root),
        ["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."))
    };
    [Fact(Skip = "Requires native Windows Job Object execution", SkipUnless = nameof(IsWindows))]
    public async Task StandardPipesExactArgumentsAndIsolatedUnicodeEnvironmentWork()
    {
        Directory.CreateDirectory(root);
        var environment = EnvironmentForChild(); environment["SYNTHETIC_UNICODE"] = "한글 ✓";
        var first = "space and \"quote\""; var second = @"trailing\";
        var result = await BoundedProcess.RunIsolatedResultAsync(Executable, ["echo", first, second],
            environment, TimeSpan.FromSeconds(8), 65536, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode); Assert.Equal("fixture-error", result.Error);
        Assert.Equal([first, second, "한글 ✓"], result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => value.TrimEnd('\r')));
        completed = true;
    }
    [Theory(Skip = "Requires native Windows Job Object execution", SkipUnless = nameof(IsWindows))]
    [InlineData("early")]
    [InlineData("early-detached")]
    [InlineData("waiting")]
    public async Task DescendantsCannotOutliveTheCommandEvenWhenParentExitsFirst(string mode)
    {
        Directory.CreateDirectory(root);
        var clock = Stopwatch.StartNew(); var timedOut = false;
        try
        {
            try
            {
                var result = await BoundedProcess.RunIsolatedResultAsync(Executable, [mode],
                    EnvironmentForChild(), TimeSpan.FromSeconds(3), 65536, TestContext.Current.CancellationToken);
                Assert.Equal("early-detached", mode); Assert.Equal(0, result.ExitCode);
            }
            catch (OperationCanceledException) { TestContext.Current.CancellationToken.ThrowIfCancellationRequested(); timedOut = true; }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
            Assert.Equal(mode != "early-detached", timedOut);
            Assert.True(File.Exists(Path.Combine(root, "parent.started"))); Assert.True(File.Exists(Path.Combine(root, "child.started")));
            if (mode == "early") Assert.Equal("0", File.ReadAllText(Path.Combine(root, "parent.normal-exit-observed")));
            await AssertChildExitedAsync();
            completed = true;
        }
        finally { CleanupChild(); }
    }
    private async Task AssertChildExitedAsync()
    {
        var (pid, born) = ChildIdentity();
        Assert.Equal("pipe-write-ok", File.ReadAllText(Path.Combine(root, "child.output-pipe-verified")));
        Assert.Equal(pid.ToString(CultureInfo.InvariantCulture) + "|" + born.ToString(CultureInfo.InvariantCulture)
            + "|" + Path.GetFileName(root), File.ReadAllText(Path.Combine(root, "child.self")));
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.Equal(born, child.StartTime.ToUniversalTime().Ticks);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(child.HasExited);
        }
        catch (ArgumentException) { /* already reaped after job termination */ }
    }
    private (int Pid, long Born) ChildIdentity()
    {
        var fields = File.ReadAllText(Path.Combine(root, "child.pid")).Split('|');
        return (int.Parse(fields[0], CultureInfo.InvariantCulture), long.Parse(fields[1], CultureInfo.InvariantCulture));
    }
    private void CleanupChild()
    {
        try
        {
            if (!File.Exists(Path.Combine(root, "child.pid"))) return;
            using var reader = File.OpenText(Path.Combine(root, "child.pid"));
            var buffer = new char[129];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count == buffer.Length) return;
            var fields = new string(buffer, 0, count).Split('|');
            if (fields.Length != 2
                || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0
                || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var born)
                || born <= 0 || born > DateTime.MaxValue.Ticks) return;
            using var child = Process.GetProcessById(pid);
            if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == born) child.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Preserve the original assertion failure; the command's Job owns its descendants.
        }
    }
    [Theory]
    [InlineData("")]
    [InlineData("123|")]
    [InlineData("invalid|123")]
    public void FailureCleanupToleratesIncompleteChildIdentity(string identity)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "child.pid"), identity);
        CleanupChild();
        completed = true;
    }
    [Fact(Skip = "Requires native Windows handle-inheritance execution", SkipUnless = nameof(IsWindows))]
    public async Task ForeignLaunchCannotHoldTheTimeoutOpen()
    {
        Directory.CreateDirectory(root); Process? foreign = null;
        IEnumerable<string> Arguments()
        {
            // Invoked after the isolated child-side handles exist. This deliberately
            // bypasses the product creation lock, reproducing a foreign framework launch.
            var start = new ProcessStartInfo(Executable) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var pair in EnvironmentForChild()) start.Environment[pair.Key] = pair.Value;
            start.Environment["SYNTHETIC_RUN_DIR"] = Path.Combine(root, "sibling");
            start.ArgumentList.Add("sibling");
            foreign = Process.Start(start) ?? throw new IOException("Fixture launch failed.");
            yield return "waiting";
        }
        try
        {
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedProcess.RunIsolatedResultAsync(
                Executable, Arguments(), EnvironmentForChild(), TimeSpan.FromSeconds(3), 65536, TestContext.Current.CancellationToken));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
            Assert.NotNull(foreign); Assert.False(foreign.HasExited);
            Assert.True(File.Exists(Path.Combine(root, "sibling", "sibling.started")));
            Assert.True(File.Exists(Path.Combine(root, "parent.started")));
            await AssertChildExitedAsync();
            completed = true;
        }
        finally
        {
            try { CleanupChild(); }
            finally
            {
                if (foreign is not null)
                {
                    try
                    {
                        if (!foreign.HasExited) foreign.Kill(entireProcessTree: true);
                        await foreign.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
                    }
                    finally { foreign.Dispose(); }
                }
            }
        }
    }
    [Fact(Skip = "Requires native Windows Job Object execution", SkipUnless = nameof(IsWindows))]
    public async Task OversizedOutputTerminatesTheContainedCommand()
    {
        Directory.CreateDirectory(root);
        await Assert.ThrowsAsync<IOException>(() => BoundedProcess.RunIsolatedResultAsync(Executable, ["flood"],
            EnvironmentForChild(), TimeSpan.FromSeconds(8), 1024, TestContext.Current.CancellationToken));
        Assert.Equal("started", File.ReadAllText(Path.Combine(root, "flood.started")));
        completed = true;
    }
}
