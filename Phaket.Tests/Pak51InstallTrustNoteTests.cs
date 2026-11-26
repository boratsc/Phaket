using Avalonia.Headless.XUnit;
using Phaket.Core.Catalog;
using Phaket.Core.Omrl;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-51: the install preview states that a hash verifies the downloaded file, not the
// publisher identity, and the message exists in both product languages.
public class Pak51InstallTrustNoteTests
{
    private static InstallTarget Target => new("/tmp/opencode/pak51-target", "steam", true);

    private static InstallPreparation Preparation(string warning) => new("among-us", "Owner-Mod", "2.0",
        "http://127.0.0.1:1", Target,
        [new OperationPlan(OperationPlan.CurrentSchemaVersion, new RecipeRef("mod", "2"), "i", "p",
            [new PlannedOperation("op", "copy", "game://plugins/Mod.dll", null, null, null, [], "", "", "", [])],
            [], [warning], false, [])],
        [], [], [new InstallPreview("Owner-Mod", "2.0", ["plugins/Mod.dll"], 42)],
        "/tmp/opencode/pak51-sandbox", "", "", "", "");

    [AvaloniaFact]
    public async Task HashNoteShowsForVerifiedHashAndNotForUnverifiedThunderstorePackage()
    {
        var vm = new ModCatalogViewModel("among-us", "/tmp/opencode/pak51-test", autoSync: false);
        vm.PrepareOverride = (_, _, _, _, _) => Task.FromResult(Preparation("ARCH_UNKNOWN"));
        await vm.BeginInstallAsync("Owner-Mod", "2.0", Target with { Arch = "x86" });
        Assert.True(vm.HasInstallTrustNote);

        vm.PrepareOverride = (_, _, _, _, _) => Task.FromResult(Preparation("THUNDERSTORE_HASH_UNVERIFIED"));
        await vm.BeginInstallAsync("Owner-Mod", "2.0", Target with { Arch = "x86" });
        Assert.False(vm.HasInstallTrustNote);
    }

    // Reads the embedded dictionaries directly: flipping LocalizationService.CurrentLanguage
    // would notify views another headless test still owns.
    [Fact]
    public void HashIdentityNoteExistsInPolishAndEnglish()
    {
        var assembly = typeof(LocalizationService).Assembly;
        var texts = new[] { "pl-PL", "en" }.Select(language =>
        {
            using var stream = assembly.GetManifestResourceStream($"Phaket.Assets.i18n.{language}.json");
            Assert.NotNull(stream);
            using var json = System.Text.Json.JsonDocument.Parse(stream);
            return json.RootElement.TryGetProperty("ModInstall.HashIdentity", out var value) ? value.GetString() : null;
        }).ToArray();
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.NotEqual(texts[0], texts[1]);
    }

    // PAK-51 point 3: a silent install outside the allowlist stays blocked. This asserts the
    // existing Core download gate every catalog install passes through, without rebuilding it.
    [Fact]
    public async Task ArtifactOutsideTheAllowlistIsRefusedBeforeAnyWrite()
    {
        var root = Directory.CreateTempSubdirectory("pak51-allowlist-").FullName;
        var gameRoot = Path.Combine(root, "game");
        Directory.CreateDirectory(gameRoot);
        try
        {
            File.WriteAllText(Path.Combine(gameRoot, "Among Us.exe"), "fixture");
            Assert.False(SourcePolicy.IsAllowedHttpsSource("https://evil.invalid/mod.zip", new string('a', 64)));
            var recipe = new Recipe
            {
                SchemaVersion = "omrl/v0alpha1", Id = "test", Version = "1.0.0",
                Target = new RecipeTarget { Game = "among-us", Systems = ["linux"] },
                Artifacts = [new RecipeArtifact { Id = "mod", Source = "https://evil.invalid/mod.zip", Sha256 = new string('a', 64) }],
                Steps = []
            };
            var error = await Assert.ThrowsAsync<PhaketException>(() =>
                PackageDownloader.EnsureArtifactsAsync(recipe, Path.Combine(root, "packages"), new NoNetworkTransport(),
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains(error.Error.Code, new[] { ErrorCodes.ArtifactDownloadUnavailable, ErrorCodes.ValidationFailed });
            // The fixture executable is the only file: the refused download wrote nothing.
            Assert.Equal(["Among Us.exe"], Directory.EnumerateFileSystemEntries(gameRoot).Select(Path.GetFileName));
        }
        finally { Directory.Delete(root, true); }
    }

    // The refused download must never reach the network.
    private sealed class NoNetworkTransport : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the refused artifact reached the network");
    }
}
