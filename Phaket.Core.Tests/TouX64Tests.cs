using System.Reflection.PortableExecutable;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Recipes;

namespace Phaket.Core.Tests;

public class TouX64Tests
{
    [Fact]
    public void GatedPlanAndRealArchiveExecuteInIsolation()
    {
        var root = Directory.CreateTempSubdirectory("phaket-tou-x64-").FullName;
        try
        {
            var recipe = RecipeParser.ParseYaml(File.ReadAllText(Path.Combine(RecipeLibrary.Directory, "among-us-tou-mira-1.7.9-beta1-x64.yaml")));
            var old = RecipeParser.ParseYaml(File.ReadAllText(Path.Combine(RecipeLibrary.Directory, "among-us-tou-mira-1.7.3.yaml")));
            var def = GameDefinitionLoader.LoadYaml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions/games/among-us.yaml")));
            var game = Path.Combine(root, "game"); var profile = Path.Combine(root, "profile");
            var staging = Path.Combine(root, "staging"); var packages = Path.Combine(root, "packages");
            foreach (var dir in new[] {game, profile, staging, packages}) Directory.CreateDirectory(dir);
            var zip = "/tmp/opencode/phaket-research/artifacts/tou-mira-1.7.9-beta1-x64.zip";
            if (!File.Exists(zip)) Assert.Skip("Real x64 artifact is not available locally.");
            File.Copy(zip, Path.Combine(packages, "tou-mira-x64.zip"));
            OperationPlan Plan(Recipe r, string arch, long? build) => OperationPlanner.Plan(r,
                new PlannerContext("i", "p", game, profile, staging, packages, def,
                    new PlanFacts("linux", "steam", "2026.8.18", OperationCatalog.Capabilities, _ => false, Arch: arch, BuildId: build)));
            var incompatible = Plan(recipe, "x86", 25134893);
            Assert.Empty(incompatible.Operations);
            Assert.Empty(Plan(recipe, "x64", 24302054).Operations);
            // PAK-65: a missing buildId fact is an explicit refusal, not a silent empty plan.
            Assert.Equal(ErrorCodes.InstallFactsMissing, Assert.Throws<PhaketException>(
                () => Plan(recipe, "x64", null)).Error.Code);
            Assert.Empty(Plan(old, "x64", 24302054).Operations);
            var plan = Plan(recipe, "x64", 25134893);
            Assert.NotEmpty(plan.Operations);
            Assert.Contains(plan.Operations, x => x.TargetPath == Path.Combine(game, "winhttp.dll"));
            Assert.Contains(plan.Operations, x => x.TargetPath == Path.Combine(game, "BepInEx/plugins/TownOfUsMira.dll"));
            var executor = new PlanExecutor(new PlanExecutionContext(game, profile, staging, packages, def, recipe.Id, "p", Path.Combine(root, "state")));
            Assert.Throws<PhaketException>(() => executor.Approve(incompatible));
            var journal = new InstallOrchestrator(executor).Run(plan, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(JournalState.Completed, journal.State);
            Assert.True(executor.VerifyPlan(plan).Succeeded);
            using var pe = new PEReader(File.OpenRead(Path.Combine(game, "winhttp.dll")));
            Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
            Assert.True(File.Exists(Path.Combine(game, "dotnet", "coreclr.dll")));
        }
        finally { Directory.Delete(root, true); }
    }
}
