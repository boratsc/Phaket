using System.Collections.Immutable;
using System.Threading;
using Phaket.Core.Omrl;

// SourcePolicy's GitHub allowlist is process-wide, so a probe repository registered by one test
// would otherwise be handed to a test running beside it in another collection. Here every test
// resolves against the entries it registered itself, so no test sees another's probe.
public sealed class SourcePolicyIsolation
{
    private static readonly AsyncLocal<ImmutableHashSet<string>?> Repos = new();

    static SourcePolicyIsolation() =>
        SourcePolicy.UseIsolatedGitHubRepos((Func<ImmutableHashSet<string>?, ImmutableHashSet<string>?> set) => Repos.Value = set(null));
}