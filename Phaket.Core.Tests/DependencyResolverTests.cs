using Phaket.Core.Catalog;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public class DependencyResolverTests
{
    private sealed class Source(params (string Name, ThunderstoreVersion[] Versions)[] entries) : IModVersionSource
    {
        private readonly Dictionary<string, ThunderstoreModDetails> values = entries.ToDictionary(x => x.Name,
            x => new ThunderstoreModDetails(null!, x.Versions, DateTimeOffset.UnixEpoch), StringComparer.OrdinalIgnoreCase);
        public Task<ThunderstoreModDetails?> GetDetailsAsync(string fullName, CancellationToken cancellationToken) =>
            Task.FromResult(values.GetValueOrDefault(fullName));
    }

    private static ThunderstoreVersion V(string name, string version, params string[] deps) =>
        new(version, $"{name}-{version}", $"https://example.test/{name}", 10, DateTimeOffset.UnixEpoch, deps);

    [Fact] public async Task ResolvesSoloAndTransitiveDependencies()
    {
        var source = new Source(("o-A", [V("o-A", "1", "o-B-1")]),
            ("o-B", [V("o-B", "1", "o-C-1")]), ("o-C", [V("o-C", "1")]));
        var result = await new DependencyResolver(source).ResolveAsync("o-A", "1");
        Assert.Equal(new[] { "o-C", "o-B", "o-A" }, result.Select(x => x.FullName));
        Assert.Single(await new DependencyResolver(new Source(("o-A", [V("o-A", "1")]))).ResolveAsync("o-A", "1"));
    }

    [Fact] public async Task ReportsMissingVersionInactiveConflictAndCycle()
    {
        async Task<string> Code(Source source, string version = "1") => (await Assert.ThrowsAsync<PhaketException>(() => new DependencyResolver(source).ResolveAsync("o-A", version))).Error.Code;
        Assert.Equal("DEPENDENCY_MISSING", await Code(new Source(("o-A", [V("o-A", "1", "o-X-1")]))));
        Assert.Equal("DEPENDENCY_VERSION_MISSING", await Code(new Source(("o-A", [V("o-A", "1")])), version: "9"));
        var inactive = new Source(("o-A", [V("o-A", "1", "o-B-1")]), ("o-B", [V("o-B", "1") with { IsActive = false }]));
        var inactiveError = await Assert.ThrowsAsync<PhaketException>(() => new DependencyResolver(inactive).ResolveAsync("o-A", "1"));
        Assert.Equal("DEPENDENCY_INACTIVE", inactiveError.Error.Code);
        Assert.Equal("DEPENDENCY_CYCLE", await Code(new Source(("o-A", [V("o-A", "1", "o-B-1")]), ("o-B", [V("o-B", "1", "o-A-1")]))));
        var conflictSource = new Source(("o-A", [V("o-A", "1", "o-B-1", "o-C-1")]),
            ("o-B", [V("o-B", "1", "o-D-1")]), ("o-C", [V("o-C", "1", "o-D-2")]),
            ("o-D", [V("o-D", "1"), V("o-D", "2")]));
        var conflict = await Assert.ThrowsAsync<PhaketException>(() => new DependencyResolver(conflictSource).ResolveAsync("o-A", "1"));
        Assert.Equal("DEPENDENCY_CONFLICT", conflict.Error.Code);
        Assert.Contains("paths", conflict.Error.Parameters.Keys);
    }

    [Fact] public async Task DependencyOrderDoesNotAffectResultAndNamesMayContainHyphens()
    {
        Source Make(params string[] deps) => new(("owner-root-pack", [V("owner-root-pack", "1", deps)]), ("owner-child-pack", [V("owner-child-pack", "2")]));
        var a = await new DependencyResolver(Make("owner-child-pack-2")).ResolveAsync("owner-root-pack", "1");
        var b = await new DependencyResolver(Make("owner-child-pack-2")).ResolveAsync("owner-root-pack", "1");
        Assert.Equal(a.Select(x => (x.FullName, x.Version, x.DownloadUrl, x.FileSize, Dependencies: string.Join(",", x.Dependencies))),
            b.Select(x => (x.FullName, x.Version, x.DownloadUrl, x.FileSize, Dependencies: string.Join(",", x.Dependencies))));
        Assert.Equal("owner-child-pack", a[0].FullName);
    }

    [Fact] public async Task ProposesEasyWorkConflictAndOverrideResolvesIt()
    {
        var source = new Source(("EasyWork-Root", [V("EasyWork-Root", "1", "denikson-BepInExPack-5.4.2350", "ebkr-Jotunn-2.30.0")]),
            ("denikson-BepInExPack", [V("denikson-BepInExPack", "5.4.2350"), V("denikson-BepInExPack", "5.4.2333")]),
            ("ebkr-Jotunn", [V("ebkr-Jotunn", "2.30.0", "denikson-BepInExPack-5.4.2333")]));
        var resolver = new DependencyResolver(source);
        var result = await resolver.ResolveWithProposalsAsync("EasyWork-Root", "1");
        Assert.Empty(result.Pinned);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("denikson-BepInExPack", conflict.Package);
        Assert.Equal(new[] { "5.4.2333", "5.4.2350" }, conflict.Versions);
        Assert.Equal("5.4.2350", conflict.SuggestedVersion);
        Assert.Contains("EasyWork-Root@1", conflict.RequestedBy);
        Assert.Contains("ebkr-Jotunn@2.30.0", conflict.RequestedBy);
        var resolved = await resolver.ResolveWithProposalsAsync("EasyWork-Root", "1", new Dictionary<string, string> { ["denikson-BepInExPack"] = "5.4.2350" });
        Assert.Empty(resolved.Conflicts);
        Assert.Equal(3, resolved.Pinned.Count);
    }

    [Fact] public async Task ProposalsAreDeterministicAndCompareNumericVersions()
    {
        var source = new Source(("o-A", [V("o-A", "1", "o-B-5.4.9", "o-C-1")]),
            ("o-B", [V("o-B", "5.4.9", "o-D-5.4.9")]), ("o-C", [V("o-C", "1", "o-D-5.4.10")]),
            ("o-D", [V("o-D", "5.4.9"), V("o-D", "5.4.10")]));
        var result = await new DependencyResolver(source).ResolveWithProposalsAsync("o-A", "1");
        Assert.Equal("5.4.10", Assert.Single(result.Conflicts).SuggestedVersion);
        var reordered = new Source(("o-A", [V("o-A", "1", "o-C-1", "o-B-5.4.9")]),
            ("o-B", [V("o-B", "5.4.9", "o-D-5.4.9")]), ("o-C", [V("o-C", "1", "o-D-5.4.10")]),
            ("o-D", [V("o-D", "5.4.10"), V("o-D", "5.4.9")]));
        var again = await new DependencyResolver(reordered).ResolveWithProposalsAsync("o-A", "1");
        Assert.Equal(result.Conflicts[0].Package, again.Conflicts[0].Package);
        Assert.Equal(result.Conflicts[0].Versions, again.Conflicts[0].Versions);
        Assert.Equal(result.Conflicts[0].RequestedBy, again.Conflicts[0].RequestedBy);
        Assert.Equal(result.Conflicts[0].SuggestedVersion, again.Conflicts[0].SuggestedVersion);
    }

    [Fact] public async Task ProposalsStillRejectInvalidGraphsAndOverrides()
    {
        async Task<string> Code(Source source) => (await Assert.ThrowsAsync<PhaketException>(() => new DependencyResolver(source).ResolveWithProposalsAsync("o-A", "1"))).Error.Code;
        Assert.Equal("DEPENDENCY_CYCLE", await Code(new Source(("o-A", [V("o-A", "1", "o-B-1")]), ("o-B", [V("o-B", "1", "o-A-1")]))));
        Assert.Equal("DEPENDENCY_MISSING", await Code(new Source(("o-A", [V("o-A", "1", "o-X-1")]))));
        Assert.Equal("DEPENDENCY_INACTIVE", await Code(new Source(("o-A", [V("o-A", "1", "o-B-1")]), ("o-B", [V("o-B", "1") with { IsActive = false }]))));
        var ex = await Assert.ThrowsAsync<PhaketException>(() => new DependencyResolver(new Source(("o-A", [V("o-A", "1")]))).ResolveWithProposalsAsync("o-A", "1", new Dictionary<string, string> { ["o-A"] = "9" }));
        Assert.Equal("DEPENDENCY_VERSION_MISSING", ex.Error.Code);
    }
}
