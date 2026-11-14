using Phaket.Core.Profiles;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

public class ProfileShareTests
{
    [Fact]
    public void ExportParseRoundTrip()
    {
        var json = ProfileShareService.Export("among-us", "My Profile",
            new[] { ("ModA", "1.0"), ("ModB", "2.0") });
        var package = ProfileShareService.Parse(json);
        Assert.Equal("among-us", package.GameId);
        Assert.Equal("My Profile", package.ProfileName);
        Assert.Equal(2, package.Mods.Count);
    }

    [Fact]
    public void ParseRejectsUnknownSchema()
    {
        Assert.Throws<InvalidDataException>(() => ProfileShareService.Parse("""{"schemaVersion":"profile-share/v99","mods":[]}"""));
    }

    [Fact]
    public void DiffComputesAddedRemovedChanged()
    {
        var current = new[] { ("A", "1.0"), ("B", "1.0"), ("C", "1.0") };
        var incoming = ProfileShareService.Parse(ProfileShareService.Export("g", "p",
            new[] { ("B", "2.0"), ("C", "1.0"), ("D", "1.0") }));
        var diff = ProfileShareService.Diff(current, incoming);
        Assert.Equal(["D"], diff.Added);
        Assert.Equal(["A"], diff.Removed);
        Assert.Equal(["B"], diff.Changed);
    }
}

public class CompatibilityReportStoreTests
{
    [Fact]
    public void KeepsTrustedAndUntrustedSeparate()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-compat-reports-").FullName;
        try
        {
            using var store = new CompatibilityReportStore(Path.Combine(dir, "reports.db"));
            store.Add(new CompatibilityReportRecord("1", "among-us", "2026.3", "linux", "Proton 9", "ModA", "1.0",
                CompatibilityResult.Working, ReporterTrust.TrustedTester, DateTimeOffset.UtcNow, null, "works"));
            store.Add(new CompatibilityReportRecord("2", "among-us", "2026.3", "linux", "Proton 9", "ModA", "1.0",
                CompatibilityResult.Working, ReporterTrust.Untrusted, DateTimeOffset.UtcNow, null, null));
            store.Add(new CompatibilityReportRecord("3", "among-us", "2026.3", "linux", "Proton 9", "ModB", "1.0",
                CompatibilityResult.Broken, ReporterTrust.Untrusted, DateTimeOffset.UtcNow, null, null));
            var reports = store.GetReports("among-us");
            Assert.Equal(3, reports.Count);
            var (trusted, untrusted, positive) = store.Summarize("among-us");
            Assert.Equal(1, trusted);
            Assert.Equal(2, untrusted);
            Assert.Equal(2, positive);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public class DependencyGraphTests
{
    private static DependencyGraph Build()
    {
        var nodes = new[]
        {
            new DependencyNode("loader", "BepInEx", "6.0"),
            new DependencyNode("reactor", "Reactor", "2.5"),
            new DependencyNode("roles", "Custom Roles", "1.8"),
        };
        var edges = new[]
        {
            new DependencyEdge("roles", "reactor", DependencyKind.Requires),
            new DependencyEdge("reactor", "loader", DependencyKind.Requires),
            new DependencyEdge("roles", "loader", DependencyKind.Requires),
        };
        return new DependencyGraph(nodes, edges);
    }

    [Fact]
    public void TransitiveDependenciesAreCycleSafe()
    {
        var graph = Build();
        var deps = graph.TransitiveDependencies("roles");
        Assert.Equal(["loader", "reactor"], deps);
    }

    [Fact]
    public void DetectsCycle()
    {
        var nodes = new[] { new DependencyNode("a", "A", "1"), new DependencyNode("b", "B", "1") };
        var edges = new[]
        {
            new DependencyEdge("a", "b", DependencyKind.Requires),
            new DependencyEdge("b", "a", DependencyKind.Requires),
        };
        var cycle = new DependencyGraph(nodes, edges).DetectCycle();
        Assert.NotNull(cycle);
        Assert.True(cycle!.Count >= 2);
    }

    [Fact]
    public void NoCycleInAcyclicGraph()
    {
        Assert.Null(Build().DetectCycle());
    }

    [Fact]
    public void ListsConflicts()
    {
        var nodes = new[] { new DependencyNode("a", "A", "1"), new DependencyNode("b", "B", "1") };
        var edges = new[] { new DependencyEdge("a", "b", DependencyKind.Conflicts) };
        Assert.Single(new DependencyGraph(nodes, edges).Conflicts());
    }

    [Fact]
    public void SampleGraphHasLoaderDependencies()
    {
        var graph = DependencyGraph.Sample(new[] { ("BepInEx IL2CPP", "6.0"), ("Custom Roles", "1.8"), ("Reactor", "2.5") });
        Assert.Contains("BepInEx IL2CPP", graph.TransitiveDependencies("Custom Roles"));
        Assert.Null(graph.DetectCycle());
    }
}
