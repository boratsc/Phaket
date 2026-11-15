using System.Diagnostics;
using System.Reflection.PortableExecutable;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

public static class InstallationArchProbe
{
    public static string? Probe(string gameRoot, GameDefinition definition)
    {
        var candidates = new[] { definition.MainExecutable, "GameAssembly.dll", "UnityPlayer.dll" };
        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var path = PathBoundary.Resolve(gameRoot, candidate!);
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var headers = pe.PEHeaders;
                var optional = headers.PEHeader;
                if (optional is null) throw new BadImageFormatException("Missing PE optional header");
                // Any CLR directory makes Machine unreliable for managed binaries.
                if (optional.CorHeaderTableDirectory.Size != 0 || optional.CorHeaderTableDirectory.RelativeVirtualAddress != 0)
                    continue;
                var machine = headers.CoffHeader.Machine;
                if (machine == Machine.I386 && optional.Magic == PEMagic.PE32) return "x86";
                if (machine == Machine.Amd64 && optional.Magic == PEMagic.PE32Plus) return "x64";
                Trace.TraceWarning("Unrecognized PE architecture in {0}", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or PhaketException or ArgumentException)
            {
                Trace.TraceWarning("Cannot probe PE architecture for {0}: {1}", candidate, ex.Message);
            }
        }
        return null;
    }
}
