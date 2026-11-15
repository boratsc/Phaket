namespace Phaket.Core.Platforms;

// Orphaned tool-session directories (crashed or killed runs) hold store tokens, so they must not
// pile up. The sweep deletes ONLY Phaket's own private sessions: direct children of the session
// root named with a GUID ("N" format) that are not reparse points and are older than maxAge.
// Anything else — the user's own legendary/Steam configuration, unrelated folders, links, fresh
// sessions of a running Phaket — is never touched.
internal static class SessionSweep
{
    public static void SweepOrphans(string root, TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        DirectoryInfo info;
        try { info = new DirectoryInfo(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) return;
        IEnumerable<DirectoryInfo> children;
        try { children = info.EnumerateDirectories(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var child in children)
        {
            try
            {
                if (child.LinkTarget is not null || (child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (!Guid.TryParseExact(child.Name, "N", out _)) continue;
                if (child.LastWriteTimeUtc > cutoff) continue;
                child.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
