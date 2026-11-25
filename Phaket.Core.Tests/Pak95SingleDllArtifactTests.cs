using System.Security.Cryptography;
using System.Text;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-95 follow-up: a SusModder `dll` artifact may be the bare plugin .dll instead
// of a ZIP. It must plan as a direct file.copy plus hash verify and cache as a file.
public sealed class Pak95SingleDllArtifactTests
{
    private static readonly GameDefinition AmongUs = GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private const string DllUrl = "https://susmodder-cdn.ovh/mods/levelimposter/1.2.4/LevelImposter.dll";
    private static readonly byte[] DllBytes = Encoding.UTF8.GetBytes("MZ fake plugin bytes");
    private static readonly string DllHash = Convert.ToHexStringLower(SHA256.HashData(DllBytes));

    private static Recipe Generate(string? assetName = "LevelImposter.dll", string url = DllUrl,
        string? sha256 = null) =>
        RecipeGenerator.GenerateCatalogPackage("LevelImposter", "1.2.4", "dll", url, sha256 ?? DllHash,
            DllBytes.Length, "x64", "2026-9-29", [], AmongUs, assetName).Recipe;

    private static OperationPlan Plan(Recipe recipe, ExecutorFixture fixture) =>
        OperationPlanner.Plan(recipe, new PlannerContext(
            "install-1", "profile-1", fixture.GameRoot, fixture.ProfileRoot, fixture.StagingRoot, fixture.PackageRoot,
            AmongUs, new PlanFacts("linux", "steam", "2026.9.29", OperationCatalog.Capabilities, _ => false,
                Arch: "x64")));

    [Fact]
    public void SingleDllArtifactPlansCopyIntoPluginsAndHashVerify()
    {
        var recipe = Generate();

        var artifact = Assert.Single(recipe.Artifacts);
        Assert.Equal("dll", artifact.Format);
        var step = Assert.Single(recipe.Steps);
        Assert.Equal("file.copy", step.Use);
        Assert.Equal("package://package", step.With["from"].GetString());
        Assert.Equal("game://BepInEx/plugins/LevelImposter.dll", step.With["to"].GetString());
        Assert.DoesNotContain(recipe.Steps, s => s.Use is "archive.extract" or "loader.ensure");
        var verify = Assert.Single(recipe.Verify);
        Assert.Equal("file.verify", verify.Use);
        Assert.Equal("game://BepInEx/plugins/LevelImposter.dll", verify.With["path"].GetString());
        Assert.Equal(DllHash, verify.With["sha256"].GetString());
        Assert.Contains("file.copy", recipe.Requires!.Capabilities!);
        Assert.Contains("file.verify", recipe.Requires!.Capabilities!);
        Assert.Equal("=2026.9.29", recipe.Target.Constraints?.GameVersion);
        Assert.Equal(["epic", "msstore", "steam"], recipe.Target.Editions!.Select(e => e.Store).Order(StringComparer.Ordinal));
        Assert.Empty(RecipeValidator.Validate(recipe, AmongUs));
    }

