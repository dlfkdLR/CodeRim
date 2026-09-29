using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ProfileCredentialFileRegression(AppSettingsStore settings, string directory)
    {
        var root = Path.Combine(CompanionFile.DataDirectory, "profile-credential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "auth.json");
        var beforeSettings = settings.Current;
        var checks = new List<string>();
        ProfileUsageStore? profile = null;
        TaskCompletionSource<ProfileUsageSnapshot>? pending = null;
        Task? refresh = null;
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Fixture user is unavailable");
        var readers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        string CredentialJson(string subject) => JsonSerializer.Serialize(new { tokens = new {
            access_token = "header." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { sub = subject })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature",
            account_id = "synthetic-profile-workspace" } });
        FileSecurity Security(FileSystemRights? foreign = null, bool inherited = false)
        {
            var acl = new FileSecurity(); acl.SetOwner(user); acl.SetAccessRuleProtection(!inherited, false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            if (foreign is { } rights) acl.AddAccessRule(new FileSystemAccessRule(readers, rights, AccessControlType.Allow));
            return acl;
        }
        void Apply(FileSecurity acl) => new FileInfo(file).SetAccessControl(acl);
        void Rejected(string name)
        {
            try { _ = ProfileUsageStore.LoadCredential(file); }
            catch (ProfileCredentialException) { checks.Add(name); return; }
            throw new InvalidOperationException("Profile reader accepted " + name);
        }
        try
        {
            var parent = new DirectorySecurity(); parent.SetOwner(user); parent.SetAccessRuleProtection(true, false);
            foreach (var (sid, rights) in new[] { (user, FileSystemRights.FullControl), (readers, FileSystemRights.ReadAndExecute) })
                parent.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).SetAccessControl(parent);
            File.WriteAllText(file, CredentialJson("original")); Apply(Security(inherited: true));
            var inheritedAcl = new FileInfo(file).GetAccessControl();
            Require(inheritedAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(x => x.IsInherited && x.IdentityReference.Equals(readers) && (x.FileSystemRights & FileSystemRights.ReadData) != 0),
                "Read-only profile fixture did not inherit its reader ACL");
            var originalBytes = File.ReadAllBytes(file); var originalAcl = inheritedAcl.GetSecurityDescriptorBinaryForm();
            var credential = ProfileUsageStore.LoadCredential(file);
            Require(credential.AccountKey is not null && originalBytes.SequenceEqual(File.ReadAllBytes(file))
                && originalAcl.SequenceEqual(new FileInfo(file).GetAccessControl().GetSecurityDescriptorBinaryForm()),
                "Reading inherited profile credentials changed the file or ACL");
            checks.Add("Inherited read-only ACL accepted without file or ACL mutation");
            Apply(Security(FileSystemRights.ReadAndExecute));
            Require(ProfileUsageStore.LoadCredential(file).SameAccount(credential), "Explicit read-only ACL changed the account");
            checks.Add("Explicit read-only ACL accepted");
            foreach (var rights in new[] { FileSystemRights.WriteData, FileSystemRights.AppendData, FileSystemRights.WriteAttributes,
                FileSystemRights.WriteExtendedAttributes, FileSystemRights.Delete, FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership })
            {
                Apply(Security(rights)); Rejected("foreign " + rights);
            }
            var foreignOwner = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            try
            {
                var wrongOwner = Security(); wrongOwner.SetOwner(foreignOwner);
                var assigned = false;
                // ERROR_INVALID_OWNER can surface as InvalidOperationException on
                // Windows. Keep assignment catches separate from the rejection assertion.
                try { Apply(wrongOwner); assigned = true; }
                catch (UnauthorizedAccessException) { }
                catch (IOException error) when ((error.HResult & 0xFFFF) is 1307 or 1314) { }
                catch (InvalidOperationException error) when (error.HResult == unchecked((int)0x80131509)
                    && user.Equals(new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier)))) { }
                if (assigned) Rejected("foreign owner");
                else
                {
                    Require(user.Equals(new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier))),
                        "The unavailable owner fixture changed the credential owner");
                    checks.Add("Owner check skipped: caller cannot assign another owner");
                }
            }
            finally { Apply(Security()); }
            Apply(Security()); File.WriteAllText(file, new string('x', 262145)); Rejected("oversized file");
            File.WriteAllText(file, CredentialJson("original")); Apply(Security(FileSystemRights.ReadAndExecute));
            var linked = Path.Combine(root, "linked.json");
            var linkChecked = false;
            try
            {
                File.CreateSymbolicLink(linked, file);
                try { _ = ProfileUsageStore.LoadCredential(linked); }
                catch (ProfileCredentialException) { linkChecked = true; }
                Require(linkChecked, "Profile reader accepted a linked credential file");
                checks.Add("Linked credential file rejected");
            }
            catch (UnauthorizedAccessException) { checks.Add("Link check skipped: caller lacks symbolic-link privilege"); }
            catch (IOException error) when ((error.HResult & 0xFFFF) == 1314) { checks.Add("Link check skipped: caller lacks symbolic-link privilege"); }
            finally { if (File.Exists(linked)) File.Delete(linked); }
            var linkedParent = Path.Combine(root, "linked-parent");
            try
            {
                Directory.CreateSymbolicLink(linkedParent, root);
                var rejected = false;
                try { _ = ProfileUsageStore.LoadCredential(Path.Combine(linkedParent, "auth.json")); }
                catch (ProfileCredentialException) { rejected = true; }
                Require(rejected, "Profile reader accepted a linked parent directory");
                checks.Add("Linked parent directory rejected");
            }
            catch (UnauthorizedAccessException) { checks.Add("Parent link check skipped: caller lacks symbolic-link privilege"); }
            catch (IOException error) when ((error.HResult & 0xFFFF) == 1314) { checks.Add("Parent link check skipped: caller lacks symbolic-link privilege"); }
            finally { if (Directory.Exists(linkedParent)) Directory.Delete(linkedParent); }
            settings.Save(beforeSettings with { ProfileSyncEnabled = true });
            var completion = new TaskCompletionSource<ProfileUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = completion;
            Require(ProfileUsageStore.LoadCredential(file).SameAccount(credential), "The response fixture did not start with readable credentials");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            profile = new ProfileUsageStore(settings, true, () => ProfileUsageStore.LoadCredential(file), (_, _, _) =>
            {
                calls++; entered.TrySetResult(true); return completion.Task;
            }, () => false);
            refresh = profile.RefreshAsync(true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Idle();
            Require(calls == 1 && !refresh.IsCompleted && profile.Status == ProfileUsageStatus.Refreshing,
                "The ACL response fixture did not enter an in-flight fetch");
            Apply(Security(FileSystemRights.AppendData));
            completion.SetResult(new(1, 2, 3, 4, DateOnly.FromDateTime(DateTime.Today), DateTimeOffset.Now, credential.AccountKey));
            await refresh;
            Require(profile.Status == ProfileUsageStatus.CredentialsUnavailable && profile.Snapshot is null,
                "A request retained account history after credential mutation rights changed");
            checks.Add("ACL change during response clears account history");
            File.WriteAllText(Path.Combine(directory, "windows-profile-credential-acl.json"),
                JsonSerializer.Serialize(new { completed = true, checks }, JsonOptions));
        }
        finally
        {
            profile?.Dispose(); pending?.TrySetCanceled();
            if (refresh is not null)
            {
                try { await refresh; } catch (OperationCanceledException) { }
            }
            settings.Save(beforeSettings); Directory.Delete(root, true);
        }
    }
}
