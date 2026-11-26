namespace Phaket.Core.Catalog;

// PAK-97: a plan's installation signal from the upstream SusModder full×dll matrix.
// The status is language-independent; the host renders the message from its own resources.
public enum ModCompatibilitySignal { Warn, Block }

public sealed record ModCompatibilityFinding(
    string FullId, string DllId, string FullName, string DllName, string Status, ModCompatibilitySignal Signal);

public static class ModCompatibilityResolver
{
    // Status that blocks a plan, the one that warns it; F/W and an absent pair stay silent.
    // A pair present in only one direction (the plan has the full mod, not its dll) cannot
    // be evaluated, so it says nothing.
    public static IReadOnlyList<ModCompatibilityFinding> Check(
        IReadOnlyList<ModCompatibility> matrix, IReadOnlyList<(string Id, string Name, string Packaging)> planItems)
    {
        if (matrix.Count == 0 || planItems.Count < 2) return [];
        var fulls = planItems.Where(item => item.Packaging == "full").ToArray();
        var dlls = planItems.Where(item => item.Packaging == "dll").ToArray();
        var findings = new List<ModCompatibilityFinding>();
        foreach (var full in fulls)
            foreach (var dll in dlls)
            {
                var status = matrix.FirstOrDefault(pair => pair.FullId == full.Id && pair.DllId == dll.Id)?.Status;
                // Unknown status is not "untested": only an explicit NT or NW produces a signal.
                var signal = status switch
                {
                    "NW" => ModCompatibilitySignal.Block,
                    "NT" => ModCompatibilitySignal.Warn,
                    _ => (ModCompatibilitySignal?)null,
                };
                if (signal is { } value) findings.Add(new(full.Id, dll.Id, full.Name, dll.Name, status!, value));
            }
        return findings;
    }
}