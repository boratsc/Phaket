using System.Net.Http;
using System.Security.Cryptography;
using Phaket.Core.Catalog;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.ViewModels;

namespace Phaket.Services;

public sealed record ModpackInstallPlan(string Target, IReadOnlyList<string> Steps,
    IReadOnlyList<InstallPreparation> Preparations, string? BlockReason = null, bool ReadyForExecution = false,
    ModpackSourceInstallPlan? SourcePlan = null, ModpackSourceInstallContext? SourceContext = null,
    string? ProfileId = null, IReadOnlyList<string>? WarningCodes = null)
{
    public bool CanInstall => BlockReason is null && (Preparations.Count > 0 || SourcePlan?.CanInstall == true || ReadyForExecution);
    public IReadOnlyList<string> Warnings => WarningCodes ?? [];
}

public sealed record ModpackInstallResult(bool Success, string ProfileId, string? Error);

public interface IModpackInstallService
{
    Task<ModpackInstallPlan> PrepareAsync(GameCatalogItem game, ModpackDefinition pack, CancellationToken cancellationToken = default);
    Task<ModpackInstallResult> InstallAsync(GameCatalogItem game, ModpackDefinition pack,
        ModpackInstallPlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

// Adapts catalog entries to the existing Thunderstore and transactional Core installers.
public sealed class ModpackInstallService : IModpackInstallService
{
    private readonly string appDataRoot;
    private readonly ModpackSourceInstaller sourceInstaller;
    private readonly ICatalogTransport? catalogTransport;
    private readonly IPackageTransport? packageTransport;
    private readonly GitHubReleaseService? githubService;

    public ModpackInstallService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket")) { }

    internal ModpackInstallService(string appDataRoot, ModpackSourceInstaller? sourceInstaller = null,
        ICatalogTransport? catalogTransport = null, IPackageTransport? packageTransport = null,
        GitHubReleaseService? githubService = null)
    {
        this.appDataRoot = Path.GetFullPath(appDataRoot);
        this.sourceInstaller = sourceInstaller ?? new ModpackSourceInstaller(packageTransport);
        this.catalogTransport = catalogTransport;
        this.packageTransport = packageTransport;
        this.githubService = githubService;
    }

    public async Task<ModpackInstallPlan> PrepareAsync(GameCatalogItem game, ModpackDefinition pack,
        CancellationToken cancellationToken = default)
    {
        var enabledItems = pack.Items.Where(item => item.Enabled).ToArray();
        if (enabledItems.Length == 0) return Block("Catalog.InstallEmpty");
        var selected = game.SelectedInstallation;
        if (selected is null || !Directory.Exists(selected.RootPath)) return Block("Catalog.InstallGameMissing");
        if (!string.Equals(game.Definition.Slug, pack.GameId, StringComparison.OrdinalIgnoreCase))
            return Block("Catalog.InstallGameMismatch");

        string profileBase = Path.Combine(appDataRoot, "modpack-profiles", Guid.NewGuid().ToString("N"));
        string profileId = Guid.NewGuid().ToString("N");
        string packagesRoot = Path.Combine(appDataRoot, "packages", "modpacks", profileId);
        string stagingRoot = Path.Combine(appDataRoot, "staging", "modpacks", profileId);
        string installerProfileRoot = Path.Combine(profileBase, "sources");
        string stateRoot = Path.Combine(appDataRoot, "state", pack.GameId,
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(selected.RootPath)))));
        var warnings = new List<string>();
        var preparations = new List<InstallPreparation>();
        ModpackSourceInstallPlan? sourcePlan = null;
        ModpackSourceInstallContext? sourceContext = null;
        try
        {
            var thunderstoreItems = enabledItems.Where(item => item.Source == "thunderstore").ToArray();
            if (thunderstoreItems.Length > 0)
            {
                var endpoint = CatalogSettings.Load(CatalogSettings.SettingsFile(appDataRoot));
                using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(15) };
                using var db = new ModCatalogStore(Path.Combine(appDataRoot, "catalog.db"));
                var client = new ModCatalogClient(catalogTransport ?? new HttpCatalogTransport(http), db);
                await client.SyncAsync(pack.GameId, cancellationToken);
                var byId = db.GetMods(pack.GameId).ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
                var install = new ModInstallService(appDataRoot, client, packageTransport ?? new HttpPackageTransport());
                for (int i = 0; i < thunderstoreItems.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = thunderstoreItems[i];
                    if (item.ModId is null || !byId.TryGetValue(item.ModId, out var found))
                        return Block("Catalog.InstallPlanFailed");
                    var preparation = await install.PrepareAsync(pack.GameId, found.FullName, item.Version,
                        endpoint, new(selected.RootPath, selected.Store, false), ct: cancellationToken,
                        profileRootOverride: Path.Combine(profileBase, "thunderstore", i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)));
                    if (preparation.Conflicts.Count > 0) return Block("Catalog.InstallVersionConflict");
                    if (preparation.Plans.Count == 0 || preparation.Plans.Any(p => p.Operations.Count == 0))
                        return Block("Catalog.InstallPlanEmpty");
                    preparations.Add(preparation);
                    warnings.AddRange(preparation.Plans.SelectMany(plan => plan.Warnings));
                }
            }

            var sourceModItems = enabledItems.Where(item => item.Source is "github" or "nexus").ToArray();
            var sourceItems = new List<ModpackSourceItem>(sourceModItems.Length);
            if (sourceModItems.Length > 0)
            {
                using var ownedGitHub = githubService is null ? new GitHubReleaseService() : null;
                var github = githubService ?? ownedGitHub!;
                foreach (var item in sourceModItems)
                {
                    if (item.Source == "nexus")
                    {
                        // W1: a public package never brings a local archive path from the server, so the
                        // user has to point the downloaded archive themselves before a plan is made.
                        if (string.IsNullOrWhiteSpace(item.ManualPath))
                            return Block(pack.IsPublic ? "Catalog.InstallNexusLocalArchiveRequired" : "Catalog.InstallNexusManualRequiredHint");
                        if (pack.IsPublic && !NexusManualViewModel.IsValidArchive(item.ManualPath))
                            return Block("Catalog.InstallNexusLocalArchiveRequired");
                    }
                    sourceItems.Add(await ToCoreItemAsync(item, github, cancellationToken));
                }
            }
            if (sourceItems.Count > 0)
            {
                Directory.CreateDirectory(packagesRoot);
                Directory.CreateDirectory(stagingRoot);
                Directory.CreateDirectory(profileBase);
                sourceContext = new ModpackSourceInstallContext("install-" + profileId, profileId,
                    Path.GetFullPath(selected.RootPath), installerProfileRoot, stagingRoot, packagesRoot, stateRoot,
                    game.Definition, Facts(game, selected));
                sourcePlan = await sourceInstaller.PrepareAsync(sourceItems, sourceContext, cancellationToken);
                if (!sourcePlan.CanInstall) return Block("Catalog.InstallPlanEmpty");
                warnings.AddRange(sourcePlan.Warnings);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (PhaketException ex) { return Block(MapCoreError(ex.Error.Code)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Block("Catalog.InstallPlanFailed");
        }

        var steps = new List<string> { "Catalog.PlanCheck", "Catalog.PlanDownload", "Catalog.PlanRestore" };
        if (preparations.Count > 0) steps.AddRange(preparations.SelectMany(p => p.Plans.SelectMany(plan => plan.Operations))
            .Select(operation => operation.Kind + " · " + operation.TargetPath));
        if (sourcePlan is not null) steps.AddRange(sourcePlan.Items.SelectMany(item => item.Plan.Operations)
            .Select(operation => operation.Kind + " · " + operation.TargetPath));
        return new(selected.RootPath, steps, preparations, ReadyForExecution: true, SourcePlan: sourcePlan,
            SourceContext: sourceContext, ProfileId: profileId, WarningCodes: warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<ModpackInstallResult> InstallAsync(GameCatalogItem game, ModpackDefinition pack,
        ModpackInstallPlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!plan.CanInstall || plan.ProfileId is null) return new(false, "", plan.BlockReason ?? "Catalog.InstallPlanFailed");
        var profiles = new ProfileStore(appDataRoot);
        var profileData = profiles.For(pack.GameId);
        var previousProfile = profileData.ActiveProfileId;
        string profileId = plan.ProfileId;
        string root = game.SelectedInstallation?.RootPath ?? plan.Target;
        var registry = new InstallRegistry(Path.Combine(appDataRoot, "installs.json"));
        var registered = new List<(string RecipeId, string GameRoot, PlanExecutionContext Context)>();
        try
        {
            profiles.CreateBackup(pack.GameId, $"{pack.Name} · przed instalacją", "modpack-restore-point");
            var profile = new GameProfile
            {
                Id = profileId,
                Name = pack.Name,
                Mods = plan.Preparations.SelectMany(p => p.Preview)
                    .Select(p => new ModEntry { Name = p.FullName, Version = p.Version })
                    .Concat(plan.SourcePlan?.Items.Select(item => new ModEntry { Name = item.Name, Version = item.Version }) ?? []).ToList()
            };
            profileData.Profiles.Add(profile);
            profileData.ActiveProfileId = profileId;
            profiles.Save();

            var total = plan.Preparations.Sum(p => p.Plans.Count) + (plan.SourcePlan?.Items.Count ?? 0);
            var completed = 0;
            if (plan.Preparations.Count > 0)
            {
                var endpoint = CatalogSettings.Load(CatalogSettings.SettingsFile(appDataRoot));
                using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(15) };
                using var db = new ModCatalogStore(Path.Combine(appDataRoot, "catalog.db"));
                var service = new ModInstallService(appDataRoot, new ModCatalogClient(catalogTransport ?? new HttpCatalogTransport(http), db), packageTransport ?? new HttpPackageTransport(), profiles: profiles);
                foreach (var preparation in plan.Preparations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await service.ExecuteAsync(preparation, true,
                        new Progress<InstallProgress>(value => progress?.Report((completed + value.Fraction) / Math.Max(1, total))),
                        cancellationToken, profileId);
                    if (!result.Success) throw new PhaketException(result.ErrorCode ?? "Catalog.InstallFailed");
                    foreach (var journal in result.Journals)
                    {
                        var index = preparation.Plans.ToList().FindIndex(item => item.PlanId == journal.PlanId);
                        if (index < 0) continue;
                        var operationPlan = preparation.Plans[index];
                        var executionContext = new PlanExecutionContext(
                            preparation.Target.Sandbox ? Path.Combine(preparation.SandboxRoot, "game") : Path.GetFullPath(preparation.Target.GameRoot),
                            preparation.PlanProfileRoots is { Count: > 0 } roots ? roots[index] : preparation.ProfileRoot,
                            preparation.StagingRoot, preparation.PackageRoots[index], GameDefinitions.LoadAll().First(g => g.Slug == preparation.GameId),
                            operationPlan.RecipeRef.Id, profileId, preparation.StateRoot);
                        registry.Add(new InstalledRecipe(operationPlan.RecipeRef.Id, operationPlan.RecipeRef.Version,
                            pack.GameId, preparation.Target.Store, executionContext.GameRoot, executionContext.ProfileRoot,
                            executionContext.StagingRoot, preparation.StateRoot, preparation.SandboxRoot.Length == 0 ? null : preparation.SandboxRoot,
                            DateTimeOffset.UtcNow, preparation.ModFullName, preparation.Version));
                        registered.Add((operationPlan.RecipeRef.Id, executionContext.GameRoot, executionContext));
                    }
                    completed++;
                }
            }
            if (plan.SourcePlan is not null && plan.SourceContext is { } context)
            {
                var outcome = sourceInstaller.Execute(plan.SourcePlan, true,
                    new Progress<InstallProgress>(value => progress?.Report((completed + value.Fraction) / Math.Max(1, total))), cancellationToken);
                if (!outcome.Success) throw new PhaketException(outcome.ErrorCode ?? "Catalog.InstallFailed");
                var current = new InstallRegistry(Path.Combine(appDataRoot, "installs.json"));
                for (int i = 0; i < plan.SourcePlan.Items.Count; i++)
                {
                    var item = plan.SourcePlan.Items[i];
                    current.Add(new InstalledRecipe(item.Plan.RecipeRef.Id, item.Plan.RecipeRef.Version, pack.GameId,
                        game.SelectedInstallation?.Store ?? "", context.GameRoot, item.ExecutionContext.ProfileRoot,
                        item.ExecutionContext.StagingRoot, context.StateRoot, null, DateTimeOffset.UtcNow, item.Name, item.Version));
                    registered.Add((item.Plan.RecipeRef.Id, context.GameRoot, item.ExecutionContext));
                    completed++;
                }
            }
            progress?.Report(1);
            return new(true, profileId, null);
        }
        catch (OperationCanceledException)
        {
            RollbackRegistered(registered, root, profiles, pack.GameId, profileId, previousProfile);
            throw;
        }
        catch (PhaketException ex)
        {
            RollbackRegistered(registered, root, profiles, pack.GameId, profileId, previousProfile);
            return new(false, profileId, MapCoreError(ex.Error.Code));
        }
        catch (Exception)
        {
            RollbackRegistered(registered, root, profiles, pack.GameId, profileId, previousProfile);
            return new(false, profileId, "Catalog.InstallFailed");
        }
        finally
        {
            if (previousProfile is not null && profiles.For(pack.GameId).Profiles.Any(p => p.Id == previousProfile))
                profiles.SetActive(pack.GameId, previousProfile);
        }
    }

    private void RollbackRegistered(List<(string RecipeId, string GameRoot, PlanExecutionContext Context)> registered,
        string gameRoot, ProfileStore profiles, string gameId, string profileId, string? previousProfile)
    {
        var registry = new InstallRegistry(Path.Combine(appDataRoot, "installs.json"));
        foreach (var entry in registered.AsEnumerable().Reverse())
        {
            try
            {
                new PlanExecutor(entry.Context).Uninstall(entry.RecipeId, DivergencePolicy.Skip);
                registry.Remove(entry.RecipeId, entry.GameRoot);
            }
            catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Modpack rollback failed: {0}", rollbackError); }
        }
        var data = profiles.For(gameId);
        data.Profiles.RemoveAll(item => item.Id == profileId);
        data.ActiveProfileId = previousProfile;
        try { profiles.Save(); }
        catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Modpack profile rollback failed: {0}", rollbackError); }
    }

    private static async Task<ModpackSourceItem> ToCoreItemAsync(ModpackMod item, GitHubReleaseService github,
        CancellationToken cancellationToken)
    {
        if (item.Source == "github")
        {
            var repo = GitHubReleaseService.ParseRepository(item.Repository);
            if (!long.TryParse(item.AssetId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var assetId) || repo is null
                || string.IsNullOrWhiteSpace(item.Tag))
                throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "GitHub"), ("detail", "invalid pinned repository, tag, or asset id"));
            if (item.Link is not null && !SourcePolicy.IsGitHubAssetUrl(item.Link))
                throw new PhaketException(ErrorCodes.ModSourceHostNotAllowed,
                    ("host", Uri.TryCreate(item.Link, UriKind.Absolute, out var rejected) ? rejected.Host : "invalid"));
            var releases = await github.GetReleasesAsync(repo, cancellationToken: cancellationToken);
            var release = releases.Releases.FirstOrDefault(candidate => candidate.Tag == item.Tag);
            var asset = release?.Assets.FirstOrDefault(candidate => candidate.Id == assetId);
            if (asset is null || item.Link is not null && !string.Equals(asset.DownloadUrl, item.Link, StringComparison.Ordinal))
                throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "GitHub"), ("detail", "pinned release asset no longer matches the selected release"));
            return new ModpackSourceItem("github", item.Name, item.Version, Repo: repo, Tag: item.Tag,
                AssetId: asset.Id, AssetName: asset.Name, DownloadUrl: asset.DownloadUrl, Size: asset.Size, Sha256: item.Sha256);
        }
        if (item.Source == "nexus")
            // ManualPath is only ever set by the local user (see ModpackApiClient trust boundary); the
            // server copy of this field is dropped before it can reach the Core installer.
            return new ModpackSourceItem("nexus", item.Name, item.Version, Url: item.Link,
                PinnedVersion: item.Version, LocalArchivePath: item.ManualPath);
        throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", item.Source), ("detail", "unsupported source"));
    }

    private static PlanFacts Facts(GameCatalogItem game, InstallationChoice selected)
    {
        long? buildId = long.TryParse(selected.BuildId, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var build) ? build : null;
        var arch = Phaket.Core.Platforms.InstallationArchProbe.Probe(selected.RootPath, game.Definition)
            ?? (selected.Store == "steam" && selected.BuildId is null ? game.Definition.DefaultArch : null);
        return new PlanFacts(OperatingSystem.IsWindows() ? "windows" : "linux", selected.Store, null,
            OperationCatalog.Capabilities, uri => Phaket.Core.Omrl.VirtualUri.TryParse(uri, out var scheme, out var path)
                && scheme == "game" && File.Exists(Path.Combine(selected.RootPath, path)), Arch: arch, BuildId: buildId);
    }

    private static string MapCoreError(string code) => code switch
    {
        ErrorCodes.ArchiveFormatUnsupported => "Catalog.InstallArchiveUnsupported",
        ErrorCodes.ArtifactHashMismatch => "Catalog.InstallHashMismatch",
        ErrorCodes.ArtifactSizeMismatch => "Catalog.InstallSizeMismatch",
        ErrorCodes.ModSourceHostNotAllowed => "Catalog.InstallHostNotAllowed",
        ErrorCodes.ModSourceInvalid => "Catalog.InstallPinnedAssetUnavailable",
        ErrorCodes.ArtifactDownloadUnavailable => "Catalog.InstallPinnedAssetUnavailable",
        _ => code.StartsWith("Catalog.", StringComparison.Ordinal) ? code : "Catalog.InstallPlanFailed"
    };

    internal static string GetMappedErrorForTest(string code) => MapCoreError(code);

    private static ModpackInstallPlan Block(string key) => new("", [], [], key);
}
