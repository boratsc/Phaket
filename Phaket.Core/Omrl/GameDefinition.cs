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
    // PoC allowlist (spec §9): GitHub release assets of concrete repos + Thunderstore API.
    // Redirects beyond the allowlist are forbidden at execution time.
    public static bool IsAllowedHttpsSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "https") return false;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal);
        return uri.Host.Equals("api.thunderstore.io", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".thunderstore.io", StringComparison.OrdinalIgnoreCase);
    }
}
