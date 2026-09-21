using System.Diagnostics;
using System.Text;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class WindowsIsolatedProcessTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "isolated command " + Guid.NewGuid().ToString("N"));
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private Dictionary<string, string?> EnvironmentForChild() => new()
    {
        ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"), ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        ["TEMP"] = root, ["TMP"] = root, ["SYNTHETIC_PID_FILE"] = Path.Combine(root, "child.pid"),
        ["SYNTHETIC_POWERSHELL"] = PowerShell
    };
    [Fact(Skip = "Requires native Windows Job Object execution", SkipUnless = nameof(IsWindows))]
    public async Task StandardPipesExactArgumentsAndIsolatedUnicodeEnvironmentWork()
    {
        Directory.CreateDirectory(root); var script = Path.Combine(root, "arguments fixture.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$first,[string]$second)
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::WriteLine($first)
            [Console]::WriteLine($second)
            [Console]::WriteLine($env:SYNTHETIC_UNICODE)
            [Console]::Error.Write('fixture-error')
            if ([Console]::In.ReadToEnd().Length -ne 0) { exit 9 }
            """, TestContext.Current.CancellationToken);
        var environment = EnvironmentForChild(); environment["SYNTHETIC_UNICODE"] = "한글 ✓";
        var first = "space and \"quote\""; var second = @"trailing\";
        var result = await BoundedProcess.RunIsolatedResultAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-File", script, first, second],
            environment, TimeSpan.FromSeconds(8), 65536, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode); Assert.Equal("fixture-error", result.Error);
        Assert.Equal([first, second, "한글 ✓"], result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => value.TrimEnd('\r')));
    }
    [Theory(Skip = "Requires native Windows Job Object execution", SkipUnless = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DescendantsCannotOutliveTheCommandEvenWhenParentExitsFirst(bool parentWaits)
    {
        Directory.CreateDirectory(root);
        const string startChild = """
            $s = [Diagnostics.ProcessStartInfo]::new($env:SYNTHETIC_POWERSHELL)
            $s.UseShellExecute = $false
            $s.CreateNoWindow = $true
            $s.Arguments = '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 30"'
            $p = [Diagnostics.Process]::Start($s)
            [IO.File]::WriteAllText($env:SYNTHETIC_PID_FILE, [string]$p.Id)
            """;
        var script = startChild + (parentWaits ? "\nStart-Sleep -Seconds 30" : "\nexit 0");
        var clock = Stopwatch.StartNew();
        var timedOut = false;
        try
        {
            try
            {
                var result = await BoundedProcess.RunIsolatedResultAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", script],
                    EnvironmentForChild(), TimeSpan.FromSeconds(3), 65536, TestContext.Current.CancellationToken);
                Assert.False(parentWaits); Assert.Equal(0, result.ExitCode);
            }
            catch (OperationCanceledException) { TestContext.Current.CancellationToken.ThrowIfCancellationRequested(); timedOut = true; }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
            if (parentWaits) Assert.True(timedOut);
            var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(root, "child.pid"), TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using var child = Process.GetProcessById(pid);
                await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                Assert.True(child.HasExited);
            }
            catch (ArgumentException) { /* already reaped after job termination */ }
        }
        finally
        {
            // Only this synthetic PID is considered for cleanup if an assertion fails.
            var pidPath = Path.Combine(root, "child.pid");
            if (File.Exists(pidPath) && int.TryParse(await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken), out var pid))
                try { using var child = Process.GetProcessById(pid); if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (ArgumentException) { }
        }
    }
    [Fact(Skip = "Requires native Windows handle-inheritance execution", SkipUnless = nameof(IsWindows))]
    public async Task ForeignLaunchCannotHoldTheTimeoutOpen()
    {
        Directory.CreateDirectory(root);
        Process? foreign = null;
        IEnumerable<string> Arguments()
        {
            // Enumerated only after the isolated child-side handles exist. A direct
            // framework launch deliberately bypasses the product creation lock.
            var start = new ProcessStartInfo(PowerShell) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command"); start.ArgumentList.Add("Start-Sleep -Seconds 20");
            foreign = Process.Start(start) ?? throw new IOException("Fixture launch failed.");
            yield return "-NoProfile"; yield return "-NonInteractive";
            yield return "-Command"; yield return "Start-Sleep -Seconds 30";
        }
        try
        {
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedProcess.RunIsolatedResultAsync(
                PowerShell, Arguments(), EnvironmentForChild(), TimeSpan.FromSeconds(3), 65536, TestContext.Current.CancellationToken));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
            Assert.NotNull(foreign); Assert.False(foreign.HasExited);
        }
        finally
        {
            if (foreign is not null)
            {
                if (!foreign.HasExited) foreign.Kill(entireProcessTree: true);
                await foreign.WaitForExitAsync(TestContext.Current.CancellationToken); foreign.Dispose();
            }
        }
    }

    [Fact(Skip = "Requires native Windows Job Object execution", SkipUnless = nameof(IsWindows))]
    public async Task OversizedOutputTerminatesTheContainedCommand()
    {
        Directory.CreateDirectory(root);
        await Assert.ThrowsAsync<IOException>(() => BoundedProcess.RunIsolatedResultAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "while ($true) { [Console]::Out.Write('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx') }"],
            EnvironmentForChild(), TimeSpan.FromSeconds(8), 1024, TestContext.Current.CancellationToken));
    }
}
