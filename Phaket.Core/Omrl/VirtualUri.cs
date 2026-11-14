using System.Text.RegularExpressions;

namespace Phaket.Core.Omrl;

// Virtual URI rules (spec §3). String-level validation happens at parse/plan time;
// filesystem boundary checks (incl. symlinks) happen again before every write.
public static class VirtualUri
{
    public static readonly IReadOnlySet<string> Schemes =
        new HashSet<string>(StringComparer.Ordinal) { "game", "profile", "staging", "package" };

    private static readonly Regex SchemePattern = new(@"^([a-z][a-z0-9+.\-]*)://", RegexOptions.Compiled);

    public static bool TryParse(string? text, out string scheme, out string path)
    {
        scheme = "";
        path = "";
        if (string.IsNullOrEmpty(text) || text.Length > 4096) return false;
        var match = SchemePattern.Match(text);
        if (!match.Success || !Schemes.Contains(match.Groups[1].Value)) return false;
        var rest = text[match.Length..];
        if (rest.Length == 0) return false;
        if (rest.Any(c => c is '\\' or ':' || char.IsControl(c))) return false;
        // "." addresses the root of the scheme (e.g. game://. for the game
        // directory). Only the bare sentinel is allowed, never as a segment.
        if (rest == ".")
        {
            scheme = match.Groups[1].Value;
            path = ".";
            return true;
        }
        var segments = rest.Split('/');
        if (segments.Length > 64) return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..") return false;
        }
        scheme = match.Groups[1].Value;
        path = rest;
        return true;
    }

    public static (string Scheme, string Path) Parse(string text)
    {
        if (!TryParse(text, out var scheme, out var path))
            throw new PhaketException(ErrorCodes.InvalidUri, ("uri", text));
        return (scheme, path);
    }
}

public static class PathBoundary
{
    // Resolves a relative path under root, rejecting escapes — also via symlinks.
    public static string Resolve(string root, string relativePath)
    {
        var rootFull = Normalize(root);
        var combined = Normalize(Path.Combine(rootFull, relativePath));
        var rootReal = ResolveLinks(rootFull);
        var targetReal = ResolveLinks(combined);
        if (!IsWithin(rootFull, combined) || !IsWithin(rootReal, targetReal))
            throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", relativePath));
        return targetReal;
    }

    public static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(root, path, comparison) || path.StartsWith(rootWithSeparator, comparison);
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    // Walks existing components and resolves symlinks/junctions to their final targets.
    private static string ResolveLinks(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var current = root;
        var rest = path[root.Length..];
        foreach (var segment in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var target = ResolveOnce(current);
            if (target is not null)
            {
                current = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, target));
            }
        }
        return current;
    }

    private static string? ResolveOnce(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (File.Exists(path))
                return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}

// Minimal glob for writableAreas: '**' spans segments, '*' stays within one segment.
public static class Glob
{
    public static bool IsMatch(string pattern, string path)
    {
        var patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Match(patternSegments, 0, pathSegments, 0);
    }

    private static bool Match(string[] pattern, int p, string[] path, int s)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == "**")
            {
                if (p == pattern.Length - 1) return true;
                for (var skip = s; skip <= path.Length; skip++)
                    if (Match(pattern, p + 1, path, skip)) return true;
                return false;
            }
            if (s >= path.Length || !SegmentMatch(pattern[p], path[s])) return false;
            p++;
            s++;
        }
        return s == path.Length;
    }

    private static bool SegmentMatch(string pattern, string segment)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", "[^/]*") + "$";
        return Regex.IsMatch(segment, regex, RegexOptions.CultureInvariant);
    }
}
