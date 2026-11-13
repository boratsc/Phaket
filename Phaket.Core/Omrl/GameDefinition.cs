namespace Phaket.Core.Omrl;

// Minimal declarative game definition for alpha validation (full schema: PAK-19).
// writableAreas are globs relative to the installation root; no engine-specific
// logic lives in Core — differences between games are data only.
public sealed record GameDefinition(string Slug, IReadOnlyList<string> WritableAreas)
{
    public bool AllowsWrite(string relativePath) =>
        WritableAreas.Any(pattern => Glob.IsMatch(pattern, relativePath));
}

public static class SourcePolicy
{
    // PoC allowlist (spec §9): concrete GitHub repositories (owner/repo) and the
    // Thunderstore API host only. Redirects beyond the allowlist are forbidden.
    private static readonly HashSet<string> GitHubRepos = new(StringComparer.OrdinalIgnoreCase);

    public static void AllowGitHubRepository(string ownerAndRepo) => GitHubRepos.Add(ownerAndRepo);

    public static bool IsAllowedHttpsSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "https") return false;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, "^/([^/]+/[^/]+)/releases/download/");
            return match.Success && GitHubRepos.Contains(match.Groups[1].Value);
        }
        return uri.Host.Equals("api.thunderstore.io", StringComparison.OrdinalIgnoreCase);
    }
}