    [Fact]
    public void SingleDllIsDetectedCaseInsensitivelyFromUrlOrAssetName()
    {
        Assert.True(RecipeGenerator.IsSingleDllArtifact("dll", "https://susmodder-cdn.ovh/x/LevelImposter.DLL", null));
        Assert.True(RecipeGenerator.IsSingleDllArtifact("dll", "https://susmodder-cdn.ovh/x/download", "LevelImposter.Dll"));
        Assert.False(RecipeGenerator.IsSingleDllArtifact("full", DllUrl, "LevelImposter.dll"));
        Assert.False(RecipeGenerator.IsSingleDllArtifact("dll", "https://susmodder-cdn.ovh/x/mod.zip", null));

        var recipe = Generate(assetName: "LevelImposter.DLL", url: "https://susmodder-cdn.ovh/download/9");
        Assert.Equal("game://BepInEx/plugins/LevelImposter.DLL", recipe.Steps[0].With["to"].GetString());
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("plugins/Evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("NUL.dll")]
    [InlineData("/etc/evil.dll")]
    public void UnsafeDllAssetNameIsRefused(string assetName)
    {
        var error = Assert.Throws<PhaketException>(() => Generate(assetName: assetName));
        Assert.Equal(ErrorCodes.ModSourceInvalid, error.Error.Code);
        Assert.Contains("unsafe dll file name", error.Error.Parameters["detail"]);
    }

    [Fact]
    public void SingleDllExecutesFromPackageCacheWithoutUnzipping()
    {
        using var fixture = new ExecutorFixture();
        var recipe = Generate();
        // The host downloader stores a bare-file artifact under its artifact id.
        File.WriteAllBytes(Path.Combine(fixture.PackageRoot, "package"), DllBytes);

        var plan = Plan(recipe, fixture);
        Assert.False(plan.RequiresDownload); // the cache file is already present and hashed
        var copy = Assert.Single(plan.Operations);
        Assert.Equal("file.copy", copy.Kind);
        Assert.Equal(DllHash, copy.Sha256);
        var verify = Assert.Single(plan.Verification);
        Assert.Equal(DllHash, verify.Sha256);

        var executor = new PlanExecutor(fixture.PlanExecutionContext(recipe.Id));
        executor.Execute(plan, executor.Approve(plan), ct: TestContext.Current.CancellationToken);

        Assert.Equal(DllBytes, File.ReadAllBytes(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "LevelImposter.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "package.zip")));
    }

    [Fact]
    public async Task SingleFileArtifactDownloadsToOneCacheFileAndReusesIt()
    {
        var root = Directory.CreateTempSubdirectory("pak95-dll-download-").FullName;
        try
        {
            var recipe = Generate();
            var first = new BytesTransport(DllUrl, DllBytes);
            var downloaded = await PackageDownloader.EnsureArtifactsAsync(recipe, root, first,
                cancellationToken: TestContext.Current.CancellationToken);

            var cache = Path.Combine(root, "package");
            Assert.Equal([cache], downloaded);
            Assert.Equal(DllBytes, File.ReadAllBytes(cache));
            Assert.False(File.Exists(cache + ".zip"));

            // Second run must hit the hash-checked cache instead of the network.
            var cached = await PackageDownloader.EnsureArtifactsAsync(recipe, root, new BytesTransport(DllUrl, null),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(cached);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SingleFileArtifactHashAndSizeMismatchAreRefused()
    {
        var root = Directory.CreateTempSubdirectory("pak95-dll-reject-").FullName;
        try
        {
            var wrongHash = Generate(sha256: new string('a', 64));
            var hashError = await Assert.ThrowsAsync<PhaketException>(() =>
                PackageDownloader.EnsureArtifactsAsync(wrongHash, root, new BytesTransport(DllUrl, DllBytes),
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCodes.ArtifactHashMismatch, hashError.Error.Code);
            Assert.False(File.Exists(Path.Combine(root, "package")));

            var tooSmall = Generate();
            tooSmall = tooSmall with { Artifacts = [tooSmall.Artifacts[0] with { SizeBytes = 2 }] };
            var sizeError = await Assert.ThrowsAsync<PhaketException>(() =>
                PackageDownloader.EnsureArtifactsAsync(tooSmall, root, new BytesTransport(DllUrl, DllBytes),
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCodes.ArtifactDownloadUnavailable, sizeError.Error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ZipDllArtifactStillExtractsAndCachesAsZip()
    {
        var recipe = RecipeGenerator.GenerateCatalogPackage("Submerged", "1.0.0", "dll",
            "https://susmodder-cdn.ovh/mods/submerged/1.0.0.zip", DllHash, DllBytes.Length, "x86", "ToU",
            ["BepInEx/plugins/Submerged/Submerged.dll"], AmongUs).Recipe;
        Assert.Equal("zip", recipe.Artifacts[0].Format);
        Assert.Equal("archive.extract", Assert.Single(recipe.Steps).Use);

        var root = Directory.CreateTempSubdirectory("pak95-zip-dll-").FullName;
        try
        {
            var url = "https://susmodder-cdn.ovh/mods/submerged/1.0.0.zip";
            var downloaded = await PackageDownloader.EnsureArtifactsAsync(recipe, root,
                new BytesTransport(url, DllBytes), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal([Path.Combine(root, "package.zip")], downloaded);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class BytesTransport(string url, byte[]? bytes) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken) => source == url && bytes is not null
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new InvalidOperationException($"unexpected download: {source}");
    }
}