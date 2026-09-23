using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CodeRim.Core.Services;

[SupportedOSPlatform("windows")]
internal sealed record WindowsUpdateLocation(string Parent, string InstallRoot, string Capsules)
{
    internal static WindowsUpdateLocation Open()
    {
        // The shell's known folder is independent of LOCALAPPDATA/HOME environment overrides.
        var id = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        if (SHGetKnownFolderPath(ref id, 0, 0, out var pointer) != 0) throw new IOException("The per-user installation location is unavailable.");
        string local;
        try { local = Marshal.PtrToStringUni(pointer) ?? throw new IOException("The per-user installation location is unavailable."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
        var parent = System.IO.Path.Combine(local, "Programs");
        if (!System.IO.Path.IsPathFullyQualified(parent) || parent.StartsWith("\\\\", StringComparison.Ordinal)  )
            throw new IOException("The local installation parent is required.");
        AssertSafeParent(InstallFileSystem.LongWindowsPath(local));
        if (!Directory.Exists(parent)) InstallFileSystem.CreatePrivateDirectory(parent);
        parent = InstallFileSystem.LongWindowsPath(parent); InstallFileSystem.CheckPath(parent);
        // Check every directory up to the current user's LocalAppData, not shared volume/profile ancestors.
        AssertSafeParent(parent); AssertSafeParent(InstallFileSystem.LongWindowsPath(local));
        var capsules = System.IO.Path.Combine(parent, ".CodeRimUpdate");
        if (!Directory.Exists(capsules)) InstallFileSystem.CreatePrivateDirectory(capsules);
        InstallFileSystem.AssertPrivateDirectory(capsules);
        return new(parent, System.IO.Path.Combine(parent, "CodeRim"), capsules);
    }
    internal string Capsule(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var parsed) || parsed.ToString("N") != id) throw new InvalidDataException("Invalid update operation.");
        var result = System.IO.Path.Combine(Capsules, id); InstallFileSystem.CheckPath(result); return result;
    }
    internal string PrivateDirectory(string name)
    {
        if (name is not ("Runtime" or "Downloads" or "Launchers" or "Results" or "Completed")) throw new InvalidDataException("Invalid update directory.");
        var path = System.IO.Path.Combine(Capsules, name);
        if (!Directory.Exists(path)) InstallFileSystem.CreatePrivateDirectory(path);
        InstallFileSystem.AssertPrivateDirectory(path); return path;
    }
    internal string Launcher(string id, bool create = false)
    {
        _ = Capsule(id); var path = System.IO.Path.Combine(PrivateDirectory("Launchers"), id);
        if (create) InstallFileSystem.CreatePrivateDirectory(path);
        else InstallFileSystem.AssertPrivateDirectory(path);
        return path;
    }
    internal Dictionary<string, string?> ChildEnvironment() => new()
    {
        ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        // A self-contained bundle needs this before managed Main. Never inherit ambient TEMP or a profile path.
        ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = PrivateDirectory("Runtime")
    };
    internal void AssertWorkerOutsideInstallation(string worker)
    {
        worker = InstallFileSystem.LongWindowsPath(worker); InstallFileSystem.CheckPath(worker);
        var parent = System.IO.Path.GetDirectoryName(worker)!;
        var id = System.IO.Path.GetFileName(parent);
        if (!string.Equals(parent, Launcher(id), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(System.IO.Path.GetFileName(worker), SignedInstallPayload.WorkerName, StringComparison.Ordinal))
            throw new IOException("The updater must run from its protected launcher capsule.");
        InstallFileSystem.AssertPrivateDirectory(Capsules); InstallFileSystem.AssertPrivateDirectory(PrivateDirectory("Launchers"));
        InstallFileSystem.AssertPrivateDirectory(parent); AssertSafeParent(Parent);
    }
    internal static void AssertSafeParent(string path)
    {
        InstallFileSystem.CheckPath(path); InstallFileSystem.CheckStreams(path);
        using var identity = WindowsIdentity.GetCurrent(); var user = identity.User ?? throw new IOException("The current user is unavailable.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        AssertSafeParentSecurity(security, user);
    }
    internal static void AssertSafeParentSecurity(DirectorySecurity security, SecurityIdentifier user)
    {
        // Known-folder parents may be owned by Windows or Administrators (including MSI-created
        // Programs folders). Those principals are already trusted writers; foreign owners/writers
        // remain forbidden. The updater's own capsules still require exact-user private ACLs.
        var trusted = new[] { user.Value, "S-1-5-18", "S-1-5-32-544" };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !trusted.Contains(owner.Value, StringComparer.Ordinal)) throw new IOException("The installation parent has an untrusted owner.");
        const FileSystemRights dangerous = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & dangerous) != 0 && !trusted.Contains(rule.IdentityReference.Value, StringComparer.Ordinal))
                throw new IOException("The installation parent is writable by another principal.");
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);
}
