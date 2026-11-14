using System.IO.Compression;
using System.Security.Cryptography;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Services;

public sealed record InstallTarget(string GameRoot, string Store, bool Sandbox);
public sealed record InstallPreview(string FullName, string Version, IReadOnlyList<string> Files, long SizeBytes);
public sealed record InstallPreparation(string GameId, string ModFullName, string Version, string Endpoint,
    InstallTarget Target, IReadOnlyList<OperationPlan> Plans, IReadOnlyList<string> PackageRoots, IReadOnlyList<ConflictProposal> Conflicts,
    IReadOnlyList<InstallPreview> Preview, string SandboxRoot, string ProfileRoot, string StagingRoot,
    string StateRoot, string PackageRoot);
public sealed record InstallOutcome(bool Success, string? ErrorCode, IReadOnlyList<ExecutionJournal> Journals);

public sealed class ModInstallService
{
    private readonly string appDataRoot;
    private readonly ModCatalogClient catalog;
    private readonly IPackageTransport packageTransport;
    private readonly IReceiptTransport? receiptTransport;
    private readonly ProfileStore profiles;

    public ModInstallService(string appDataRoot, ModCatalogClient catalog,
        IPackageTransport? packageTransport = null, IReceiptTransport? receiptTransport = null, ProfileStore? profiles = null)
    {
        this.appDataRoot = appDataRoot;
        this.catalog = catalog;
        this.packageTransport = packageTransport ?? new HttpPackageTransport();
        this.receiptTransport = receiptTransport;
        this.profiles = profiles ?? new ProfileStore(Path.Combine(appDataRoot, "profiles"));
    }

