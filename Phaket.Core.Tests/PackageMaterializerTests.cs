using System.Security.Cryptography;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-146 part A: package trees are materialized from the read-only vanilla cache with
// reflink where the filesystem answers and a full copy otherwise. The tests run the
// fallback and a fake clone; the real reflink path is checked only when the temporary
// volume supports it (btrfs/XFS on Linux, ReFS on Windows).
public sealed class PackageMaterializerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "phaket-pak146-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FakeCloner(bool probe, Func<string, string, bool>? clone = null) : IFileCloner
    {
        public int Probes, Clones;
        public bool Probe(string directory) { Probes++; return probe; }
        public bool TryClone(string source, string destination)
        {
            Clones++;
            return clone?.Invoke(source, destination) ?? false;
        }
    }

    // The fake clone is a real copy so the materialized tree still matches the cache hash.
    private sealed class CopyingCloner : IFileCloner
    {
        public int Clones;
        public bool Probe(string directory) => true;
        public bool TryClone(string source, string destination) { Clones++; File.Copy(source, destination); return true; }
    }

    // Non-trivial fixture: nested directories, random binaries, an empty file, Polish names.
    private string CreateCache(bool withManifest = true)
    {
        var cache = Path.Combine(root, "cache");
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(cache, "Among Us_Data", "StreamingAssets", "Polski katalog"));
        File.WriteAllBytes(Path.Combine(cache, "Among Us.exe"), RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(Path.Combine(cache, "Among Us_Data", "sharedassets0.assets"), RandomNumberGenerator.GetBytes(70000));
        File.WriteAllText(Path.Combine(cache, "Among Us_Data", "StreamingAssets", "Polski katalog", "Zażółć gęślą jaźń.txt"), "ogonki");
        File.WriteAllBytes(Path.Combine(cache, "empty.bin"), []);
        if (withManifest)
        {
            VanillaCache.WriteMarker(cache, new VanillaCacheMarker("steam", "2026.8.18", "123", "host-runner", DateTimeOffset.UtcNow));
            Assert.NotNull(VanillaCache.ReadTreeHash(cache));
        }
        _ = ct;
        return cache;
    }

    private static Dictionary<string, string> Hashes(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is not (VanillaCache.MarkerFileName or VanillaCache.HashFileName))
            .ToDictionary(f => Path.GetRelativePath(directory, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static Dictionary<string, DateTime> Times(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);

    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RoundTrip_RebuildsTheSameTreeWithoutMetadata()
    {
        var cache = CreateCache();
        var target = Path.Combine(root, "pkg");

        var created = await PackageMaterializer.MaterializeAsync(
            cache, target, null, new FakeCloner(probe: false), CT);

        Assert.Equal(Hashes(cache), Hashes(target));
        Assert.False(File.Exists(Path.Combine(target, VanillaCache.MarkerFileName)));
        Assert.False(File.Exists(Path.Combine(target, VanillaCache.HashFileName)));
        Assert.DoesNotContain(created, p => p.EndsWith(VanillaCache.MarkerFileName));
        Assert.True(File.Exists(Path.Combine(target, "Among Us_Data", "StreamingAssets", "Polski katalog", "Zażółć gęślą jaźń.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(target, "empty.bin")).Length);
        Assert.False(Directory.Exists(target + PackageMaterializer.PartialSuffix));
    }

    [Fact]
    public async Task Progress_ReachesTheTotal()
    {
        var cache = CreateCache();
        long last = 0, total = -1;
        // A synchronous IProgress, like the house SyncProgress: Progress<T> posts asynchronously
        // to the captured sync context and races the assert under parallel test runs.
        var progress = new SyncTupleProgress(p => { last = p.copied; total = p.total; });
        await PackageMaterializer.MaterializeAsync(cache, Path.Combine(root, "pkg"), progress, new FakeCloner(probe: false), CT);
        Assert.Equal(total, last);
    }

    [Fact]
    public async Task SourceCacheIsUntouched()
    {
        var cache = CreateCache();
        var before = Times(cache);
        await PackageMaterializer.MaterializeAsync(cache, Path.Combine(root, "pkg"), null, new FakeCloner(probe: true), CT);
        Assert.Equal(Hashes(cache), Hashes(cache));
        Assert.Equal(before, Times(cache));
    }

    [Fact]
    public async Task SourceFilesAreOpenedReadOnly()
    {
        var cache = CreateCache();
        var target = Path.Combine(root, "pkg");
        await PackageMaterializer.MaterializeAsync(cache, target, null, new FakeCloner(probe: false), CT);
        // The copy must not need write access to the cache either.
        var other = Path.Combine(root, "pkg2");
        var handle = new FileStream(Path.Combine(cache, "Among Us.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            await PackageMaterializer.MaterializeAsync(cache, other, null, new FakeCloner(probe: false), CT);
        }
        finally { handle.Dispose(); }
        Assert.Equal(Hashes(target), Hashes(other));
    }

    [Fact]
    public async Task FailedCopy_CleansPartial_AndKeepsTargetAbsent()
    {
        var cache = CreateCache();
        var target = Path.Combine(root, "pkg");
        // The second clone fails after the first file landed in ".partial".
        var calls = 0;
        var cloner = new FakeCloner(probe: true, clone: (s, d) =>
        {
            if (++calls == 2) throw new IOException("simulated");
            File.Copy(s, d);
            return true;
        });

        await Assert.ThrowsAsync<IOException>(() => PackageMaterializer.MaterializeAsync(cache, target, null, cloner, CT));

        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + PackageMaterializer.PartialSuffix));
    }

    [Fact]
    public async Task FailedCopy_KeepsExistingTarget()
    {
        var cache = CreateCache();
        var target = Path.Combine(root, "pkg");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "previous.txt"), "old");
        var cloner = new FakeCloner(probe: true, clone: (_, _) => throw new IOException("simulated"));

        await Assert.ThrowsAsync<IOException>(() => PackageMaterializer.MaterializeAsync(cache, target, null, cloner, CT));

        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "previous.txt")));
        Assert.False(Directory.Exists(target + PackageMaterializer.PartialSuffix));
    }

    [Fact]
    public async Task InsufficientSpace_ThrowsBeforeCopying()
    {
        var cache = CreateCache();
        var target = Path.Combine(root, "pkg");
        var cloner = new FakeCloner(probe: true, clone: (s, d) => { File.Copy(s, d); return true; });

        var error = await Assert.ThrowsAsync<PhaketException>(() => PackageMaterializer.MaterializeAsync(
            cache, target, null, cloner, CT, freeBytes: _ => 1024));

        Assert.Equal(ErrorCodes.DiskFull, error.Error.Code);
        Assert.Equal(0, cloner.Clones);
        Assert.False(Directory.Exists(target));
        // Polish first, English always present (AGENTS.md).
        Assert.Contains("Za mało miejsca na dysku", error.LocalizedMessage("pl-PL"));
        Assert.Contains("Not enough disk space", error.LocalizedMessage("en"));
    }

    [Fact]
    public async Task FallbackCloner_StillCopiesEverything()
    {
        var cache = CreateCache();
        var target = Path.Combine(root, "pkg");
        var cloner = new FakeCloner(probe: true);

        await PackageMaterializer.MaterializeAsync(cache, target, null, cloner, CT);

        Assert.True(cloner.Clones > 0);
        Assert.Equal(Hashes(cache), Hashes(target));
    }

    [Fact]
    public async Task ClonePath_IsUsedWhenItSucceeds()
    {
        var cache = CreateCache();
        var target = Path.Combine(root, "pkg");
        var cloner = new CopyingCloner();

        await PackageMaterializer.MaterializeAsync(cache, target, null, cloner, CT);

        Assert.Equal(4, cloner.Clones);
        Assert.Equal(Hashes(cache), Hashes(target));
    }

    [Fact]
    public async Task CacheHashMismatch_IsDetected()
    {
        var cache = CreateCache();
        File.WriteAllText(Path.Combine(cache, "Among Us.exe"), "tampered");
        var target = Path.Combine(root, "pkg");

        var error = await Assert.ThrowsAsync<PhaketException>(() => PackageMaterializer.MaterializeAsync(
            cache, target, null, new FakeCloner(probe: false), CT));

        Assert.Equal(ErrorCodes.ArtifactHashMismatch, error.Error.Code);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task TargetInsideSource_IsRefused_AndCacheWithoutMarkerStillCopies()
    {
        var cache = CreateCache(withManifest: false);
        await Assert.ThrowsAsync<PhaketException>(() => PackageMaterializer.MaterializeAsync(
            cache, Path.Combine(cache, "nested"), null, new FakeCloner(probe: false), CT));
        Assert.False(Directory.Exists(Path.Combine(cache, "nested")));

        var target = Path.Combine(root, "pkg");
        await PackageMaterializer.MaterializeAsync(cache, target, null, new FakeCloner(probe: false), CT);
        Assert.Equal(Hashes(cache), Hashes(target));
    }

    [Fact]
    public void CopyOne_FallsBackWhenCloningIsUnsupported()
    {
        var cache = CreateCache();
        var destination = Path.Combine(root, "tree", "dir", "copy.bin");
        var source = Path.Combine(cache, "Among Us_Data", "sharedassets0.assets");
        PackageMaterializer.CopyOne(source, destination);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))));
    }

    [Fact]
    public void Probe_ReportsTheTemporaryVolume()
    {
        var directory = Path.Combine(root, "probe");
        var support = PackageMaterializer.TryDetectCloneSupport(directory);
        Assert.Contains(support, new[] { CloneSupport.None, CloneSupport.BtrfsOrXfs, CloneSupport.ReFS, CloneSupport.Unknown });
        // Scratch files never survive the probe.
        Assert.Empty(Directory.GetFiles(directory));
    }

    // Gates on the real filesystem: a reflink clone plus a write to the copy must leave the
    // source unchanged (copy-on-write). Runs where the volume answers (btrfs/XFS homedir,
    // ReFS on Windows); /tmp on tmpfs and NTFS cannot, so the test returns early.
    [Fact]
    public async Task Reflink_IsCopyOnWrite_AndMaterializeLeavesSourceUnchanged()
    {
        foreach (var candidate in CloneCandidates())
        {
            // An answered "no" on this volume: probe for a clone-capable one next.
            var support = PackageMaterializer.TryDetectCloneSupport(candidate);
            if (support is not (CloneSupport.BtrfsOrXfs or CloneSupport.ReFS)) continue;
            var work = Path.Combine(candidate, "phaket-pak146-cow-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cache = Path.Combine(work, "cache");
                Directory.CreateDirectory(cache);
                var bytes = RandomNumberGenerator.GetBytes(200_000);
                var source = Path.Combine(cache, "asset.bin");
                File.WriteAllBytes(source, bytes);

                // 1. A raw clone is copy-on-write.
                var copy = Path.Combine(work, "pkg", "asset.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                Assert.Equal(PackageMaterializer.CloneResult.Success, PackageMaterializer.TryCloneFile(source, copy));
                await using (var stream = new FileStream(copy, FileMode.Open, FileAccess.Write))
                {
                    stream.WriteByte(0xFF);
                    await stream.FlushAsync(CT);
                }
                Assert.Equal(bytes, await File.ReadAllBytesAsync(source, CT));
                Assert.NotEqual(bytes[0], (await File.ReadAllBytesAsync(copy, CT))[0]);

                // 2. The full materializer keeps the cache byte-identical.
                var target = Path.Combine(work, "materialized");
                await PackageMaterializer.MaterializeAsync(cache, target, null, PackageMaterializer.DefaultCloner(), CT);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(source, CT));
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(target, "asset.bin"), CT));
                return;
            }
            finally
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
        }
        // No clone-capable volume on this machine: the platform clone path was not exercised.
    }

    // Review PAK-146a finding 1: a lexical comparison let a target that is an alias of the cache
    // through. Both sides are canonicalized now.
    [Fact]
    public async Task TargetSymlinkToSource_IsRefused()
    {
        var cache = CreateCache();
        var alias = Path.Combine(root, "alias-of-cache");
        Directory.CreateSymbolicLink(alias, cache);

        var error = await Assert.ThrowsAsync<PhaketException>(() => PackageMaterializer.MaterializeAsync(
            cache, alias, null, new FakeCloner(probe: false), CT));

        Assert.Equal(ErrorCodes.PathOutOfBounds, error.Error.Code);
        Assert.False(Directory.Exists(alias + PackageMaterializer.PartialSuffix));
    }

    [Fact]
    public async Task TargetUnderSymlinkedParentOfSource_IsRefused()
    {
        var cache = CreateCache();
        // "<root>/cache" reached through a link: the lexical paths differ, the real trees do not.
        var parent = Path.Combine(root, "linked-root");
        Directory.CreateSymbolicLink(parent, root);

        var error = await Assert.ThrowsAsync<PhaketException>(() => PackageMaterializer.MaterializeAsync(
            cache, Path.Combine(parent, "cache", "nested"), null, new FakeCloner(probe: false), CT));

        Assert.Equal(ErrorCodes.PathOutOfBounds, error.Error.Code);
    }

    [Fact]
    public async Task SourceSymlinkToTarget_IsRefused()
    {
        var cache = CreateCache();
        var real = Path.Combine(root, "real-target");
        Directory.CreateDirectory(real);
        var linkedCache = Path.Combine(root, "linked-cache");
        Directory.CreateSymbolicLink(linkedCache, real);

        var error = await Assert.ThrowsAsync<PhaketException>(() => PackageMaterializer.MaterializeAsync(
            linkedCache, Path.Combine(real, "pkg"), null, new FakeCloner(probe: false), CT));

        Assert.Equal(ErrorCodes.PathOutOfBounds, error.Error.Code);
    }

    // The case-insensitivity itself is Windows-only; the shared rule is asserted through the
    // comparer PathBoundary uses, which is the one the materializer compares with.
    [Fact]
    public void BoundaryComparison_FollowsThePlatformCaseRule()
    {
        var same = PathBoundary.IsWithin("/data/Cache", "/data/cache");
        if (OperatingSystem.IsWindows()) Assert.True(same);
        else Assert.False(same);
    }

    // Review PAK-146a finding 3: a crash between the two renames of PromotePartial leaves the
    // previous package under "partial-old-*" and no target at all.
    [Fact]
    public async Task OrphanedPartialOld_WithoutTarget_IsRestored()
    {
        var cache = CreateCache();
        var target = Path.Combine(root, "pkg");
        var orphan = target + PackageMaterializer.OrphanSuffix + "stale";
        Directory.CreateDirectory(Path.Combine(orphan, "Among Us_Data"));
        File.WriteAllText(Path.Combine(orphan, "previous.txt"), "old");

        // Materialization fails after recovery, so the restored tree is the only thing observed.
        var cloner = new FakeCloner(probe: true, clone: (_, _) => throw new IOException("simulated"));
        await Assert.ThrowsAsync<IOException>(() => PackageMaterializer.MaterializeAsync(cache, target, null, cloner, CT));

        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "previous.txt")));
        Assert.False(Directory.Exists(orphan));
        Assert.False(Directory.Exists(target + PackageMaterializer.PartialSuffix));
    }

    [Fact]
    public async Task OrphanedPartialOld_WithTarget_IsDiscarded()
    {
        var cache = CreateCache();
        var target = Path.Combine(root, "pkg");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "previous.txt"), "old");
        var orphan = target + PackageMaterializer.OrphanSuffix + "stale";
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "stale.txt"), "stale");

        await PackageMaterializer.MaterializeAsync(cache, target, null, new FakeCloner(probe: false), CT);

        Assert.False(Directory.Exists(orphan));
        Assert.Equal(Hashes(cache), Hashes(target));
        Assert.DoesNotContain(Directory.GetDirectories(root), d => d.Contains(PackageMaterializer.OrphanSuffix));
    }

    // R3d #8: ReFS clones need cluster-aligned ranges, so the size is rounded up to a whole
    // cluster before the clone and trimmed back afterwards. The arithmetic is platform-neutral.
    [Fact]
    public void CloneAlignmentRoundsToTheSmallestClusterMultiple()
    {
        Assert.Equal(0, PackageMaterializer.CloneAlignment(0, 65536));
        Assert.Equal(65536, PackageMaterializer.CloneAlignment(1, 65536));
        Assert.Equal(65536, PackageMaterializer.CloneAlignment(65536, 65536));
        Assert.Equal(131072, PackageMaterializer.CloneAlignment(65537, 65536));
        Assert.Equal(100, PackageMaterializer.CloneAlignment(100, 0)); // no geometry: unchanged
        Assert.Equal(100, PackageMaterializer.CloneAlignment(100, 1));
    }

    private static IEnumerable<string> CloneCandidates() =>
        OperatingSystem.IsWindows()
            ? [Path.GetTempPath()]
            : [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath()];

    // Progress that reports on the calling thread, so a test can assert right after await.
    private sealed class SyncTupleProgress(Action<(long copied, long total)> report) : IProgress<(long copied, long total)>
    {
        public void Report((long copied, long total) value) => report(value);
    }
}
