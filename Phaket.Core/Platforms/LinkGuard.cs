using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

// Path safety for install tools that write into a user-selected game tree. Every check runs
// BEFORE the tool process starts, so a hostile or stale tree is refused rather than partially
// written; the post-run verification in the adapters stays as a second layer.
// shortcut: TOCTOU remains — an external process can add a link between this scan and the tool's
// write. Closing it needs the tool to refuse reparse points itself; re-check when legendary /
// DepotDownloader gain such an option.
internal static class LinkGuard
{
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Any link or reparse point on the way to (and including) the path itself.
    public static void RejectLinks(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new PhaketException(Omrl.ErrorCodes.PathOutOfBounds, ("path", current));
        }
    }

    // Links anywhere inside an existing target tree: even one that resolves back inside the tree
    // is refused, because the tree can be re-pointed while the tool writes into it.
    public static void RejectNestedLinks(string targetPath)
    {
        if (!Directory.Exists(targetPath)) return;
        var pending = new Stack<string>();
        pending.Push(targetPath);
        while (pending.TryPop(out var directory))
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PhaketException(Omrl.ErrorCodes.PathOutOfBounds, ("path", entry.FullName));
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
            }
    }

    public static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unable to resolve link");
        }
        return current;
    }

    public static bool IsInside(string path, string root) =>
        Resolve(path).StartsWith(Resolve(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison);
}
