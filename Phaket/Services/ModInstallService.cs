using System.IO.Compression;
using System.Security.Cryptography;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Services;

public sealed record InstallTarget(string GameRoot, string Store, bool Sandbox, string? Arch = null);
public sealed record InstallPreview(string FullName, string Version, IReadOnlyList<string> Files, long SizeBytes);
public sealed record InstallPreparation(string GameId, string ModFullName, string Version, string Endpoint,
    InstallTarget Target, IReadOnlyList<OperationPlan> Plans, IReadOnlyList<string> PackageRoots, IReadOnlyList<ConflictProposal> Conflicts,
    IReadOnlyList<InstallPreview> Preview, string SandboxRoot, string ProfileRoot, string StagingRoot,
    string StateRoot, string PackageRoot, IReadOnlyList<string>? PlanProfileRoots = null);
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
        CancellationToken ct = default, string? profileRootOverride = null)
    {
        if (gameId is not ("among-us" or "valheim")) throw new ArgumentException("Unsupported game id.", nameof(gameId));
        if (target.Arch is not (null or "x86" or "x64")) throw new ArgumentException("Unsupported target architecture.", nameof(target));
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
        var profileRoot = profileRootOverride is null
            ? Path.Combine(root.Length == 0 ? appDataRoot : root, "profile")
            : Path.GetFullPath(profileRootOverride);
        if (profileRootOverride is not null && !PathBoundary.IsWithin(Path.GetFullPath(appDataRoot), profileRoot))
            throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", profileRootOverride));
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
        // Explicit user/build choice wins; then the real installation's PE header; a sandbox with
        // nothing to probe falls back to the current retail architecture from the game definition
        // (PAK-65: a sandbox must plan, not refuse). A real install is never guessed about.
        var arch = target.Arch ?? InstallationArchProbe.Probe(gameRoot, definition)
            ?? (target.Sandbox ? definition.DefaultArch : null);
        long? buildId = null;
        if (!target.Sandbox && target.Store == "steam" && definition.StoreIds?.TryGetValue("steam", out var appId) == true
            && appId.All(char.IsAsciiDigit))
        {
            var gameDirectory = new DirectoryInfo(gameRoot);
            var steamApps = gameDirectory.Parent?.Parent;
            if (gameDirectory.Parent?.Name == "common" && steamApps is not null)
            {
                var manifestPath = Path.Combine(steamApps.FullName, $"appmanifest_{appId}.acf");
                try
                {
                    if (File.Exists(manifestPath))
                    {
                        var manifest = SteamAdapter.ParseAppManifest(manifestPath);
                        if (manifest?.AppId == appId && manifest.InstallDir == gameDirectory.Name
                            && long.TryParse(manifest.BuildId, System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out var parsedBuild))
                            buildId = parsedBuild;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { }
            }
        }
        var plans = new List<OperationPlan>();
        var packageRoots = new List<string>();
        var previews = new List<InstallPreview>();
        var planProfileRoots = new List<string>();
        foreach (var mod in resolution.Pinned)
        {
            ct.ThrowIfCancellationRequested();
            var modPackages = Path.Combine(packages, SafeFile(mod.FullName) + "-" + SafeFile(mod.Version));
            Directory.CreateDirectory(modPackages);
            var loader = mod.FullName.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase);
            var hash = new string('0', 64);
            string[] entries = [];
            byte[]? packageBytes = null;
            if (!loader)
            {
                await using var sourceStream = await packageTransport.OpenReadAsync(mod.DownloadUrl, ct);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int count;
                while ((count = await sourceStream.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + count > 512L * 1024 * 1024) throw new PhaketException(ErrorCodes.ArtifactDownloadUnavailable, ("detail", "download too large"));
                    await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
                }
                packageBytes = buffer.ToArray();
                hash = Convert.ToHexStringLower(SHA256.HashData(packageBytes));
                buffer.Position = 0;
                using var sourceZip = new ZipArchive(buffer, ZipArchiveMode.Read);
                entries = sourceZip.Entries.Select(e => e.FullName).ToArray();
            }
            var generated = RecipeGenerator.Generate(mod.FullName, mod.Version, mod.DownloadUrl, hash,
                entries, definition, arch);
            var recipe = generated.Recipe;
            if (loader && arch is null) throw new PhaketException("LOADER_ARCH_UNAVAILABLE", ("arch", "unknown"));
            await PackageDownloader.EnsureArtifactsAsync(recipe, modPackages,
                packageBytes is null ? packageTransport : new BufferedPackageTransport(mod.DownloadUrl, packageBytes, packageTransport), cancellationToken: ct);
            using var zip = ZipFile.OpenRead(Path.Combine(modPackages, recipe.Artifacts[0].Id + ".zip"));
            var facts = new PlanFacts(OperatingSystem.IsWindows() ? "windows" : "linux", target.Store, null,
                OperationCatalog.Capabilities, uri => VirtualUri.TryParse(uri, out var scheme, out var path)
                    && scheme == "game" && File.Exists(Path.Combine(gameRoot, path)),
                mod.FullName.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase)
                    ? new Dictionary<string, string> { ["bepinex"] = mod.Version } : null,
                Arch: arch, BuildId: buildId);
            var planProfileRoot = Path.Combine(profileRoot, plans.Count.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(planProfileRoot);
            var context = new PlannerContext("install", "mod-profile", gameRoot, planProfileRoot, stagingRoot, modPackages, definition, facts);
            plans.Add(OperationPlanner.Plan(recipe, context, generated.Ownership.ToDictionary(o => o.StepId, o => o.Ownership)));
            planProfileRoots.Add(planProfileRoot);
            packageRoots.Add(modPackages);
            previews.Add(new(mod.FullName, mod.Version,
                zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).ToArray(), zip.Entries.Sum(e => e.Length)));
        }
        return new(gameId, modFullName, version, endpoint, target, plans, packageRoots, [], previews, root, profileRoot, stagingRoot,
            stateRoot, packages, planProfileRoots);
    }

    public async Task<InstallOutcome> ExecuteAsync(InstallPreparation preparation, bool planAccepted,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default, string? targetProfileId = null)
    {
        if (!planAccepted) throw new PhaketException("PLAN_NOT_ACCEPTED");
        // PAK-65: a plan with no operations means the recipe did not match this installation.
        // Running it would "succeed" and register an install that changed nothing.
        if (preparation.Plans.Count == 0 || preparation.Plans.Any(p => p.Operations.Count == 0))
            throw new PhaketException(ErrorCodes.PlanEmpty);
        var receiptTransport = this.receiptTransport ?? new HttpReceiptTransport(new HttpClient { BaseAddress = new Uri(preparation.Endpoint) });
        var journals = new List<ExecutionJournal>();
        var executed = new List<(OperationPlan Plan, PlanExecutionContext Context, int Index)>();
        (OperationPlan Plan, PlanExecutionContext Context, int Index)? activeAttempt = null;
        var activeAttemptCompleted = false;
        for (var i = 0; i < preparation.Plans.Count; i++)
        {
            var plan = preparation.Plans[i];
            try
            {
                var definition = GameDefinitions.LoadAll().First(g => g.Slug == preparation.GameId);
                var planProfileRoot = preparation.PlanProfileRoots is { Count: > 0 } roots ? roots[i] : preparation.ProfileRoot;
                var ctx = new PlanExecutionContext(preparation.Target.Sandbox ? Path.Combine(preparation.SandboxRoot, "game") : Path.GetFullPath(preparation.Target.GameRoot),
                    planProfileRoot, preparation.StagingRoot, preparation.PackageRoots[i], definition,
                    plan.RecipeRef.Id, "mod-profile", preparation.StateRoot);
                activeAttempt = (plan, ctx, i);
                activeAttemptCompleted = false;
                var journal = new InstallOrchestrator(new PlanExecutor(ctx)).Run(plan, progress, ct,
                    new ReceiptContext(preparation.GameId, preparation.Target.Store, preparation.ModFullName));
                journals.Add(journal);
                executed.Add((plan, ctx, i));
                activeAttemptCompleted = true;
                new InstallRegistry(Path.Combine(appDataRoot, "installs.json")).Add(new InstalledRecipe(plan.RecipeRef.Id,
                    plan.RecipeRef.Version, preparation.GameId, preparation.Target.Store, ctx.GameRoot, ctx.ProfileRoot,
                    ctx.StagingRoot, preparation.StateRoot, preparation.Target.Sandbox ? preparation.SandboxRoot : null, DateTimeOffset.UtcNow,
                    preparation.Preview[i].FullName, preparation.Preview[i].Version));
                if (targetProfileId is not null)
                {
                    var profile = profiles.For(preparation.GameId).Profiles.FirstOrDefault(p => p.Id == targetProfileId)
                        ?? throw new InvalidOperationException("Install profile not found.");
                    if (!profile.Mods.Any(mod => string.Equals(mod.Name, preparation.Preview[i].FullName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(mod.Version, preparation.Preview[i].Version, StringComparison.OrdinalIgnoreCase)))
                        profile.Mods.Add(new ModEntry { Name = preparation.Preview[i].FullName, Version = preparation.Preview[i].Version });
                    profiles.Save();
                }
                else
                {
                    var profile = profiles.Active(preparation.GameId) ?? profiles.AddProfile(preparation.GameId, "Installed mods");
                    profile.Mods.Add(new ModEntry { Name = preparation.Preview[i].FullName, Version = preparation.Preview[i].Version });
                    profiles.Save();
                }
                activeAttempt = null;
            }
            catch (OperationCanceledException)
            {
                RollbackFailedAttempt(activeAttempt, activeAttemptCompleted, executed, profiles, preparation, appDataRoot);
                throw;
            }
            catch (Exception ex)
            {
                var code = (ex as PhaketException)?.Error.Code ?? ex.GetType().Name;
                RollbackFailedAttempt(activeAttempt, activeAttemptCompleted, executed, profiles, preparation, appDataRoot);
                await new ReceiptSender(receiptTransport).SendAsync(preparation.GameId, preparation.ModFullName,
                    preparation.Version, preparation.Target.Store, "failed", code, CancellationToken.None);
                return new(false, code, journals);
            }
        }
        await new ReceiptSender(receiptTransport).SendAsync(preparation.GameId, preparation.ModFullName,
            preparation.Version, preparation.Target.Store, "installed", cancellationToken: ct);
        return new(true, null, journals);
    }

    private static void RollbackFailedAttempt(
        (OperationPlan Plan, PlanExecutionContext Context, int Index)? activeAttempt,
        bool activeAttemptCompleted,
        List<(OperationPlan Plan, PlanExecutionContext Context, int Index)> executed,
        ProfileStore profiles,
        InstallPreparation preparation,
        string appDataRoot)
    {
        if (activeAttempt is { } active && !activeAttemptCompleted)
        {
            try
            {
                var journal = PlanExecutor.LoadJournal(active.Context.ProfileRoot);
                if (journal is not null && journal.State != JournalState.RolledBack)
                    new PlanExecutor(active.Context).Rollback(journal, DivergencePolicy.Skip);
            }
            catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Install rollback failed: {0}", rollbackError); }
        }

        foreach (var (plan, context, index) in executed.AsEnumerable().Reverse())
        {
            try { new PlanExecutor(context).Uninstall(plan.RecipeRef.Id, DivergencePolicy.Skip); }
            catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Install rollback failed: {0}", rollbackError); }
            try { new InstallRegistry(Path.Combine(appDataRoot, "installs.json")).Remove(plan.RecipeRef.Id, context.GameRoot); }
            catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Install registry cleanup failed: {0}", rollbackError); }
            try
            {
                var profile = profiles.Active(preparation.GameId);
                if (profile is not null)
                {
                    var name = preparation.Preview[index].FullName;
                    profile.Mods.RemoveAll(m => m.Name == name);
                    profiles.Save();
                }
            }
            catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Profile rollback failed: {0}", rollbackError); }
        }

        if (activeAttempt is { } failed && !activeAttemptCompleted)
        {
            try { new InstallRegistry(Path.Combine(appDataRoot, "installs.json")).Remove(failed.Plan.RecipeRef.Id, failed.Context.GameRoot); }
            catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Install registry cleanup failed: {0}", rollbackError); }
        }
    }

    private static string SafeFile(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private sealed class BufferedPackageTransport(string url, byte[] bytes, IPackageTransport fallback) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken) => source == url
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : fallback.OpenReadAsync(source, cancellationToken);
    }

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
