using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

internal static class SignedInstallPayload
{
    internal const string WorkerName = "CodeRim.UpdateWorker.exe";
    internal const int ManifestResourceId = 21001;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    [SupportedOSPlatform("windows")]
    internal static InstallPayloadManifest Read(WindowsPublisherTrust worker)
    {
        var (text, architecture) = ReadResource(worker.Stream);
        return BindManifest(text, architecture, worker.Size, worker.Sha256);
    }
    // This parser only returns bytes; it is not an authentication API. Production calls it with a held, verified PE lease.
    internal static (string Text, string Architecture) ReadResource(FileStream input)
    {
        input.Position = 0; using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
        var header = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("A native PE worker is required.");
        var architecture = pe.PEHeaders.CoffHeader.Machine switch { Machine.Amd64 => "x64", Machine.Arm64 => "arm64", _ => throw new InvalidDataException("Unsupported worker architecture.") };
        if (header.CertificateTableDirectory.Size < 8 || header.CertificateTableDirectory.RelativeVirtualAddress <= 0
            || (long)header.CertificateTableDirectory.RelativeVirtualAddress + header.CertificateTableDirectory.Size > input.Length) throw new InvalidDataException("An embedded Authenticode signature is required.");
        var directory = header.ResourceTableDirectory;
        if (directory.Size is < 16 or > 4 * 1024 * 1024) throw new InvalidDataException("The signed resource directory is missing or too large.");
        var root = ReadRva(directory.RelativeVirtualAddress, directory.Size);
        uint offset = Find(root, 0, 10, directory: true); // RT_RCDATA, numeric id, neutral language only.
        offset = Find(root, offset, ManifestResourceId, directory: true);
        offset = Find(root, offset, 0, directory: false);
        if (offset > root.Length - 16) throw new InvalidDataException("Invalid signed resource data entry.");
        var data = root.AsSpan((int)offset, 16); var rva = BinaryPrimitives.ReadUInt32LittleEndian(data); var size = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (size is 0 or > InstallPayloadManifest.MaximumManifestBytes || rva > int.MaxValue) throw new InvalidDataException("Invalid signed manifest length.");
        return (StrictUtf8.GetString(ReadRva((int)rva, (int)size)), architecture);
        byte[] ReadRva(int rva, int size)
        {
            var sections = pe.PEHeaders.SectionHeaders.Where(section => rva >= section.VirtualAddress && (long)rva + size <= (long)section.VirtualAddress + section.SizeOfRawData).ToArray();
            if (sections.Length != 1) throw new InvalidDataException("The signed resource is outside a unique raw PE section.");
            var section = sections[0]; var physical = (long)section.PointerToRawData + rva - section.VirtualAddress;
            var signature = header.CertificateTableDirectory;
            if (physical < 0 || physical + size > input.Length || physical < (long)signature.RelativeVirtualAddress + signature.Size && physical + size > signature.RelativeVirtualAddress)
                throw new InvalidDataException("The resource overlaps unauthenticated certificate data.");
            input.Position = physical; var bytes = new byte[size]; input.ReadExactly(bytes); return bytes;
        }
    }
    private static uint Find(byte[] root, uint offset, int id, bool directory)
    {
        if (offset > root.Length - 16) throw new InvalidDataException("Invalid resource tree.");
        var start = (int)offset; var count = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(start + 12)) + BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(start + 14));
        if (count > 512 || (long)start + 16 + count * 8 > root.Length) throw new InvalidDataException("Invalid resource tree bounds.");
        uint? found = null;
        for (var index = 0; index < count; index++)
        {
            var entry = root.AsSpan(start + 16 + index * 8, 8); if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != (uint)id) continue;
            var target = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            if (found is not null || ((target & 0x80000000) != 0) != directory) throw new InvalidDataException("Ambiguous signed manifest resource.");
            found = target & 0x7fffffff;
        }
        return found ?? throw new InvalidDataException("Missing signed manifest resource.");
    }
    internal static InstallPayloadManifest BindManifest(string json, string architecture, long workerSize, string workerHash)
    {
        var manifest = InstallPayloadManifest.Parse(json);
        if (manifest.Architecture != architecture || manifest.Files.Any(file => file.Path.Equals(WorkerName, StringComparison.OrdinalIgnoreCase))
            || !manifest.Files.Any(file => file.Path == "CodeRim.exe") || !manifest.Files.Any(file => file.Path == "CodeRimCLI.exe"))
            throw new InvalidDataException("The signed payload contract is invalid.");
        return InstallPayloadManifest.Parse(JsonSerializer.Serialize(new { schema = 1, product = "CodeRim", version = manifest.Version.ToString(3), architecture,
            files = manifest.Files.Append(new(WorkerName, workerSize, workerHash)).Select(file => new { path = file.Path, size = file.Size, sha256 = file.Sha256 }) }));
    }
    internal static void CopyVerified(string source, string destination, long expectedSize, string expectedHash)
    {
        InstallFileSystem.CheckPath(source); InstallFileSystem.CheckStreams(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = InstallFileSystem.CreateFile(destination);
        CopyAndHash(input, output, expectedSize, expectedHash); output.Flush(true);
    }
    internal static void CopyAndHash(Stream input, Stream output, long size, string hash)
    {
        if (size is < 0 or > InstallPayloadManifest.MaximumFileBytes || !PublisherPin.IsHash(hash)) throw new InvalidDataException("Invalid copy identity.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, size - total + 1)); if (read == 0) break;
            total += read; if (total > size) throw new InvalidDataException("Payload exceeds its signed size.");
            digest.AppendData(buffer, 0, read); output.Write(buffer, 0, read);
        }
        if (total != size || Convert.ToHexStringLower(digest.GetHashAndReset()) != hash) throw new InvalidDataException("Payload digest does not match.");
    }
    internal static void ExtractIdentityFile(string archivePath, string name, string destination, InstallPayloadFile? expected = null)
    {
        using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > ReleasePackageDownload.MaximumPackageBytes) throw new InvalidDataException("Payload archive exceeds its limit.");
        InstallPayloadManifest.CheckCentralDirectory(input); input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read); var matches = zip.Entries.Where(entry => entry.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].FullName != name) throw new InvalidDataException("Payload identity file is missing or ambiguous.");
        var entry = matches[0]; var kind = (entry.ExternalAttributes >> 16) & 0xf000;
        if (kind is not (0 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || entry.Length is < 1 or > InstallPayloadManifest.MaximumFileBytes
            || entry.Length > Math.Max(1, entry.CompressedLength) * 200) throw new InvalidDataException("Unsupported payload identity entry.");
        using var source = entry.Open(); using var output = InstallFileSystem.CreateFile(destination);
        if (expected is not null) CopyAndHash(source, output, expected.Size, expected.Sha256);
        else
        {
            var buffer = new byte[65536]; long total = 0;
            while (true) { var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, entry.Length - total + 1)); if (count == 0) break;
                total += count; if (total > entry.Length) throw new InvalidDataException("Worker exceeds its ZIP size."); output.Write(buffer, 0, count); }
            if (total != entry.Length) throw new InvalidDataException("Truncated worker.");
        }
        output.Flush(true);
    }
    internal static void ExtractIdentityTo(string archivePath, string name, Stream output, InstallPayloadFile? expected = null)
    {
        using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > ReleasePackageDownload.MaximumPackageBytes) throw new InvalidDataException("Payload archive exceeds its limit.");
        InstallPayloadManifest.CheckCentralDirectory(input); input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read); var matches = zip.Entries.Where(entry => entry.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].FullName != name) throw new InvalidDataException("Payload identity file is missing or ambiguous.");
        var entry = matches[0]; var kind = (entry.ExternalAttributes >> 16) & 0xf000;
        if (kind is not (0 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || entry.Length is < 1 or > InstallPayloadManifest.MaximumFileBytes
            || entry.Length > Math.Max(1, entry.CompressedLength) * 200) throw new InvalidDataException("Unsupported payload identity entry.");
        using var source = entry.Open();
        if (expected is not null) CopyAndHash(source, output, expected.Size, expected.Sha256);
        else
        {
            var buffer = new byte[65536]; long total = 0;
            while (true) { var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, entry.Length - total + 1)); if (count == 0) break;
                total += count; if (total > entry.Length) throw new InvalidDataException("Worker exceeds its ZIP size."); output.Write(buffer, 0, count); }
            if (total != entry.Length) throw new InvalidDataException("Truncated worker.");
        }
        output.Flush();
    }
}
