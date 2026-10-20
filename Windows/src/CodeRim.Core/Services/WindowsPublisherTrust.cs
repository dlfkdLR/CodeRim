using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodeRim.Core.Services;

// Native chain work may retrieve revocation information. Invoke only inside the supervised job.
[SupportedOSPlatform("windows")]
internal sealed class WindowsPublisherTrust : IDisposable
{
    private readonly FileStream lease;
    internal string Path { get; }
    internal long Size => lease.Length;
    internal string Sha256 { get; }
    internal FileStream Stream => lease;
    private WindowsPublisherTrust(string path, FileStream lease, string sha256) { Path = path; this.lease = lease; Sha256 = sha256; }
    internal static WindowsPublisherTrust Verify(string path, PublisherPin pin)
    {
        InstallFileSystem.CheckPath(path); InstallFileSystem.CheckStreams(path);
        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (lease.Length is < 1 or > InstallPayloadManifest.MaximumFileBytes) throw new InvalidDataException("Invalid signed executable size.");
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            var name = Marshal.StringToCoTaskMemUni(path); var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Revocation = 1, Choice = 1, File = infoPointer,
                StateAction = 1, Flags = 0x80 | 0x2000 }; // Chain revocation excluding root; disable obsolete MD2/MD4.
            try
            {
                Marshal.StructureToPtr(new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = name, Handle = lease.SafeFileHandle.DangerousGetHandle() }, infoPointer, false);
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new InvalidDataException("The executable publisher could not be verified.");
                var provider = WTHelperProvDataFromStateData(data.State); var signerPointer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                if (provider == 0 || signerPointer == 0) throw new InvalidDataException("Verified signer evidence is missing.");
                var signer = Marshal.PtrToStructure<Signer>(signerPointer);
                if (signer.Error != 0 || signer.Certificates == 0 || signer.Chain == 0 || signer.SignerInfo == 0) throw new InvalidDataException("Verified signer chain is missing.");
                var signedInfo = Marshal.PtrToStructure<MessageSigner>(signer.SignerInfo);
                if (signedInfo.Hash.Oid == 0 || Marshal.PtrToStringAnsi(signedInfo.Hash.Oid) != "2.16.840.1.101.3.4.2.1")
                    throw new InvalidDataException("The verified Authenticode signature must use SHA-256.");
                var certificate = Marshal.PtrToStructure<ProviderCertificate>(signer.Chain);
                if (certificate.Context == 0) throw new InvalidDataException("Verified publisher certificate is missing.");
                var context = Marshal.PtrToStructure<CertificateContext>(certificate.Context);
                if (context.Size is 0 or > 65536 || context.Encoded == 0) throw new InvalidDataException("Verified publisher certificate is invalid.");
                var encoded = new byte[context.Size]; Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
                using var verified = X509CertificateLoader.LoadCertificate(encoded);
                if (!pin.Matches(verified.PublicKey.ExportSubjectPublicKeyInfo())) throw new InvalidDataException("The executable publisher does not match the compiled pin.");
            }
            finally
            {
                data.StateAction = 2; _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                Marshal.FreeHGlobal(infoPointer); Marshal.FreeCoTaskMem(name);
            }
            lease.Position = 0; var hash = Convert.ToHexStringLower(SHA256.HashData(lease)); lease.Position = 0;
            return new(path, lease, hash);
        }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose() => lease.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { internal uint Size; internal nint Path; internal nint Handle; internal nint Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        internal uint Size; internal nint Callback; internal nint Client; internal uint Ui; internal uint Revocation; internal uint Choice; internal nint File;
        internal uint StateAction; internal nint State; internal nint Url; internal uint Flags; internal uint UiContext; internal nint SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Signer
    {
        internal uint Size; internal System.Runtime.InteropServices.ComTypes.FILETIME VerifyTime; internal uint Certificates; internal nint Chain;
        internal uint Type; internal nint SignerInfo; internal uint Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal uint Size; internal nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Algorithm { internal nint Oid; internal Blob Parameters; }
    [StructLayout(LayoutKind.Sequential)] private struct MessageSigner { internal uint Version; internal Blob Issuer; internal Blob Serial; internal Algorithm Hash; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate { internal uint Size; internal nint Context; }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContext { internal uint Encoding; internal nint Encoded; internal uint Size; internal nint Info; internal nint Store; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern nint WTHelperProvDataFromStateData(nint state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
}
