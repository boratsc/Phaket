using Phaket.Core.Catalog;
using Phaket.Core.Omrl;

namespace Phaket.Core.Mods;

public interface IModVersionSource
{
    Task<ThunderstoreModDetails?> GetDetailsAsync(string fullName, CancellationToken cancellationToken);
}

public sealed record PinnedMod(string FullName, string Version, string DownloadUrl, long FileSize,
    IReadOnlyList<string> Dependencies);

public sealed record ResolutionResult(IReadOnlyList<PinnedMod> Pinned, IReadOnlyList<ConflictProposal> Conflicts);
public sealed record ConflictProposal(string Package, IReadOnlyList<string> Versions,
    IReadOnlyList<string> RequestedBy, string SuggestedVersion);

public sealed class DependencyResolver(IModVersionSource source)
{
    public async Task<ResolutionResult> ResolveWithProposalsAsync(string rootFullName, string rootVersion,
        IReadOnlyDictionary<string, string>? versionOverrides = null, CancellationToken ct = default)
    {
        var selected = new Dictionary<string, ThunderstoreVersion>(StringComparer.OrdinalIgnoreCase);
        var requests = new Dictionary<string, SortedDictionary<string, SortedSet<string>>>(StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        var ordered = new List<PinnedMod>();

        async Task Visit(string name, string requestedVersion, string requestedFrom)
        {
            ct.ThrowIfCancellationRequested();
            var version = versionOverrides?.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value ?? requestedVersion;
            if (!requests.TryGetValue(name, out var byVersion))
                requests[name] = byVersion = new(StringComparer.Ordinal);
            if (!byVersion.TryGetValue(requestedVersion, out var paths)) byVersion[requestedVersion] = paths = new(StringComparer.Ordinal);
            paths.Add(requestedFrom);

            if (selected.TryGetValue(name, out var previous))
            {
                if (!string.Equals(previous.VersionNumber, version, StringComparison.Ordinal)) return;
                if (states.GetValueOrDefault(name) == 1)
                    throw new PhaketException("DEPENDENCY_CYCLE", ("cycle", string.Join("->", stack.Skip(stack.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))).Append(name))));
                return;
            }

            var details = await source.GetDetailsAsync(name, ct);
            if (details is null) throw new PhaketException("DEPENDENCY_MISSING", ("package", name));
            var match = details.Versions.FirstOrDefault(v => string.Equals(v.VersionNumber, version, StringComparison.Ordinal));
            if (match is null) throw new PhaketException("DEPENDENCY_VERSION_MISSING", ("package", name), ("version", version));
            if (!match.IsActive) throw new PhaketException("DEPENDENCY_INACTIVE", ("package", name), ("version", version));
            selected[name] = match;
            states[name] = 1;
            stack.Add(name);
            var dependencies = match.Dependencies.Select(ParseDependency).OrderBy(d => d.FullName, StringComparer.Ordinal).ThenBy(d => d.Version, StringComparer.Ordinal).ToArray();
            foreach (var dependency in dependencies) await Visit(dependency.FullName, dependency.Version, $"{name}@{version}");
            stack.RemoveAt(stack.Count - 1);
            states[name] = 2;
            ordered.Add(new(name, match.VersionNumber, match.DownloadUrl, match.FileSize, dependencies.Select(d => $"{d.FullName}-{d.Version}").ToArray()));
        }

        await Visit(rootFullName, rootVersion, "ROOT");
        var conflicts = requests.Where(x => x.Value.Count > 1 &&
                (versionOverrides is null || !versionOverrides.Keys.Any(k => string.Equals(k, x.Key, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new ConflictProposal(x.Key, x.Value.Keys.ToArray(), x.Value.Values.SelectMany(p => p).Distinct().Order(StringComparer.Ordinal).ToArray(),
                x.Value.Keys.Order(Comparer<string>.Create(CompareVersions)).Last()))
            .ToArray();
        return conflicts.Length > 0 ? new([], conflicts) : new(ordered, []);
    }

    private static int CompareVersions(string left, string right)
    {
        var a = left.Split('.'); var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (int.TryParse(a[i], out var an) && int.TryParse(b[i], out var bn))
            { var comparison = an.CompareTo(bn); if (comparison != 0) return comparison; }
            else { var comparison = string.Compare(a[i], b[i], StringComparison.Ordinal); if (comparison != 0) return comparison; }
        }
        return a.Length.CompareTo(b.Length);
    }

    public async Task<IReadOnlyList<PinnedMod>> ResolveAsync(string rootFullName, string rootVersion,
        CancellationToken cancellationToken = default)
    {
        var selected = new Dictionary<string, ThunderstoreVersion>(StringComparer.OrdinalIgnoreCase);
        var requestedBy = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        var ordered = new List<PinnedMod>();

        async Task Visit(string fullName, string version, string requestedFrom)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!requestedBy.TryGetValue(fullName, out var paths)) requestedBy[fullName] = paths = new(StringComparer.Ordinal);
            paths.Add(requestedFrom);
            if (selected.TryGetValue(fullName, out var previous))
            {
                if (!string.Equals(previous.VersionNumber, version, StringComparison.Ordinal))
                    throw new PhaketException("DEPENDENCY_CONFLICT", ("package", fullName),
                        ("versions", string.Join(",", new[] { previous.VersionNumber, version }.Distinct().Order(StringComparer.Ordinal))),
                        ("paths", string.Join(";", paths)));
                if (states.GetValueOrDefault(fullName) == 1)
                {
                    var cycle = stack.Skip(stack.FindIndex(x => string.Equals(x, fullName, StringComparison.OrdinalIgnoreCase))).Append(fullName);
                    throw new PhaketException("DEPENDENCY_CYCLE", ("cycle", string.Join("->", cycle)));
                }
                return;
            }

            var details = await source.GetDetailsAsync(fullName, cancellationToken);
            if (details is null) throw new PhaketException("DEPENDENCY_MISSING", ("package", fullName));
            var match = details.Versions.FirstOrDefault(v => string.Equals(v.VersionNumber, version, StringComparison.Ordinal));
            if (match is null) throw new PhaketException("DEPENDENCY_VERSION_MISSING", ("package", fullName), ("version", version));
            if (!match.IsActive) throw new PhaketException("DEPENDENCY_INACTIVE", ("package", fullName), ("version", version));

            selected[fullName] = match;
            states[fullName] = 1;
            stack.Add(fullName);
            var dependencies = match.Dependencies.Select(ParseDependency).OrderBy(d => d.FullName, StringComparer.Ordinal).ThenBy(d => d.Version, StringComparer.Ordinal).ToArray();
            foreach (var dependency in dependencies)
                await Visit(dependency.FullName, dependency.Version, $"{fullName}@{version}");
            stack.RemoveAt(stack.Count - 1);
            states[fullName] = 2;
            ordered.Add(new(fullName, match.VersionNumber, match.DownloadUrl, match.FileSize, dependencies.Select(d => $"{d.FullName}-{d.Version}").ToArray()));
        }

        await Visit(rootFullName, rootVersion, "ROOT");
        return ordered;
    }

    private static (string FullName, string Version) ParseDependency(string value)
    {
        var versionSeparator = value.LastIndexOf('-');
        var ownerSeparator = value.IndexOf('-');
        if (ownerSeparator <= 0 || versionSeparator <= ownerSeparator + 1 || versionSeparator == value.Length - 1)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "invalid dependency identifier"));
        return (value[..versionSeparator], value[(versionSeparator + 1)..]);
    }
}
