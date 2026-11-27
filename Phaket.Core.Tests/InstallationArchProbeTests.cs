using System.Buffers.Binary;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

public sealed class InstallationArchProbeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "phaket-arch-" + Guid.NewGuid());
    private static readonly GameDefinition Game = new("among-us", [], MainExecutable: "Among Us.exe");

    public InstallationArchProbeTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(false, "x86")]
    [InlineData(true, "x64")]
    public void DetectsNativeExecutable(bool x64, string expected)
    {
        WritePe("Among Us.exe", x64);
        Assert.Equal(expected, InstallationArchProbe.Probe(root, Game));
    }

    [Fact]
    public void IgnoresManagedMachineAndTriesNativeFallback()
    {
        WritePe("Among Us.exe", false, managed: true);
        Assert.Null(InstallationArchProbe.Probe(root, Game));
        WritePe("GameAssembly.dll", true);
        Assert.Equal("x64", InstallationArchProbe.Probe(root, Game));
    }

    [Fact]
    public void InvalidAndMissingCandidatesAreUnknownOrUseFallback()
    {
        Assert.Null(InstallationArchProbe.Probe(root, Game));
        File.WriteAllText(Path.Combine(root, "Among Us.exe"), "not a PE");
        Assert.Null(InstallationArchProbe.Probe(root, Game));
        WritePe("UnityPlayer.dll", false);
        Assert.Equal("x86", InstallationArchProbe.Probe(root, Game));
    }

    private void WritePe(string name, bool x64, bool managed = false)
    {
        var bytes = new byte[512];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, x64 ? (ushort)0x8664 : (ushort)0x14c);
        U16(0x94, x64 ? (ushort)240 : (ushort)224);
        const int optional = 0x98;
        U16(optional, x64 ? (ushort)0x20b : (ushort)0x10b);
        U32(optional + (x64 ? 108 : 92), 16);
        if (managed)
        {
            U32(optional + (x64 ? 112 : 96) + 14 * 8, 0x2000);
            U32(optional + (x64 ? 112 : 96) + 14 * 8 + 4, 72);
        }
        File.WriteAllBytes(Path.Combine(root, name), bytes);
    }

    // R2c: hostile or broken PE input must read as "unknown", never crash the probe.
    private void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(root, name), bytes);

    private static byte[] Mutate(bool x64, Action<byte[]> change)
    {
        var bytes = new byte[512];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, x64 ? (ushort)0x8664 : (ushort)0x14c);
        U16(0x94, x64 ? (ushort)240 : (ushort)224);
        const int optional = 0x98;
        U16(optional, x64 ? (ushort)0x20b : (ushort)0x10b);
        U32(optional + (x64 ? 108 : 92), 16);
        change(bytes);
        return bytes;
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("x64")]
    public void ELfanewOutsideTheFileIsUnknown(string arch)
    {
        var x64 = arch == "x64";
        // The PE header would sit far past the end of the file.
        Write("Among Us.exe", Mutate(x64, bytes =>
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3c), 0x7fff_fff0)));
        Assert.Null(InstallationArchProbe.Probe(root, Game));
    }

    [Theory]
    [InlineData(0)]    // empty file
    [InlineData(2)]    // shorter than the DOS signature
    [InlineData(0x40)] // shorter than the DOS header
    public void ShortFileIsUnknown(int length)
    {
        Write("Among Us.exe", new byte[length]);
        Assert.Null(InstallationArchProbe.Probe(root, Game));
    }

    [Fact]
    public void TruncatedCoffHeaderIsUnknown()
    {
        // Header points to 0x80 but the file ends inside the COFF header.
        var bytes = Mutate(false, _ => { })[..0x88];
        Write("Among Us.exe", bytes);
        Assert.Null(InstallationArchProbe.Probe(root, Game));
    }

    [Fact]
    public void TruncatedOptionalHeaderIsUnknown()
    {
        // The file ends inside the optional header PEReader requires ("Image is too small").
        var bytes = Mutate(false, _ => { })[..0xc0];
        Write("Among Us.exe", bytes);
        Assert.Null(InstallationArchProbe.Probe(root, Game));
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("x64")]
    public void AbsurdSectionTableIsUnknown(string arch)
    {
        var x64 = arch == "x64";
        // PEReader validates bounds and reports zero sections; the probe must not crash and must
        // not invent a third architecture from a broken table.
        Write("Among Us.exe", Mutate(x64, bytes =>
        {
            const int coff = 0x80 + 4;
            const int optional = 0x98;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(coff + 16), 0xffff);          // SizeOfOptionalHeader
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(optional + (x64 ? 108 : 92)), 0xffff); // NumberOfSections
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optional + (x64 ? 132 : 116)), 0xffff_ff00); // FileAlignment
        }));
        // Parser does not throw: either the validated machine+magic are accepted, or it is unknown.
        var probed = InstallationArchProbe.Probe(root, Game);
        Assert.True(probed is null || probed == arch);
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("x64")]
    public void BrokenClrDirectoryIsUnknown(string arch)
    {
        var x64 = arch == "x64";
        // CLR directory points far outside the file: the probe must not throw. It reads as
        // managed (skipped, unknown) or, at worst, not as the wrong architecture.
        Write("Among Us.exe", Mutate(x64, bytes =>
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + (x64 ? 112 : 96) + 14 * 8), 0xffff_ff00)));
        Assert.Null(InstallationArchProbe.Probe(root, Game));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
