using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-142: last agreed state per installation and its three-way file comparison.
public sealed class InstallBaselineTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-baseline-").FullName;
    private static readonly DateTimeOffset When = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static BaselineFileState State(char digit, long size = 10) => new(new string(digit, 64), When, size);

    private static IReadOnlyDictionary<string, BaselineFileState?> Snapshot(
        params (string Path, BaselineFileState? State)[] files) =>
        files.ToDictionary(f => f.Path, f => f.State, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, BaselineFileState> Baseline(params (string Path, BaselineFileState State)[] files) =>
        files.ToDictionary(f => f.Path, f => f.State, StringComparer.Ordinal);

    private static BaselineChangeKind KindOf(IReadOnlyList<BaselineComparison> comparisons, string path) =>
        comparisons.Single(c => c.Path == path).Kind;

    [Fact]
    public void ComparesEveryCaseOfTheSixStateMatrix()
    {
        var baseline = Baseline(("a.dll", State('1')), ("b.dll", State('1')), ("c.dll", State('1')),
            ("d.dll", State('1')), ("e.dll", State('1')), ("f.dll", State('1')));
        // f: the recipe keeps its own bytes while the installation was edited.
        var local = Snapshot(("a.dll", State('1')), ("b.dll", State('2')), ("c.dll", State('1')),
            ("d.dll", State('2')), ("e.dll", State('3')), ("f.dll", State('4')));
        var remote = Snapshot(("a.dll", State('1')), ("b.dll", State('1')), ("c.dll", State('2')),
            ("d.dll", State('3')), ("e.dll", State('3')), ("f.dll", State('1')));

        var comparisons = BaselineComparison.Compare(local, remote, baseline);

        Assert.Equal(["a.dll", "b.dll", "c.dll", "d.dll", "e.dll", "f.dll"], comparisons.Select(c => c.Path));
        Assert.Equal(BaselineChangeKind.Unchanged, KindOf(comparisons, "a.dll"));
        Assert.Equal(BaselineChangeKind.OneSidedChange, KindOf(comparisons, "b.dll"));
        Assert.Equal(BaselineChangeKind.RemoteOnly, KindOf(comparisons, "c.dll"));
        Assert.Equal(BaselineChangeKind.TrueConflict, KindOf(comparisons, "d.dll"));
        Assert.Equal(BaselineChangeKind.IdenticalChange, KindOf(comparisons, "e.dll"));
        Assert.Equal(BaselineChangeKind.OneSidedChange, KindOf(comparisons, "f.dll"));
    }

    [Fact]
    public void OneSidedMergeKeepsTheChangedSideAndOnlyConflictsNeedTheUser()
    {
        var baseline = Baseline(("b.dll", State('1')), ("d.dll", State('1')));
        var comparisons = BaselineComparison.Compare(
            Snapshot(("b.dll", State('2')), ("d.dll", State('2')), ("local.txt", State('7'))),
            Snapshot(("b.dll", State('1')), ("d.dll", State('3')), ("remote.txt", State('8'))),
            baseline);

        var localEdit = comparisons.Single(c => c.Path == "b.dll");
        Assert.Equal(BaselineChangeKind.OneSidedChange, localEdit.Kind);
        Assert.Equal(State('2'), localEdit.Merged);
        Assert.False(localEdit.RequiresUserDecision);
        Assert.Equal(BaselineChangeKind.LocalOnly, KindOf(comparisons, "local.txt"));
        Assert.Equal(State('7'), comparisons.Single(c => c.Path == "local.txt").Merged);
        Assert.Equal(BaselineChangeKind.RemoteOnly, KindOf(comparisons, "remote.txt"));
        Assert.Equal(BaselineChangeKind.TrueConflict, KindOf(comparisons, "d.dll"));
        Assert.True(comparisons.Single(c => c.Path == "d.dll").RequiresUserDecision);
        Assert.Null(comparisons.Single(c => c.Path == "d.dll").Merged);
    }

    [Fact]
    public void DeletionOnBothSidesIsNotAConflictAndADeletionAgainstAnEditIs()
    {
        var baseline = Baseline(("gone.dll", State('1')), ("edit-vs-delete.dll", State('1')));
        var comparisons = BaselineComparison.Compare(
            Snapshot(("gone.dll", null), ("edit-vs-delete.dll", State('2'))),
            Snapshot(("gone.dll", null), ("edit-vs-delete.dll", null)),
            baseline);
        // Both sides deleted the file: the result matches the baseline (absent), no question.
        Assert.Equal(BaselineChangeKind.IdenticalChange, KindOf(comparisons, "gone.dll"));
        Assert.Equal(BaselineChangeKind.TrueConflict, KindOf(comparisons, "edit-vs-delete.dll"));
    }

    [Fact]
    public void RoundTripKeepsStateHashAndDetectsAnEditedBaseline()
    {
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(root, "BepInEx", "plugins", "Mod.dll"), "mod");
        File.WriteAllText(Path.Combine(root, "winhttp.dll"), "loader");
        var captured = InstallBaseline.Capture("install-1", root);
        Assert.Equal(2, captured.Files.Count);
        Assert.Equal("BepInEx/plugins/Mod.dll", captured.Files.Keys.First());

        var path = Path.Combine(root, "state", "baseline.json");
        captured.Save(path);
        var loaded = InstallBaseline.Load(path, "install-1");
        Assert.True(InstallBaseline.SameEntries(captured, loaded));
        Assert.Equal(captured.StateHash, loaded.StateHash);
        Assert.False(File.Exists(path + ".tmp"));

        var tampered = File.ReadAllText(path).Replace(InstallBaseline.ReadFile(
            Path.Combine(root, "winhttp.dll")).Sha256[..12], new string('0', 12), StringComparison.Ordinal);
        File.WriteAllText(path, tampered);
        var ex = Assert.Throws<PhaketException>(() => InstallBaseline.Load(path, "install-1"));
        Assert.Equal(ErrorCodes.ValidationFailed, ex.Error.Code);
        Assert.Contains("stateHash", ex.Error.Parameters["detail"]);
    }

    [Fact]
    public void LoadRefusesAnotherInstallationAndUnknownSchema()
    {
        var path = Path.Combine(root, "baseline.json");
        InstallBaseline.Create("install-1", Baseline(("a.dll", State('1')))).Save(path);

        var foreign = Assert.Throws<PhaketException>(() => InstallBaseline.Load(path, "install-2"));
        Assert.Contains("install-1", foreign.Error.Parameters["detail"]);

        File.WriteAllText(path, File.ReadAllText(path).Replace(InstallBaseline.CurrentSchemaVersion, "install-baseline/v9"));
        var schema = Assert.Throws<PhaketException>(() => InstallBaseline.Load(path));
        Assert.Equal(ErrorCodes.UnsupportedSchemaVersion, schema.Error.Code);
    }

    [Fact]
    public void PathsOutsideTheGameTreeAreRefused()
    {
        foreach (var bad in new[] { "../escape.dll", "/abs.dll", "C:/game.dll", "dir//x.dll", "a/./b.dll" })
        {
            var ex = Assert.Throws<PhaketException>(
                () => InstallBaseline.Create("install-1", Baseline((bad, State('1')))));
            Assert.Equal(ErrorCodes.ValidationFailed, ex.Error.Code);
        }
    }

    [Fact]
    public void LocalStateReadsTheFileThatIsOnDisk()
    {
        var file = Path.Combine(root, "value.txt");
        File.WriteAllText(file, "hello");
        var state = InstallBaseline.LocalState(file)!;
        Assert.Equal(5, state.Size);
        Assert.Null(InstallBaseline.LocalState(Path.Combine(root, "missing.txt")));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}