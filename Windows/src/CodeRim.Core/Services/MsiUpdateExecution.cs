using System.Diagnostics;
using System.Collections;
using System.IO.Pipes;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace CodeRim.Core.Services;

public enum MsiUpdateStatus { Applied, RebootRequired, Cancelled, Busy, FailedRestored, RecoveryRequired, ParentStillRunning }
public sealed record MsiUpdateResult(MsiUpdateStatus Status, int? ExitCode);

[SupportedOSPlatform("windows")]
public static class MsiUpdateExecution
{
    public const string EntryArgument = "--msi-update";
    private sealed record Request(string Manifest, string Signature, string Parent);

    public static async Task<string> StartAsync(InstallerAuthorization authorization, string workerDigest, CancellationToken token)
    {
        var location = WindowsUpdateLocation.Open();
        var self = Environment.ProcessPath ?? throw new IOException("Missing application image.");
        if (!string.Equals(Path.GetFullPath(self), Path.Combine(location.InstallRoot, "CodeRim.exe"), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(location.InstallRoot, "CodeRim.install.json"))) throw new InvalidDataException("Use the MSI installation to update.");
        if (workerDigest.Length != 64 || !workerDigest.All(char.IsAsciiHexDigit)) throw new InvalidDataException("This application does not have a pinned installer worker.");
        var id = Guid.NewGuid().ToString("N"); var launcher = location.Launcher(id, create: true);
        var source = Path.Combine(location.InstallRoot, "CodeRim.UpdateWorker.exe");
        var target = Path.Combine(launcher, "CodeRim.UpdateWorker.exe");
        InstallFileSystem.CheckPath(source);
        using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), Convert.FromHexString(workerDigest)))
            throw new InvalidDataException("The installed update worker has changed.");
        original.Position = 0;
        using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) original.CopyTo(copy);
        using var copied = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(copied), Convert.FromHexString(workerDigest)))
            throw new InvalidDataException("The update worker copy has changed.");
        var request = new Request(Convert.ToBase64String(authorization.Manifest), authorization.Signature, JsonSerializer.Serialize(UpdateParent.Current()));
        InstallFileSystem.WriteNew(Path.Combine(launcher, "request.json"), JsonSerializer.SerializeToUtf8Bytes(request));
        var start = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = launcher };
        start.Environment.Clear(); foreach (var pair in location.ChildEnvironment()) start.Environment[pair.Key] = pair.Value;
        AnonymousPipeServerStream pipe; Process child;
        lock (BoundedProcess.CreationLock)
        {
            pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            try
            {
                start.ArgumentList.Add(EntryArgument); start.ArgumentList.Add(id); start.ArgumentList.Add(pipe.GetClientHandleAsString());
                child = Process.Start(start) ?? throw new IOException("Could not start the verified update worker.");
                pipe.DisposeLocalCopyOfClientHandle();
            }
            catch { pipe.Dispose(); throw; }
        }
        using var environmentPipe = pipe;
        using var process = child;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            // Environment secrets are transferred only through an inherited anonymous pipe, never disk/argv/logs.
            var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(x => (string)x.Key, x => (string)x.Value!);
            var environmentBytes = JsonSerializer.SerializeToUtf8Bytes(environment);
            if (environmentBytes.Length > 1048576) throw new InvalidDataException("The restart environment is too large.");
            await environmentPipe.WriteAsync(BitConverter.GetBytes(environmentBytes.Length), deadline.Token).ConfigureAwait(false);
            await environmentPipe.WriteAsync(environmentBytes, deadline.Token).ConfigureAwait(false);
            await environmentPipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            while (!File.Exists(Path.Combine(launcher, "ready")))
            {
                if (process.HasExited) throw new IOException("The update worker rejected the installer.");
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return id;
        }
        catch { InstallFileSystem.WriteNew(Path.Combine(launcher, "cancelled"), [1]); throw; }
    }

    public static void Confirm(string id) => InstallFileSystem.WriteNew(Path.Combine(WindowsUpdateLocation.Open().Launcher(id), "confirmed"), [1]);
    public static void Cancel(string id)
    {
        var file = Path.Combine(WindowsUpdateLocation.Open().Launcher(id), "cancelled");
        if (!File.Exists(file)) InstallFileSystem.WriteNew(file, [1]);
    }

    public static async Task<MsiUpdateResult> ExecuteAsync(string id, string environmentHandle)
    {
        try { return await ExecuteCoreAsync(id, environmentHandle).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var result = new MsiUpdateResult(MsiUpdateStatus.RecoveryRequired, null);
            try { Record(WindowsUpdateLocation.Open(), id, result); } catch (Exception record) when (record is not OutOfMemoryException) { }
            ShowResult(result); return result;
        }
    }

    private static async Task<MsiUpdateResult> ExecuteCoreAsync(string id, string environmentHandle)
    {
        var location = WindowsUpdateLocation.Open(); var launcher = location.Launcher(id);
        using var environmentPipe = new AnonymousPipeClientStream(PipeDirection.In, environmentHandle);
        using var environmentDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var lengthBytes = new byte[4]; await environmentPipe.ReadExactlyAsync(lengthBytes, environmentDeadline.Token).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is < 2 or > 1048576) throw new InvalidDataException("Invalid restart environment.");
        var environmentBytes = new byte[length]; await environmentPipe.ReadExactlyAsync(environmentBytes, environmentDeadline.Token).ConfigureAwait(false);
        var restartEnvironment = JsonSerializer.Deserialize<Dictionary<string, string>>(environmentBytes) ?? throw new InvalidDataException("Missing restart environment.");
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.Combine(launcher, "CodeRim.UpdateWorker.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update worker must run from its private launcher.");
        var bytes = InstallFileSystem.ReadBounded(Path.Combine(launcher, "request.json"), 16384);
        using var json = JsonDocument.Parse(bytes); InstallPayloadManifest.ExactObject(json.RootElement, "Manifest", "Signature", "Parent");
        var request = JsonSerializer.Deserialize<Request>(bytes) ?? throw new InvalidDataException("Missing installer request.");
        var parent = UpdateParent.Parse(request.Parent);
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var package = InstallerUpdates.ParseAuthorization(Convert.FromBase64String(request.Manifest), request.Signature, arch, Version.Parse(ReleaseUpdates.CurrentVersion));
        var downloaded = InstallerUpdates.FindCached(package, location.PrivateDirectory("Downloads")) ?? throw new InvalidDataException("The verified MSI is missing.");
        using var lease = InstallerUpdates.OpenVerifiedInstaller(downloaded);
        AllowInstallerServiceRead(downloaded.Path);
        var installedExe = Path.Combine(location.InstallRoot, "CodeRim.exe");
        var previous = Snapshot(location.InstallRoot);
        if (previous.Version != ReleaseUpdates.CurrentVersion) throw new InvalidDataException("The MSI registration does not match the running app.");
        InstallFileSystem.WriteNew(Path.Combine(launcher, "ready"), [1]);
        var exited = parent.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        while (!exited.IsCompleted)
        {
            if (File.Exists(Path.Combine(launcher, "cancelled"))) return Record(location, id, new(MsiUpdateStatus.Cancelled, null));
            await Task.WhenAny(exited, Task.Delay(100)).ConfigureAwait(false);
        }
        if (!await exited.ConfigureAwait(false) || !File.Exists(Path.Combine(launcher, "confirmed")) || File.Exists(Path.Combine(launcher, "cancelled")))
            return Record(location, id, new(MsiUpdateStatus.ParentStillRunning, null));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"))
            { UseShellExecute = false, WorkingDirectory = launcher };
        foreach (var argument in new[] { "/i", downloaded.Path, "/qn", "/norestart", "REBOOT=ReallySuppress", "LAUNCHAPP=0", "MSIRESTARTMANAGERCONTROL=Disable", "/l*v", Path.Combine(launcher, "install.log") }) start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = launcher; start.Environment["TMP"] = launcher;
        using var process = Process.Start(start) ?? throw new IOException("Windows Installer could not be started.");
        // Never kill msiexec or report rollback while its service-side transaction may still be running.
        await process.WaitForExitAsync().ConfigureAwait(false);
        var after = Snapshot(location.InstallRoot);
        var unchanged = previous.Version == after.Version && previous.ProductCode == after.ProductCode && IsRegistered(after.ProductCode)
            && previous.Hashes.Count == after.Hashes.Count && previous.Hashes.All(pair => after.Hashes.TryGetValue(pair.Key, out var value) && pair.Value == value);
        var installedVersion = File.Exists(installedExe) ? FileVersionInfo.GetVersionInfo(installedExe).FileVersion : null;
        var status = Classify(process.ExitCode, unchanged, installedVersion == package.Version.ToString(3) + ".0" && after.Version == package.Version.ToString(3) && IsRegistered(after.ProductCode));
        var result = Record(location, id, new(status, process.ExitCode));
        if (status != MsiUpdateStatus.Applied) ShowResult(result);
        if (status == MsiUpdateStatus.Applied || unchanged && status is MsiUpdateStatus.FailedRestored or MsiUpdateStatus.Cancelled or MsiUpdateStatus.Busy)
        {
            var restart = new ProcessStartInfo(installedExe) { UseShellExecute = false, WorkingDirectory = location.InstallRoot };
            restart.Environment.Clear(); foreach (var pair in restartEnvironment) restart.Environment[pair.Key] = pair.Value;
            using var restarted = Process.Start(restart);
        }
        return result;
    }

    internal static void AllowInstallerServiceRead(string path)
    {
        // Windows Installer's service reads the authenticated MSI as SYSTEM. Expose only
        // these public installer bytes; request/pipe/runtime directories stay user-private.
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("The current user is unavailable.");
        var security = new FileSecurity(); security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void ShowResult(MsiUpdateResult result)
    {
        var message = result.Status switch
        {
            MsiUpdateStatus.RebootRequired => "CodeRim was installed, but Windows must restart to finish the update. Your settings and accounts are preserved.",
            MsiUpdateStatus.Busy => "Another Windows installation is running. CodeRim was not updated. Try again after it finishes.",
            MsiUpdateStatus.Cancelled => "The update was cancelled. The previous CodeRim installation was verified and will reopen.",
            MsiUpdateStatus.FailedRestored => "The update could not finish. Windows Installer restored the previous CodeRim installation, which will reopen.",
            _ => "The CodeRim update could not be verified as complete. Use the official CodeRim MSI installer to repair the app. Do not remove your account or settings data."
        };
        _ = MessageBoxW(0, message, "CodeRim update", 0x30);
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MessageBoxW(nint window, string text, string caption, uint type);

    private sealed record InstalledState(string? Version, string? ProductCode, Dictionary<string, string> Hashes);
    private static readonly string[] IdentityFiles = ["CodeRim.exe", "CodeRimCLI.exe", "CodeRim.UpdateWorker.exe", "CodeRim.install.json"];
    private static InstalledState Snapshot(string root)
    {
        using var registry = Registry.CurrentUser.OpenSubKey(@"Software\CodeRim\Installer");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in IdentityFiles)
        {
            var path = Path.Combine(root, name); InstallFileSystem.CheckPath(path);
            if (!File.Exists(path)) continue;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            hashes.Add(name, Convert.ToHexString(SHA256.HashData(file)));
        }
        return new(registry?.GetValue("Version") as string, registry?.GetValue("ProductCode") as string, hashes);
    }
    private static bool IsRegistered(string? product) => product is not null && Guid.TryParse(product, out _) && MsiQueryProductStateW(product) == 5;
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MsiQueryProductStateW(string productCode);

    internal static MsiUpdateStatus Classify(int code, bool oldVerified, bool newVerified) => code switch
    {
        0 => newVerified ? MsiUpdateStatus.Applied : MsiUpdateStatus.RecoveryRequired,
        3010 or 1641 => MsiUpdateStatus.RebootRequired,
        1618 => oldVerified ? MsiUpdateStatus.Busy : MsiUpdateStatus.RecoveryRequired,
        1602 => oldVerified ? MsiUpdateStatus.Cancelled : MsiUpdateStatus.RecoveryRequired,
        _ => oldVerified ? MsiUpdateStatus.FailedRestored : MsiUpdateStatus.RecoveryRequired
    };
    private static MsiUpdateResult Record(WindowsUpdateLocation location, string id, MsiUpdateResult result)
    {
        InstallFileSystem.WriteNew(Path.Combine(location.PrivateDirectory("Results"), "msi-" + id + ".json"), JsonSerializer.SerializeToUtf8Bytes(result));
        return result;
    }
}
