using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed class CredentialVault
{
    private readonly string root = Path.Combine(CompanionFile.DataDirectory, "vault");
    private readonly AtomicCredentialFiles entries;
    internal sealed record Snapshot(string Value, string Version);
    public CredentialVault()
    {
        if (Path.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked credential directories are not supported.");
        Directory.CreateDirectory(root); RestrictDirectory(root); entries = new(root);
    }
    public void Save(string id, string value) => Store(id, value, null, conditional: false);
    internal bool SaveIfUnchanged(string id, string? expectedVersion, string value) => Store(id, value, expectedVersion, conditional: true);
    private bool Store(string id, string value, string? expectedVersion, bool conditional)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            if (bytes.Length > AtomicCredentialFiles.MaximumBytes - 4096) throw new InvalidDataException("Credential entry is too large.");
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            if (conditional) return entries.CompareExchange(id, expectedVersion, encrypted);
            entries.Write(id, encrypted); return true;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    // Compare imported-connection versions without decrypting an unrelated or damaged login.
    internal string? Version(string id) => entries.Read(id) is { } value ? AtomicCredentialFiles.Version(value) : null;
    public string? Load(string id) => LoadVersioned(id)?.Value;
    internal Snapshot? LoadVersioned(string id)
    {
        var encrypted = entries.Read(id); if (encrypted is null) return null;
        var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return new(Encoding.UTF8.GetString(bytes), AtomicCredentialFiles.Version(encrypted)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Delete(string id) => entries.Delete(id);
    internal Task<FileStream> AcquireRefreshAsync(string id, CancellationToken token) => entries.AcquireRefreshAsync(id, token);
    public static void RestrictDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
