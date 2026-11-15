using System.Reflection.PortableExecutable;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public class GeneratedLoaderArchTests
{
    private static GeneratedRecipe Generate(string game, string? arch) => RecipeGenerator.Generate(
        "BepInEx-BepInExPack_Test", "1.0.0", "https://thunderstore.io/package/download/Test/Test/1.0.0/",
        new string('0', 64), [], GameDefinitionLoader.LoadYaml(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "definitions/games", game + ".yaml"))), arch);

    [Theory]
    [InlineData("among-us", null, "x86", "88bbefc79b4f30fe42e10dadb9f536dbe669532b8d4b9e3b08239d88146e1564")]
    [InlineData("among-us", "x86", "x86", "88bbefc79b4f30fe42e10dadb9f536dbe669532b8d4b9e3b08239d88146e1564")]
    [InlineData("among-us", "x64", "x64", "5b63d7d0e92a2448ccd13b738ff95ff1cc1026c6d718555d4df8e6c824953ac4")]
    [InlineData("valheim", "x64", "x64", "6bca77b1016acfe0ffaebb6534c84b12ae8d12e5a96abbf59ec6e4526204fc6f")]
    public void PinnedLoader(string game, string? arch, string expectedArch, string hash)
    {
        var artifact = Assert.Single(Generate(game, arch).Recipe.Artifacts);
        Assert.Equal(expectedArch, artifact.Arch);
        Assert.Equal(hash, artifact.Sha256);
    }

    [Fact]
    public void ValheimX86IsRejected() => Assert.Equal("LOADER_ARCH_UNAVAILABLE",
        Assert.Throws<PhaketException>(() => Generate("valheim", "x86")).Error.Code);

    [Fact]
    public void RealX86LoaderExecutesInIsolation()
    {
        const string zip = "/tmp/opencode/phaket-research/artifacts/amongus-loader-6.0.700.zip";
        if (!File.Exists(zip)) Assert.Skip("Real loader archive unavailable.");
        var root = Directory.CreateTempSubdirectory("loader-x86-").FullName;
        try
        {
            var game = Path.Combine(root, "game"); var profile = Path.Combine(root, "profile");
            var staging = Path.Combine(root, "staging"); var packages = Path.Combine(root, "packages");
            foreach (var dir in new[] { game, profile, staging, packages }) Directory.CreateDirectory(dir);
            File.Copy(zip, Path.Combine(packages, "package.zip"));
            var recipe = Generate("among-us", "x86").Recipe;
            var definition = GameDefinitionLoader.LoadYaml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions/games/among-us.yaml")));
            var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, definition,
                new PlanFacts("linux", "steam", null, OperationCatalog.Capabilities, _ => false, Arch: "x86")));
            var executor = new PlanExecutor(new PlanExecutionContext(game, profile, staging, packages, definition, recipe.Id, "p", Path.Combine(root, "state")));
            Assert.Equal(JournalState.Completed, new InstallOrchestrator(executor).Run(plan, cancellationToken: TestContext.Current.CancellationToken).State);
            Assert.True(File.Exists(Path.Combine(game, "BepInEx/core/BepInEx.Unity.IL2CPP.dll")));
            Assert.True(File.Exists(Path.Combine(game, "dotnet/coreclr.dll")));
            using var pe = new PEReader(File.OpenRead(Path.Combine(game, "winhttp.dll")));
            Assert.Equal(Machine.I386, pe.PEHeaders.CoffHeader.Machine);
        }
        finally { Directory.Delete(root, true); }
    }
}
