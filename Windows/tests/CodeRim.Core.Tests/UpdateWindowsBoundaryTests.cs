using System.Security.AccessControl;
using System.Runtime.Versioning;
using System.Security.Principal;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class UpdateWindowsBoundaryTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    [Fact(Skip = "Requires native Windows ACL checks on a private synthetic directory", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void PrivateParentPassesButForeignWritePermissionIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "coderim-parent-acl-" + Guid.NewGuid().ToString("N"));
        InstallFileSystem.CreatePrivateDirectory(path);
        try
        {
            WindowsUpdateLocation.AssertSafeParent(path);
            var info = new DirectoryInfo(path); var original = info.GetAccessControl(); var changed = info.GetAccessControl();
            changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow)); info.SetAccessControl(changed);
            try { Assert.Throws<IOException>(() => WindowsUpdateLocation.AssertSafeParent(path)); }
            finally { info.SetAccessControl(original); }
        }
        finally { Directory.Delete(path); }
    }
    [Fact(Skip = "Requires native Windows Authenticode rejection of a synthetic unsigned executable", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void UnsignedSyntheticPeCannotAuthorizeAnUpdate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "coderim-unsigned-" + Guid.NewGuid().ToString("N") + ".exe"); File.WriteAllBytes(path, [0x4d, 0x5a, 0, 0]);
        try { Assert.Throws<InvalidDataException>(() => WindowsPublisherTrust.Verify(path, PublisherPin.FromBuildMetadata([new string('a', 64)])!)); }
        finally { File.Delete(path); }
    }
}
