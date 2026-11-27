using Phaket.Core.Domain;
using Phaket.Core.Execution;

namespace Phaket.Core.Omrl;

public static class BaselineSync
{
    public static IReadOnlyList<BaselineComparison> Compare(
        IReadOnlyList<PlannedOperation> operations, InstallBaseline baseline, string gameRoot,
        Func<PlannedOperation, IReadOnlyDictionary<string, BaselineFileState>?>? treeWrites = null,
        Func<BaselineComparison, bool>? keptLocal = null,
        IReadOnlySet<string>? replacesEveryPath = null) =>
        BaselineComparison.Compare(operations, baseline, gameRoot, treeWrites, null, replacesEveryPath);

    // True when the operation can WRITE one of the decided (kept-local) paths: exactly that file,
    // or a tree write (extract/loader) whose planned writes include it. Without a write list a
    // tree write under the path is conservatively treated as touching it: a "keep mine" answer
    // must never let an archive overwrite the file the user chose to keep.
    public static bool OperationTouches(PlannedOperation operation, IReadOnlyList<string> conflictPaths,
        string gameRoot, IEnumerable<string>? plannedFiles)
    {
        var relative = BaselineComparison.RelativeToGame(operation.TargetPath, gameRoot);
        // An extract over the game root has no game-relative target of its own: it is a tree
        // write, so every conflicting path is under it and the write list is the only evidence.
        var treeOverRoot = relative is null && IsTreeWrite(operation) && IsGameRoot(operation.TargetPath, gameRoot);
        if (relative is null && !treeOverRoot) return false;
        var planned = (plannedFiles ?? []).Select(file => BaselineComparison.RelativeToGame(file, gameRoot))
            .Where(file => file is not null).ToArray();
        foreach (var path in conflictPaths)
        {
            if (!treeOverRoot)
            {
                if (string.Equals(path, relative, StringComparison.Ordinal)) return true;
                if (!path.StartsWith(relative!.TrimEnd('/') + "/", StringComparison.Ordinal)) continue;
            }
            // A planned write list is the final word; a missing one means "cannot prove it won't".
            if (plannedFiles is null || planned.Any(file => string.Equals(file, path, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private static bool IsTreeWrite(PlannedOperation operation) =>
        operation.Kind is "archive.extract" or "loader.ensure" or "game.provision";

    // The write target IS the game root (an extract over the whole tree).
    private static bool IsGameRoot(string targetPath, string gameRoot) =>
        string.Equals(System.IO.Path.GetFullPath(targetPath).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            System.IO.Path.GetFullPath(gameRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
    // A tree write (extract/loader) passes the states of the files it would leave and is
    // judged by those instead of by its staging or game-root target. An archive rewrites every
    // file it carries, so a local edit of one of them is a two-sided edit: keptLocal names the
    // paths a whole-file decision already covers, and those are not asked about again.
    public static IReadOnlyList<BaselineComparison> Compare(
        IReadOnlyList<PlannedOperation> operations, InstallBaseline baseline, string gameRoot,
        Func<PlannedOperation, IReadOnlyDictionary<string, BaselineFileState>?>? treeWrites = null,
        Func<BaselineComparison, bool>? keptLocal = null,
        IReadOnlySet<string>? replacesEveryPath = null)
    {
        var local = new Dictionary<string, BaselineFileState?>(StringComparer.Ordinal);
        var remote = new Dictionary<string, BaselineFileState?>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (treeWrites?.Invoke(operation) is { } writes)
            {
                foreach (var (path, state) in writes)
                {
                    if (!baseline.Files.ContainsKey(path)) continue;
                    local[path] = InstallBaseline.LocalState(System.IO.Path.Combine(gameRoot, path));
                    remote[path] = state;
                }
                continue;
            }
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
        // An archive replaces every entry it carries, so an edited file under it is a two-sided
        // edit even when only the user changed it: the question is the file, not the diff kind.
        return Compare(local, remote, baseline.Files)
            .Select(comparison => replacesEveryPath?.Contains(comparison.Path) == true
                    && comparison.Kind is not (BaselineChangeKind.TrueConflict or BaselineChangeKind.Unchanged
                        or BaselineChangeKind.RemoteOnly or BaselineChangeKind.IdenticalChange)
                ? comparison with { Kind = BaselineChangeKind.TrueConflict }
                : comparison)
            .ToArray();
    }

    // Game-relative path of a write target (null for profile/staging writes and for the game
    // root itself: an extract over the whole tree is a tree write, not one file).
    public static string? RelativeToGame(string targetPath, string gameRoot)
    {
        var root = System.IO.Path.GetFullPath(gameRoot);
        var full = System.IO.Path.GetFullPath(targetPath);
        // A write target that IS the game root (an extract over the whole tree) is the root,
        // not a game-relative file: an empty string would wrongly match a path prefix.
        var relative = PathBoundary.IsWithin(root, full)
            ? full[root.Length..].TrimStart(System.IO.Path.DirectorySeparatorChar).Replace('\\', '/')
            : null;
        return string.IsNullOrEmpty(relative) ? null : relative;
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