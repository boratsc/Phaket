using Avalonia.Headless.XUnit;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// R2c: the planner now emits ARCH_UNKNOWN for any step consuming an arch-marked artifact.
// The plan preview must say so, from the real plan's warnings (not a hand-fed string).
public sealed class Pak_R2c_ArchUnknownPreviewTests
{
    private static readonly InstallTarget Target = new("", "steam", true);

    private static InstallPreparation Preparation(params string[] warnings) => new(
        "among-us", "Owner-Mod", "2.0", "http://localhost", Target,
        [new OperationPlan(OperationPlan.CurrentSchemaVersion, new RecipeRef("mod", "2"),
            "i", "p",
            [new PlannedOperation("extract-mod", "archive.extract", "/tmp/x/custom-roles", "package://custom-roles.zip",
                null, null, [], "", "", "", [])],
            [], warnings, false, [])],
        [], [], [new InstallPreview("Owner-Mod", "2.0", ["plugins/Mod.dll"], 42)],
        "/tmp/sandbox", "", "", "", "");

    [AvaloniaFact]
    public async Task ArchUnknownWarningFromThePlanIsShownLocalized()
    {
        var vm = new ModCatalogViewModel("among-us", "/tmp/opencode/ui4-test", autoSync: false);
        // The exact warning list the planner produces for an unknown installation arch.
        vm.PrepareOverride = (_, _, _, _, _) => Task.FromResult(Preparation("ARCH_UNKNOWN"));

        await vm.BeginInstallAsync("Owner-Mod", "2.0", Target);

        Assert.Equal([LocalizationService.Instance["ModInstall.WarningArchUnknown"]], vm.PlanWarnings);
        Assert.True(vm.HasPreview);
    }
}