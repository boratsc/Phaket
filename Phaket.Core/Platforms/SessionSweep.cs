using Phaket.Core.Execution;

namespace Phaket.Core.Platforms;

// Orphaned tool-session directories (crashed or killed runs) hold store tokens, so they must not
// pile up. The sweep deletes ONLY Phaket's own private sessions: direct children of the session
// root named with a GUID ("N" format) that are not reparse points and are older than maxAge.
// Anything else — the user's own legendary/Steam configuration, unrelated folders, links, fresh
// sessions of a running Phaket — is never touched.
internal static class SessionSweep
{
    // A session's directory timestamp only moves when its tool creates or removes an entry, so a long
    // download or a wait for the Steam Guard code leaves an active session looking days old while its
    // cached tokens are written. A file touched recently inside the session keeps it (PAK-60); only
    // file times count, because reads move a directory's write time without any write.
    private static readonly TimeSpan ActiveWriteWindow = TimeSpan.FromMinutes(30);
    // A crash-looping sweep must stay cheap: a session whose file was not reached in the budget is kept.
    private const int InspectedEntryBudget = 512;

    public static void SweepOrphans(string root, TimeSpan maxAge)
    {
        // Launch records are app state, not sessions under a tool config root: only refresh them here.
        if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(PhaketRoot()))
            ProcessTracker.SweepStore(Path.Combine(root, "processes.json"));

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var activeCutoff = DateTimeOffset.UtcNow - ActiveWriteWindow;
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
                if (HasRecentFile(child, activeCutoff)) continue;
                child.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool HasRecentFile(DirectoryInfo directory, DateTimeOffset activeCutoff)
    {
        DateTime cutoff = activeCutoff.UtcDateTime;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(directory);
        int inspected = 0;
        while (pending.Count > 0)
        {
            if (inspected++ > InspectedEntryBudget) return true;
            var current = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = current.GetFileSystemInfos(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub) { if (sub.LinkTarget is null) pending.Push(sub); continue; }
                if (entry.LinkTarget is null && entry.LastWriteTimeUtc > cutoff) return true;
            }
        }
        return false;
    }

    private static string PhaketRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket");
}
