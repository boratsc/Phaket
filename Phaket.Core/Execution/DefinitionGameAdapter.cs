using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Execution;

// Generic adapter driven entirely by a declarative game definition — no
// game-specific branches in Core (spec §6.1/§6.3). The host supplies roots,
// recipe content and the trusted executor; the adapter only plans/orchestrates.
public sealed class DefinitionGameAdapter : IGameAdapter
{
    private readonly GameDefinition definition;
    private readonly string os;
    private readonly string store;
    private readonly string gameRoot;
    private readonly string profileRoot;
    private readonly string stagingRoot;
    private readonly string packageRoot;
    private readonly Func<RecipeRef, Recipe> recipeResolver;

    public DefinitionGameAdapter(
        GameDefinition definition, string os, string store,
        string gameRoot, string profileRoot, string stagingRoot, string packageRoot,
        Func<RecipeRef, Recipe> recipeResolver)
    {
        this.definition = definition;
        this.os = os;
        this.store = store;
        this.gameRoot = gameRoot;
        this.profileRoot = profileRoot;
        this.stagingRoot = stagingRoot;
        this.packageRoot = packageRoot;
        this.recipeResolver = recipeResolver;
    }

    public IReadOnlyList<Installation> DetectInstallations(PlatformSnapshot snapshot) =>
        snapshot.Installations
            .Where(facts => Directory.Exists(facts.Installation.RootPath))
            .Select(facts => facts.Installation)
            .ToArray();

    public IReadOnlyList<string> ProbeVersion(InstallationFacts facts) => facts.Versions;

    public IReadOnlyList<ModdingMethod> SupportedMethods(InstallationFacts facts) =>
        (definition.LoaderLayouts ?? [])
            .Select(layout => new ModdingMethod(
                $"{layout.Id}-{layout.Variant}",
                ["loader.ensure", "archive.extract", "file.copy", "file.verify"],
                []))
            .ToArray();

    public OperationPlan PlanInstall(Profile profile, InstallationFacts facts)
    {
        if (profile.Recipes.Count == 0)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "profile has no recipes"));
        if (facts.Versions.Count > 1)
            throw new PhaketException(ErrorCodes.AmbiguousTarget, ("reason", "multiple game versions reported"));
        var recipe = recipeResolver(profile.Recipes[0]);
        var planFacts = new PlanFacts(os, store,
            facts.Versions.Count == 1 ? facts.Versions[0] : null,
            facts.Capabilities.ToHashSet(StringComparer.Ordinal),
            uri => VirtualUri.TryParse(uri, out var scheme, out var path)
                && scheme == "game" && File.Exists(Path.Combine(gameRoot, path)),
            Arch: facts.Arch ?? InstallationArchProbe.Probe(gameRoot, definition));
        var context = new PlannerContext(facts.Installation.InstallationId, profile.ProfileId,
            gameRoot, profileRoot, stagingRoot, packageRoot, definition, planFacts);
        return OperationPlanner.Plan(recipe, context);
    }

    public Task<ExecutionJournal> ApplyPlan(OperationPlan plan, IPlanExecutorHost host, CancellationToken cancellationToken)
    {
        var journal = host.Approve(plan);
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            journal = host.ExecuteOperation(plan, journal, operation.OpId);
        }
        return Task.FromResult(host.Complete(plan, journal));
    }

    public Task<VerificationResult> Verify(InstallationFacts facts, Profile profile, IPlanExecutorHost host, CancellationToken cancellationToken) =>
        Task.FromResult(host.VerifyPlan(PlanInstall(profile, facts)));

    public Task<ExecutionJournal> Rollback(ExecutionJournal journal, IPlanExecutorHost host, CancellationToken cancellationToken) =>
        Task.FromResult(host.Rollback(journal));

    public LaunchSpec BuildLaunchSpec(Profile profile, InstallationFacts facts) =>
        new(definition.MainExecutable ?? "", [], facts.Installation.RootPath,
            new Dictionary<string, string>());
}
