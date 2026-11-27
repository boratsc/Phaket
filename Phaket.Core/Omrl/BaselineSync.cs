using Phaket.Core.Domain;
using Phaket.Core.Execution;

namespace Phaket.Core.Omrl;

public static class BaselineSync
{
    public static IReadOnlyList<BaselineComparison> Compare(
        IReadOnlyList<PlannedOperation> operations, InstallBaseline baseline, string gameRoot) =>
        BaselineComparison.Compare(operations, baseline, gameRoot);

    // True when a write OP lands on one of the decided paths. The operation is the final word:
    // an extract may write a conflicted file the comparison only saw as a directory target.
    public static bool OperationTouches(PlannedOperation operation, IReadOnlyList<string> conflictPaths,
        string gameRoot, IEnumerable<string>? plannedFiles)
    {
        var relative = BaselineComparison.RelativeToGame(operation.TargetPath, gameRoot);
        if (relative is null) return false;
        foreach (var path in conflictPaths)
        {
            if (string.Equals(path, relative, StringComparison.Ordinal)) return true;
            // A tree write (extract/provision/loader) is conflict-free only when it cannot
            // produce the conflicted file.
            if (path.StartsWith(relative.TrimEnd('/') + "/", StringComparison.Ordinal)
                && !(plannedFiles ?? []).Any(file => string.Equals(BaselineComparison.RelativeToGame(file, gameRoot), path, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }
}

public enum BaselineChangeKind
{
    Unchanged,
    LocalOnly,
    RemoteOnly,
    OneSidedChange,
    IdenticalChange,
    TrueConflict,
}

// One game-relative path seen from three sides: the agreed baseline, the installation
// as it is now, and what the new recipe would leave. Null means the file is absent.
public sealed record BaselineComparison(
    string Path, BaselineChangeKind Kind,
    BaselineFileState? Base, BaselineFileState? Local, BaselineFileState? Remote)
{
    public bool RequiresUserDecision => Kind is BaselineChangeKind.TrueConflict;

    // Only a two-sided edit needs the user; every one-sided edit merges without a question.
    public BaselineFileState? Merged => Kind switch
    {
        BaselineChangeKind.Unchanged or BaselineChangeKind.LocalOnly or BaselineChangeKind.IdenticalChange => Local,
        BaselineChangeKind.RemoteOnly => Remote,
        BaselineChangeKind.OneSidedChange => Same(Local, Base) ? Remote : Local,
        _ => null,
    };

    // True when the merge keeps the local bytes, so the executor has nothing to write.
    public bool LeavesLocalUnchanged => Same(Merged, Local);

    internal static bool Same(BaselineFileState? left, BaselineFileState? right) =>
        left is null ? right is null
        : right is not null && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    // Three-way comparison. Absent is a real state, so a deletion on one side is a
    // one-sided change and both sides reaching the same bytes is not a conflict.
    public static IReadOnlyList<BaselineComparison> Compare(
        IReadOnlyDictionary<string, BaselineFileState?> local,
        IReadOnlyDictionary<string, BaselineFileState?> remote,
        IReadOnlyDictionary<string, BaselineFileState> baseline)
    {
        var locals = InstallBaseline.NormalizeSnapshot(local);
        var remotes = InstallBaseline.NormalizeSnapshot(remote);
        var bases = InstallBaseline.NormalizeSnapshot(
            baseline.ToDictionary(kv => kv.Key, kv => (BaselineFileState?)kv.Value));
        var paths = bases.Keys.Union(locals.Keys, StringComparer.Ordinal)
            .Union(remotes.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal);
        return paths.Select(path => new BaselineComparison(path,
            Classify(bases.GetValueOrDefault(path), locals.GetValueOrDefault(path), remotes.GetValueOrDefault(path)),
            bases.GetValueOrDefault(path), locals.GetValueOrDefault(path), remotes.GetValueOrDefault(path))).ToArray();
    }

    // Compares the baseline entries rewritten by single-file writes with the game tree.
    // A configured key lives in a shared file the ledger owns, so it is not judged here.
    public static IReadOnlyList<BaselineComparison> Compare(
        IReadOnlyList<PlannedOperation> operations, InstallBaseline baseline, string gameRoot)
    {
        var local = new Dictionary<string, BaselineFileState?>(StringComparer.Ordinal);
        var remote = new Dictionary<string, BaselineFileState?>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (operation.Kind is "archive.extract" or "loader.ensure" or "game.provision" or "config.set") continue;
            if (RelativeToGame(operation.TargetPath, gameRoot) is not { } relative) continue;
            local[relative] = InstallBaseline.LocalState(System.IO.Path.Combine(gameRoot, relative));
            // A file the recipe writes for the first time has nothing agreed to edit.
            if (!baseline.Files.ContainsKey(relative)) continue;
            // A write without a known hash cannot be compared, and guessing one would turn
            // every local edit into a false conflict: leave it to the executor's base state.
            if (operation.Sha256 is { Length: 64 } sha256)
                remote[relative] = new BaselineFileState(sha256.ToLowerInvariant(),
                    baseline.Files[relative].Mtime, operation.SizeBytes ?? baseline.Files[relative].Size);
        }
        return Compare(local, remote, baseline.Files);
    }

    // Game-relative path of a write target, or null for profile/staging writes.
    public static string? RelativeToGame(string targetPath, string gameRoot)
    {
        var root = System.IO.Path.GetFullPath(gameRoot);
        var full = System.IO.Path.GetFullPath(targetPath);
        return PathBoundary.IsWithin(root, full)
            ? full[root.Length..].TrimStart(System.IO.Path.DirectorySeparatorChar).Replace('\\', '/')
            : null;
    }

    private static BaselineChangeKind Classify(
        BaselineFileState? baseFile, BaselineFileState? localFile, BaselineFileState? remoteFile)
    {
        if (baseFile is not null)
        {
            var localChanged = !Same(localFile, baseFile);
            var remoteChanged = !Same(remoteFile, baseFile);
            if (!localChanged && !remoteChanged) return BaselineChangeKind.Unchanged;
            if (!localChanged) return BaselineChangeKind.RemoteOnly;
            if (!remoteChanged) return BaselineChangeKind.OneSidedChange;
            return Same(localFile, remoteFile) ? BaselineChangeKind.IdenticalChange : BaselineChangeKind.TrueConflict;
        }
        // Nothing agreed yet: a path only one side has is that side's creation; equal
        // bytes on both sides are one change, different bytes were never agreed.
        if (localFile is not null && remoteFile is not null)
            return Same(localFile, remoteFile) ? BaselineChangeKind.IdenticalChange : BaselineChangeKind.TrueConflict;
        return localFile is not null ? BaselineChangeKind.LocalOnly : BaselineChangeKind.RemoteOnly;
    }
}