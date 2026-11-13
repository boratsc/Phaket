namespace Phaket.Core.Profiles;

// Dependency graph model (spec §8.6): normative requires/conflicts edges with
// transitive closure, cycle detection and conflict listing. Deterministic and
// offline; a rebuildable projection, not a second source of truth.
public sealed record DependencyNode(string Id, string Name, string Version);

public enum DependencyKind { Requires, Conflicts }

public sealed record DependencyEdge(string From, string To, DependencyKind Kind);

public sealed class DependencyGraph
{
    public IReadOnlyList<DependencyNode> Nodes { get; }
    public IReadOnlyList<DependencyEdge> Edges { get; }

    public DependencyGraph(IReadOnlyList<DependencyNode> nodes, IReadOnlyList<DependencyEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    // Transitive requires-closure of a node (excludes the node itself), cycle-safe.
    public IReadOnlyList<string> TransitiveDependencies(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var edge in Edges.Where(e => e.From == current && e.Kind == DependencyKind.Requires))
                if (seen.Add(edge.To)) stack.Push(edge.To);
        }
        seen.Remove(id);
        return seen.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    // Detects cycles in the requires graph (returns one cycle as a node path, or null).
    public IReadOnlyList<string>? DetectCycle()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unvisited,1=visiting,2=done
        var path = new List<string>();
        foreach (var node in Nodes)
        {
            var cycle = Visit(node.Id, state, path);
            if (cycle is not null) return cycle;
        }
        return null;
    }

    private IReadOnlyList<string>? Visit(string id, Dictionary<string, int> state, List<string> path)
    {
        if (state.TryGetValue(id, out var s))
        {
            if (s == 1)
            {
                var idx = path.IndexOf(id);
                return path.Skip(idx).Append(id).ToArray();
            }
            return null;
        }
        state[id] = 1;
        path.Add(id);
        foreach (var edge in Edges.Where(e => e.From == id && e.Kind == DependencyKind.Requires))
        {
            var cycle = Visit(edge.To, state, path);
            if (cycle is not null) return cycle;
        }
        path.RemoveAt(path.Count - 1);
        state[id] = 2;
        return null;
    }

    public IReadOnlyList<DependencyEdge> Conflicts() =>
        Edges.Where(e => e.Kind == DependencyKind.Conflicts).ToArray();

    // Sample graph for the PoC demo profiles (clearly demo data).
    public static DependencyGraph Sample(IEnumerable<(string Name, string Version)> mods)
    {
        var nodes = mods.Select(m => new DependencyNode(m.Name, m.Name, m.Version)).ToArray();
        var names = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.Ordinal);
        var edges = new List<DependencyEdge>();
        void Require(string from, string to)
        {
            if (names.Contains(from) && names.Contains(to)) edges.Add(new DependencyEdge(from, to, DependencyKind.Requires));
        }
        // BepInEx is the loader everything depends on.
        var loader = names.Contains("BepInEx IL2CPP") ? "BepInEx IL2CPP" : names.Contains("BepInEx") ? "BepInEx" : null;
        if (loader is not null)
            foreach (var n in nodes.Where(n => n.Id != loader)) Require(n.Id, loader);
        Require("Custom Roles", "Reactor");
        Require("Szyfrowy Mod", "Reactor");
        if (names.Contains("PlantEverything") && names.Contains("CraftFromContainers"))
            edges.Add(new DependencyEdge("PlantEverything", "CraftFromContainers", DependencyKind.Conflicts));
        return new DependencyGraph(nodes, edges);
    }
}
