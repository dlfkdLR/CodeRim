using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class UpdateRuntimeBoundaryTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    [Fact(Skip = "Requires native Windows private ACL and environment fixture", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void ChildRuntimeUsesOnlyExplicitPrivateExtractionAndWorkerLeavesCannotEscapeFixedAncestor()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new InstallFixture(); var capsules = Path.Combine(fixture.Root, ".CodeRimUpdate"); InstallFileSystem.CreatePrivateDirectory(capsules);
        var location = new WindowsUpdateLocation(fixture.Root, Path.Combine(fixture.Root, "CodeRim"), capsules);
        var environment = location.ChildEnvironment();
        Assert.Equal(4, environment.Count); Assert.Equal(Path.Combine(capsules, "Runtime"), environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]);
        InstallFileSystem.AssertPrivateDirectory(environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]!);
        Assert.False(environment.ContainsKey("TEMP")); Assert.False(environment.ContainsKey("USERPROFILE")); Assert.False(environment.ContainsKey("PATH"));
        var launcher = location.Launcher(Guid.NewGuid().ToString("N"), create: true); var worker = Path.Combine(launcher, SignedInstallPayload.WorkerName); File.WriteAllText(worker, "synthetic inert bytes");
        location.AssertWorkerOutsideInstallation(worker);
        var other = fixture.NewStage(); var outside = Path.Combine(other, SignedInstallPayload.WorkerName); File.WriteAllText(outside, "synthetic inert bytes");
        Assert.ThrowsAny<Exception>(() => location.AssertWorkerOutsideInstallation(outside));
    }
    [Fact(Skip = "Requires native Windows foreign DeleteChild ACL rejection", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void PrivateLeafCannotHideForeignDeleteChildPermissionOnInstallationParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new InstallFixture(); var capsules = Path.Combine(fixture.Root, ".CodeRimUpdate"); InstallFileSystem.CreatePrivateDirectory(capsules);
        var location = new WindowsUpdateLocation(fixture.Root, Path.Combine(fixture.Root, "CodeRim"), capsules);
        var launcher = location.Launcher(Guid.NewGuid().ToString("N"), create: true); var worker = Path.Combine(launcher, SignedInstallPayload.WorkerName); File.WriteAllText(worker, "synthetic inert bytes");
        var info = new DirectoryInfo(fixture.Root); var original = info.GetAccessControl(); var changed = info.GetAccessControl();
        changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow)); info.SetAccessControl(changed);
        try { Assert.Throws<IOException>(() => location.AssertWorkerOutsideInstallation(worker)); }
        finally { info.SetAccessControl(original); }
    }
}
