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
    public CredentialVault() { Directory.CreateDirectory(root); RestrictDirectory(root); }
    public void Save(string id, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            var path = PathFor(id); var temporary = path + ".new";
            File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string? Load(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Credential entry is too large.");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Delete(string id) => File.Delete(PathFor(id));
    private string PathFor(string id) => Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".bin");
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