    public async Task<InstallPreparation> PrepareAsync(string gameId, string modFullName, string version,
        string endpoint, InstallTarget target, IReadOnlyDictionary<string, string>? versionOverrides = null,
        CancellationToken ct = default)
    {
        if (gameId is not ("among-us" or "valheim")) throw new ArgumentException("Unsupported game id.", nameof(gameId));
        var definition = GameDefinitions.LoadAll().First(g => g.Slug == gameId);
        if (!target.Sandbox && (string.IsNullOrWhiteSpace(target.GameRoot) || !Path.IsPathFullyQualified(target.GameRoot)
            || string.IsNullOrEmpty(definition.MainExecutable) || !File.Exists(Path.Combine(target.GameRoot, definition.MainExecutable))))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "Game executable not found in selected directory."));
        using var db = new ModCatalogStore(Path.Combine(appDataRoot, "catalog.db"));
        await catalog.SyncAsync(gameId, ct);
        var mods = db.GetMods(gameId);
        var byName = mods.ToDictionary(m => m.FullName, StringComparer.OrdinalIgnoreCase);
        var source = new CatalogVersionSource(catalog, byName);
        var resolution = await new DependencyResolver(source).ResolveWithProposalsAsync(modFullName, version, versionOverrides, ct);
        if (resolution.Conflicts.Count > 0)
            return new(gameId, modFullName, version, endpoint, target, [], [], resolution.Conflicts, [], "", "", "", "", "");

        var root = target.Sandbox ? Directory.CreateTempSubdirectory("phaket-install-").FullName : "";
        var gameRoot = target.Sandbox ? Path.Combine(root, "game") : Path.GetFullPath(target.GameRoot);
        var profileRoot = Path.Combine(root.Length == 0 ? appDataRoot : root, "profile");
        var stagingRoot = Path.Combine(root.Length == 0 ? appDataRoot : root, "staging");
        var packages = Path.Combine(appDataRoot, "packages");
        var rootId = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(gameRoot))));
        var stateRoot = Path.Combine(appDataRoot, "state", gameId, rootId);
        if (target.Sandbox) Directory.CreateDirectory(gameRoot); Directory.CreateDirectory(profileRoot); Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(packages); Directory.CreateDirectory(stateRoot);

        if (target.Sandbox)
        {
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            File.WriteAllText(Path.Combine(gameRoot, definition.MainExecutable ?? "game.exe"), "sandbox fixture");
        }
        var plans = new List<OperationPlan>();
        var packageRoots = new List<string>();
        var previews = new List<InstallPreview>();
        foreach (var mod in resolution.Pinned)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await packageTransport.OpenReadAsync(mod.DownloadUrl, ct);
            var modPackages = Path.Combine(packages, SafeFile(mod.FullName) + "-" + SafeFile(mod.Version));
            Directory.CreateDirectory(modPackages);
            var packagePath = Path.Combine(modPackages, "package.zip");
            await using (var output = File.Create(packagePath)) await bytes.CopyToAsync(output, ct);
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(packagePath, ct)));
            using var zip = ZipFile.OpenRead(packagePath);
            var generated = RecipeGenerator.Generate(mod.FullName, mod.Version, mod.DownloadUrl, hash,
                zip.Entries.Select(e => e.FullName), definition);
            var recipe = generated.Recipe;
            var facts = new PlanFacts(OperatingSystem.IsWindows() ? "windows" : "linux", target.Store, null,
                OperationCatalog.Capabilities, uri => VirtualUri.TryParse(uri, out var scheme, out var path)
                    && scheme == "game" && File.Exists(Path.Combine(gameRoot, path)),
                mod.FullName.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase)
                    ? new Dictionary<string, string> { ["bepinex"] = mod.Version } : null);
            var context = new PlannerContext("install", "mod-profile", gameRoot, profileRoot, stagingRoot, modPackages, definition, facts);
            plans.Add(OperationPlanner.Plan(recipe, context, generated.Ownership.ToDictionary(o => o.StepId, o => o.Ownership)));
            packageRoots.Add(modPackages);
            previews.Add(new(mod.FullName, mod.Version,
                zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).ToArray(), zip.Entries.Sum(e => e.Length)));
        }
        return new(gameId, modFullName, version, endpoint, target, plans, packageRoots, [], previews, root, profileRoot, stagingRoot, stateRoot, packages);
    }

    public async Task<InstallOutcome> ExecuteAsync(InstallPreparation preparation, bool planAccepted,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (!planAccepted) throw new PhaketException("PLAN_NOT_ACCEPTED");
        var receiptTransport = this.receiptTransport ?? new HttpReceiptTransport(new HttpClient { BaseAddress = new Uri(preparation.Endpoint) });
        var journals = new List<ExecutionJournal>();
        var executed = new List<(OperationPlan Plan, PlanExecutionContext Context, int Index)>();
        for (var i = 0; i < preparation.Plans.Count; i++)
        {
            var plan = preparation.Plans[i];
            try
            {
                var definition = GameDefinitions.LoadAll().First(g => g.Slug == preparation.GameId);
                var ctx = new PlanExecutionContext(preparation.Target.Sandbox ? Path.Combine(preparation.SandboxRoot, "game") : Path.GetFullPath(preparation.Target.GameRoot),
                    preparation.ProfileRoot, preparation.StagingRoot, preparation.PackageRoots[i], definition,
                    plan.RecipeRef.Id, "mod-profile", preparation.StateRoot);
                var journal = new InstallOrchestrator(new PlanExecutor(ctx)).Run(plan, progress, ct,
                    new ReceiptContext(preparation.GameId, preparation.Target.Store, preparation.ModFullName));
                journals.Add(journal);
                executed.Add((plan, ctx, i));
                new InstallRegistry(Path.Combine(appDataRoot, "installs.json")).Add(new InstalledRecipe(plan.RecipeRef.Id,
                    plan.RecipeRef.Version, preparation.GameId, preparation.Target.Store, ctx.GameRoot, ctx.ProfileRoot,
                    ctx.StagingRoot, preparation.StateRoot, preparation.Target.Sandbox ? preparation.SandboxRoot : null, DateTimeOffset.UtcNow,
                    preparation.Preview[i].FullName, preparation.Preview[i].Version));
                var profile = profiles.Active(preparation.GameId) ?? profiles.AddProfile(preparation.GameId, "Installed mods");
                profile.Mods.Add(new ModEntry { Name = preparation.Preview[i].FullName, Version = preparation.Preview[i].Version });
                profiles.Save();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var code = (ex as PhaketException)?.Error.Code ?? ex.GetType().Name;
                foreach (var (completedPlan, completedContext, completedIndex) in executed.AsEnumerable().Reverse())
                {
                    try { new PlanExecutor(completedContext).Uninstall(completedPlan.RecipeRef.Id, DivergencePolicy.Skip); }
                    catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Install rollback failed: {0}", rollbackError); }
                    new InstallRegistry(Path.Combine(appDataRoot, "installs.json")).Remove(completedPlan.RecipeRef.Id, completedContext.GameRoot);
                    var profile = profiles.Active(preparation.GameId);
                    if (profile is not null)
                    {
                        var name = preparation.Preview[completedIndex].FullName;
                        profile.Mods.RemoveAll(m => m.Name == name);
                        profiles.Save();
                    }
                }
                await new ReceiptSender(receiptTransport).SendAsync(preparation.GameId, preparation.ModFullName,
                    preparation.Version, preparation.Target.Store, "failed", code, CancellationToken.None);
                return new(false, code, journals);
            }
        }
        await new ReceiptSender(receiptTransport).SendAsync(preparation.GameId, preparation.ModFullName,
            preparation.Version, preparation.Target.Store, "installed", cancellationToken: ct);
        return new(true, null, journals);
    }

    private static string SafeFile(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private sealed class CatalogVersionSource(ModCatalogClient client, IReadOnlyDictionary<string, ThunderstoreMod> mods) : IModVersionSource
    {
        public async Task<ThunderstoreModDetails?> GetDetailsAsync(string fullName, CancellationToken cancellationToken)
        {
            if (!mods.TryGetValue(fullName, out var mod)) return null;
            try { return await client.GetModAsync(mod.Id, cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
        }
    }
}
