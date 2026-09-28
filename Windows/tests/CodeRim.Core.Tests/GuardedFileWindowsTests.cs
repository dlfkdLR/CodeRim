using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

/// <summary>Real Windows file operations, independent of WPF's account fixture and error wrapping.</summary>
public sealed class GuardedFileWindowsTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private readonly string root = Path.Combine(Path.GetTempPath(), "coderim-file-" + Guid.NewGuid().ToString("N"));
    private string Login()
    {
        var directory = Path.Combine(root, "로그인-😀"); Directory.CreateDirectory(directory);
        return Path.Combine(directory, "auth.json");
    }
    [Fact(Skip = "Requires Windows directory handles and file ACL semantics", SkipUnless = nameof(IsWindows))]
    public void LongUnicodeLoginPathsRetainPublicationAndDirectoryProtection()
    {
        var directory = Path.Combine(root, new string('a', 120), new string('b', 120), "로그인-😀");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "auth.json"); Assert.True(directory.Length > 260);
        Assert.Null(GuardedFile.ReadIfPresent(file));
        using (Acquire(file))
        {
            GuardedFile.CreateIfAbsent(file, "synthetic-private-login");
            Assert.Equal("synthetic-private-login", GuardedFile.Read(file));
            Assert.ThrowsAny<IOException>(() => Directory.Move(directory, directory + "-moved"));
        }
        Directory.Move(directory, directory + "-moved"); Directory.Move(directory + "-moved", directory);
        Assert.Equal("synthetic-private-login", GuardedFile.Read(file));
    }
    [Fact(Skip = "Requires Windows sharing and file ACL semantics", SkipUnless = nameof(IsWindows))]
    public void SignedOutPublicationCreatesTheCompletePrivateLogin()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Login(); Assert.Null(GuardedFile.ReadIfPresent(file));
        // Let the original native exception escape: never replace its cause with
        // a generic account-generation assertion in the higher-level UI fixture.
        GuardedFile.CreateIfAbsent(file, "synthetic-private-login");
        Assert.Equal("synthetic-private-login", GuardedFile.Read(file));
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new FileInfo(file).GetAccessControl(); Assert.True(acl.AreAccessRulesProtected);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow) Assert.Equal(identity.User, rule.IdentityReference);
    }
    [Fact(Skip = "Requires Windows directory handles", SkipUnless = nameof(IsWindows))]
    public void PathMoveIsBlockedButGuardedPublicationWorksWhileAncestorsArePinned()
    {
        var file = Login(); var temporary = file + ".fixture.tmp";
        using (Acquire(file))
        {
            GuardedFile.WritePrivate(temporary, "synthetic-staging");
            Assert.Equal("synthetic-staging", GuardedFile.Read(temporary));
            // Native baseline 742ee11: MoveFile reopens the destination parent
            // for FILE_ADD_FILE, conflicting with the intentional deny-write lease.
            var error = Assert.Throws<IOException>(() => File.Move(temporary, file, overwrite: false));
            Assert.Equal(32, error.HResult & 0xffff); // ERROR_SHARING_VIOLATION, not an unrelated failure.
            Assert.False(File.Exists(file)); Assert.Equal("synthetic-staging", GuardedFile.Read(temporary));
            GuardedFile.CreateIfAbsent(file, "synthetic-staging");
            File.Delete(temporary);
        }
        Assert.Equal("synthetic-staging", GuardedFile.Read(file));
    }
    [Fact(Skip = "Requires Windows directory handles", SkipUnless = nameof(IsWindows))]
    public void LeaseBlocksDirectoryAndAncestorMovesUntilDisposed()
    {
        var file = Login(); var directory = Path.GetDirectoryName(file)!;
        using (Acquire(file))
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(directory, directory + "-moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(root, root + "-moved"));
        }
        Directory.Move(directory, directory + "-moved"); Directory.Move(directory + "-moved", directory);
        Directory.Move(root, root + "-moved"); Directory.Move(root + "-moved", root);
    }
    [Fact(Skip = "Requires Windows exclusive publication", SkipUnless = nameof(IsWindows))]
    public void CompetingLoginIsNeverReplaced()
    {
        var file = Login(); GuardedFile.WritePrivate(file, "other-login");
        Assert.ThrowsAny<IOException>(() => GuardedFile.CreateIfAbsent(file, "stale-login"));
        Assert.Equal("other-login", GuardedFile.Read(file));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }
    [Fact(Skip = "Requires Windows native rename semantics", SkipUnless = nameof(IsWindows))]
    public async Task ConcurrentPublishersKeepExactlyOneCompleteUnicodeLogin()
    {
        var file = Path.Combine(Path.GetDirectoryName(Login())!, "로그인-😀.json");
        var values = Enumerable.Range(0, 8).Select(index => index + new string('한', 16384)).ToArray();
        using var start = new ManualResetEventSlim(false);
        var writers = values.Select(value => Task.Run(() => {
            start.Wait();
            try { GuardedFile.CreateIfAbsent(file, value); return value; }
            catch (IOException) { return null; }
        })).ToArray();
        start.Set(); var results = await Task.WhenAll(writers);
        Assert.Equal(Assert.Single(results, value => value is not null), GuardedFile.Read(file));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }
    private static IDisposable Acquire(string file)
    {
        var type = typeof(GuardedFile).Assembly.GetType("CodeRim.Core.Services.LoginDirectoryLease", throwOnError: true)!;
        var method = type.GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)!;
        try { return (IDisposable)method.Invoke(null, [file])!; }
        catch (TargetInvocationException error) when (error.InnerException is { } cause)
        { ExceptionDispatchInfo.Capture(cause).Throw(); throw; }
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (Directory.Exists(root + "-moved")) Directory.Delete(root + "-moved", true);
    }
}
