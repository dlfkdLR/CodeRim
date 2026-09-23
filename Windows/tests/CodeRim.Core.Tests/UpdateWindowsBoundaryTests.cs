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
    [Theory(Skip = "Requires native Windows security descriptors", SkipUnless = nameof(IsWindows))]
    [InlineData("S-1-5-21-111-222-333-1001", false, true)]
    [InlineData("S-1-5-18", false, true)]
    [InlineData("S-1-5-32-544", false, true)]
    [InlineData("S-1-5-21-111-222-333-1001", true, false)]
    [InlineData("S-1-5-18", true, false)]
    [InlineData("S-1-5-32-544", true, false)]
    [InlineData("S-1-5-21-111-222-333-1002", false, false)]
    [InlineData("S-1-1-0", false, false)]
    [SupportedOSPlatform("windows")]
    public void KnownFolderTrustRejectsForeignOwnersAndWriters(string ownerSid, bool foreignWrite, bool allowed)
    {
        var user = new SecurityIdentifier("S-1-5-21-111-222-333-1001");
        var security = new DirectorySecurity(); security.SetOwner(new SecurityIdentifier(ownerSid));
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        if (foreignWrite) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow));
        if (allowed) WindowsUpdateLocation.AssertSafeParentSecurity(security, user);
        else Assert.Throws<IOException>(() => WindowsUpdateLocation.AssertSafeParentSecurity(security, user));
    }
    [Fact(Skip = "Requires native Windows MSI file ACL checks", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void MsiServiceReceivesReadOnlyAccessWithoutExposingPrivateDirectories()
    {
        var path = Path.Combine(Path.GetTempPath(), "coderim-msi-acl-" + Guid.NewGuid().ToString("N"));
        InstallFileSystem.CreatePrivateDirectory(path);
        var file = Path.Combine(path, "test.msi"); File.WriteAllText(file, "Synthetic public installer bytes");
        try
        {
            using var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            MsiUpdateExecution.AllowInstallerServiceRead(file);
            InstallFileSystem.AssertPrivateDirectory(path);
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileInfo(file).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.Equal(2, rules.Length);
            var system = Assert.Single(rules, r => r.IdentityReference.Value == "S-1-5-18");
            Assert.Equal(AccessControlType.Allow, system.AccessControlType);
            Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, system.FileSystemRights);
            Assert.Throws<IOException>(() => { using var changed = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        }
        finally { File.Delete(file); Directory.Delete(path); }
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
