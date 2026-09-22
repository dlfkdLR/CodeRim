using System.Buffers.Binary;
using System.Text;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

// Synthetic PE structures test parsing only. The certificate table is deliberately NOT a valid signature and is never installed/trusted/executed.
public sealed class UpdatePeResourceTests
{
    [Theory]
    [InlineData(0x8664, "x64")][InlineData(0xaa64, "arm64")]
    public void ReadsExactNeutralNumericManifestResource(int machine, string architecture)
    {
        using var file = Write(Image(machine)); var result = SignedInstallPayload.ReadResource(file);
        Assert.Equal("{}", result.Text); Assert.Equal(architecture, result.Architecture);
    }
    [Theory]
    [InlineData("unsigned")][InlineData("wrong-id")][InlineData("wrong-language")][InlineData("directory-loop")]
    [InlineData("wrong-type")][InlineData("count-bomb")][InlineData("certificate-overlap")][InlineData("virtual-only")]
    [InlineData("size-bomb")][InlineData("invalid-utf8")][InlineData("x86")]
    public void RejectsUnboundOrHostileResources(string kind)
    {
        var bytes = Image(kind == "x86" ? 0x014c : 0x8664);
        switch (kind)
        {
            case "unsigned": Put32(bytes, 0x128, 0); break;
            case "wrong-id": Put32(bytes, 0x228, 21002); break;
            case "wrong-language": Put32(bytes, 0x240, 1033); break;
            case "directory-loop": Put32(bytes, 0x214, 0x80000000); break;
            case "wrong-type": Put32(bytes, 0x210, 24); break;
            case "count-bomb": Put16(bytes, 0x20e, 65535); break;
            case "certificate-overlap": Put32(bytes, 0x128, 0x260); break;
            case "virtual-only": Put32(bytes, 0x248, 0x3000); break;
            case "size-bomb": Put32(bytes, 0x24c, 1048577); break;
            case "invalid-utf8": bytes[0x260] = 0xff; break;
        }
        using var file = Write(bytes);
        Assert.ThrowsAny<Exception>(() => SignedInstallPayload.ReadResource(file));
    }
    private static byte[] Image(int machine)
    {
        var bytes = new byte[0x408]; Put16(bytes, 0, 0x5a4d); Put32(bytes, 0x3c, 0x80); Put32(bytes, 0x80, 0x4550);
        Put16(bytes, 0x84, machine); Put16(bytes, 0x86, 1); Put16(bytes, 0x94, 240); Put16(bytes, 0x96, 0x22);
        Put16(bytes, 0x98, 0x20b); Put32(bytes, 0xb8, 0x1000); Put32(bytes, 0xbc, 0x200); Put32(bytes, 0xd0, 0x2000); Put32(bytes, 0xd4, 0x200);
        Put16(bytes, 0xdc, 3); Put32(bytes, 0x104, 16); Put32(bytes, 0x118, 0x1000); Put32(bytes, 0x11c, 0x200);
        Put32(bytes, 0x128, 0x400); Put32(bytes, 0x12c, 8); // Nonvalid certificate record, declared solely to exercise parser bounds.
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(bytes, 0x188); Put32(bytes, 0x190, 0x200); Put32(bytes, 0x194, 0x1000); Put32(bytes, 0x198, 0x200); Put32(bytes, 0x19c, 0x200);
        Put32(bytes, 0x1ac, 0x40000040);
        Put16(bytes, 0x20e, 1); Put32(bytes, 0x210, 10); Put32(bytes, 0x214, 0x80000018);
        Put16(bytes, 0x226, 1); Put32(bytes, 0x228, SignedInstallPayload.ManifestResourceId); Put32(bytes, 0x22c, 0x80000030);
        Put16(bytes, 0x23e, 1); Put32(bytes, 0x240, 0); Put32(bytes, 0x244, 0x48); Put32(bytes, 0x248, 0x1060); Put32(bytes, 0x24c, 2);
        bytes[0x260] = (byte)'{'; bytes[0x261] = (byte)'}'; return bytes;
    }
    private static FileStream Write(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "coderim-pe-parser-" + Guid.NewGuid().ToString("N") + ".fixture");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose); file.Write(bytes); file.Position = 0; return file;
    }
    private static void Put16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
