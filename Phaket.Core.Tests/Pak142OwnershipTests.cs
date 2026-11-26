using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-142: which files an uninstall owns alone, and which it must leave to the others.
// Plan-only assertions stay independent of the executor; behaviour is checked on ledger
// fixtures so the uninstall code path itself is exercised.
public sealed class Pak142OwnershipTests : IDisposable
{
    private const string Shared = "BepInEx/plugins/Shared.dll";
    private const string Owned = "BepInEx/plugins/A.dll";
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak142-owner-").FullName;

    private static LedgerOwner Owner(string recipe, int layer, string profile = "profile-1") =>
        new(recipe, profile, recipe + "-op", layer);

    private OwnershipLedger LedgerWith(params LedgerEntry[] entries)
    {
        var ledger = new OwnershipLedger();
        foreach (var entry in entries) ledger.Set(entry);
        return ledger;
    }

    [Fact]
    public void UninstallPlanRemovesOnlyFilesTheRecipeOwnsAlone()
    {
        var ledger = LedgerWith(
            new LedgerEntry(Owned, null, null, null, false, [Owner("mod-a", 1)], "a"),
            new LedgerEntry(Shared, null, null, null, false, [Owner("mod-a", 1), Owner("mod-b", 2)], "shared"),
            new LedgerEntry("BepInEx/config/x.cfg::key", null, null, null, false, [Owner("mod-a", 1)], "cfg"),
            new LedgerEntry("BepInEx/config/y.cfg::key", null, null, null, false, [Owner("mod-b", 1)], "cfg"));

        var plan = ledger.PlanUninstall("mod-a");
        Assert.Equal(["BepInEx/config/x.cfg::key", Owned, Shared], plan.Resources.Select(r => r.Resource));

        var removed = plan.Resources.Single(r => r.Resource == Owned);
        Assert.Equal(UninstallAction.Remove, removed.Action);
        Assert.True(removed.Removable);
        Assert.Empty(removed.RemainingOwners);

        var shared = plan.Resources.Single(r => r.Resource == Shared);
        Assert.Equal(UninstallAction.Recompute, shared.Action);
        Assert.False(shared.Removable);
        Assert.Equal(["mod-b"], shared.RemainingOwners);

        // A config key is recomputed inside a file the ledger still owns, never deleted.
        Assert.Equal(UninstallAction.Recompute,
            plan.Resources.Single(r => r.Resource == "BepInEx/config/x.cfg::key").Action);
        // Another recipe's file is not even listed.
        Assert.DoesNotContain(plan.Resources, r => r.Resource == "BepInEx/config/y.cfg::key");
    }

    [Fact]
    public void OnlyTheSoleOwnerRemovesTheFileAndTheOthersKeepIt()
    {
        var ledger = LedgerWith(
            new LedgerEntry(Owned, null, null, null, false, [Owner("mod-a", 1)], "a"),
            new LedgerEntry(Shared, null, null, null, false, [Owner("mod-a", 1), Owner("mod-b", 2)], "shared"));
        Assert.Equal([Owned], ledger.SoleOwnerResources("mod-a"));
        Assert.Contains(Shared, ledger.ForRecipe("mod-a").Select(e => e.Resource));
        Assert.DoesNotContain(ledger.PlanUninstall("mod-a").Resources,
            r => r.Resource == Shared && r.Removable);
    }

    [Fact]
    public void LastOwnerLeavingRestoresTheBaseInsteadOfDeleting()
    {
        var baseBackup = Path.Combine(root, "backups", "base", Owned);
        Directory.CreateDirectory(Path.GetDirectoryName(baseBackup)!);
        File.WriteAllText(baseBackup, "vanilla");
        var ledger = LedgerWith(new LedgerEntry(Owned, "vanilla-hash", baseBackup, null, true, [Owner("mod-a", 1)], "mod"));

        var change = Assert.Single(ledger.PlanUninstall("mod-a").Resources);
        Assert.Equal(UninstallAction.RestoreBase, change.Action);
        Assert.Equal("restore-base", change.Basis);
        Assert.True(change.Removable);
        Assert.Equal("vanilla", File.ReadAllText(baseBackup));

        var sharedEntry = LedgerWith(new LedgerEntry(Owned, null, null, null, false, [Owner("mod-a", 1)], "mod"))
            .PlanUninstall("mod-a").Resources.Single();
        Assert.Equal("remove", sharedEntry.Basis);
    }

    [Fact]
    public void SharedConfigFileIsKeptEvenForItsLastOwner()
    {
        var ledger = LedgerWith(new LedgerEntry("BepInEx/config/x.cfg", null, null, null, false, [Owner("mod-a", 1)], "cfg")
        {
            Shared = true,
        });
        Assert.Equal(UninstallAction.Keep, ledger.PlanUninstall("mod-a").Resources.Single().Action);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}