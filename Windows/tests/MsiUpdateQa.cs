// Compiled only into the disposable old-version QA fixture, never release binaries.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace CodeRim.Core.Services;
[SupportedOSPlatform("windows")]
public static class MsiUpdateQa
{
    public static async Task<string> StartAsync(string directory, string workerPin)
    {
        if (Environment.GetEnvironmentVariable("CI") != "true") throw new InvalidOperationException("Disposable CI only.");
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var manifestPath = Directory.GetFiles(directory, "*-" + architecture + "-Setup.msi.manifest.json").Single();
        var manifest = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        var signature = (await File.ReadAllTextAsync(manifestPath.Replace(".manifest.json", ".manifest.sig", StringComparison.Ordinal)).ConfigureAwait(false)).Trim();
        var package = InstallerUpdates.ParseAuthorization(manifest, signature, architecture, Version.Parse(ReleaseUpdates.CurrentVersion));
        var downloadDirectory = WindowsUpdateLocation.Open().PrivateDirectory("Downloads");
        var temporary = Path.Combine(downloadDirectory, Guid.NewGuid().ToString("N") + ".msi");
        File.Copy(Path.Combine(directory, package.FileName), temporary);
        _ = InstallerUpdates.Cache(new(package, temporary));
        var operation = await MsiUpdateExecution.StartAsync(new(package, manifest, signature), workerPin, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "handoff-operation.txt"), operation).ConfigureAwait(false);
        return operation;
    }
}
