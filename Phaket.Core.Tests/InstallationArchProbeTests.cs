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

    public void Dispose() => Directory.Delete(root, recursive: true);
}
