using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using Phaket.Core.Catalog;
using Phaket.Core.Accounts;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Services;

public sealed record InstallTarget(string GameRoot, string Store, bool Sandbox, string? Arch = null);
// PAK-127: the plan preview names both the package entry and the path the plan writes it to.
public sealed record InstallPreviewEntry(string Source, string Target)
{
    public string Display => Source == Target ? Target : Source + " → " + Target;
}
public sealed record InstallPreview(string FullName, string Version, IReadOnlyList<string> Files, long SizeBytes,
    // Source → destination pairs from the plan operations; Files carries the destinations.
    IReadOnlyList<InstallPreviewEntry>? Entries = null)
{
    public bool HasEntries => Entries is { Count: > 0 };
}
public sealed record InstallPreparation(string GameId, string ModFullName, string Version, string Endpoint,
    InstallTarget Target, IReadOnlyList<OperationPlan> Plans, IReadOnlyList<string> PackageRoots, IReadOnlyList<ConflictProposal> Conflicts,
    IReadOnlyList<InstallPreview> Preview, string SandboxRoot, string ProfileRoot, string StagingRoot,
    string StateRoot, string PackageRoot, IReadOnlyList<string>? PlanProfileRoots = null,
    // "registry" (build id) or "probe" (installed depot manifest); null when unknown.
    string? GameVersionSource = null,
    // Packages the catalog declared no sha256 for (Thunderstore v1): installed, but never
    // recorded as hash-verified in the install registry.
    IReadOnlyList<string>? UnverifiedPackages = null,
    // PAK-144: the game's appmanifest at install time, so the version pin records
    // the build the mods were actually installed against.
    SteamAppManifest? Manifest = null);
// ErrorParameters carries the message placeholders (e.g. the tool tail) so the host
// can show the localized reason instead of a bare code.
public sealed record InstallOutcome(bool Success, string? ErrorCode, IReadOnlyList<ExecutionJournal> Journals,
    IReadOnlyDictionary<string, string>? ErrorParameters = null);

public sealed class ModInstallService
{
    private readonly string appDataRoot;
    private readonly ModCatalogClient catalog;
    private readonly IPackageTransport packageTransport;
    private readonly IReceiptTransport? receiptTransport;
    private readonly ProfileStore profiles;
    private readonly IBaseGameSource? baseGameSource;
    private readonly ICatalogTransport? catalogTransport;
    // PAK-102: a full-package install provisions the pinned vanilla through DepotDownloader.
    // Steam auth comes from the same store and dialog as the library install; without them
    // the provision refuses with an actionable message instead of blocking on a prompt.
    private readonly SteamCredentialStore? steamCredentials;
    private readonly Func<DepotPrompt, CancellationToken, Task<string>>? steamPrompt;
    private readonly IProgress<string?>? steamMobileConfirmation;
    private readonly Func<CancellationToken, Task<LegendaryTokens>>? epicTokens;
    // W1: a build id the registry cannot resolve must not trigger a full registry sync
    // on every PrepareAsync. The install service is constructed per action, so the
    // negative cache is process-wide and expires, allowing a retry after a real sync.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> UnresolvedBuilds = new(StringComparer.Ordinal);
    private static readonly TimeSpan UnresolvedBuildsTtl = TimeSpan.FromMinutes(10);

    public ModInstallService(string appDataRoot, ModCatalogClient catalog,
        IPackageTransport? packageTransport = null, IReceiptTransport? receiptTransport = null, ProfileStore? profiles = null,
        IBaseGameSource? baseGameSource = null, ICatalogTransport? catalogTransport = null,
        SteamCredentialStore? steamCredentials = null,
        Func<DepotPrompt, CancellationToken, Task<string>>? steamPrompt = null,
        Func<CancellationToken, Task<LegendaryTokens>>? epicTokens = null,
        IProgress<string?>? steamMobileConfirmation = null)
    {
        this.appDataRoot = appDataRoot;
        this.catalog = catalog;
        this.packageTransport = packageTransport ?? new HttpPackageTransport();
        this.receiptTransport = receiptTransport;
        this.profiles = profiles ?? new ProfileStore(Path.Combine(appDataRoot, "profiles"));
        this.baseGameSource = baseGameSource;
        this.catalogTransport = catalogTransport;
        this.steamCredentials = steamCredentials;
        this.steamPrompt = steamPrompt;
        this.epicTokens = epicTokens;
        this.steamMobileConfirmation = steamMobileConfirmation;
    }

    public async Task<InstallPreparation> PrepareAsync(string gameId, string modFullName, string version,
        string endpoint, InstallTarget target, IReadOnlyDictionary<string, string>? versionOverrides = null,
        CancellationToken ct = default, string? profileRootOverride = null, string? modId = null)
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
        // Two catalog sources may publish the same full name (a Thunderstore package and a
        // SusModder mod); a caller that knows the catalog id resolves through it instead.
        var byName = mods.GroupBy(m => m.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byId = mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var source = new CatalogVersionSource(catalog, byName, byId, modId);
        var resolution = await new DependencyResolver(source).ResolveWithProposalsAsync(modFullName, version, versionOverrides, ct);
        if (resolution.Conflicts.Count > 0)
            return new(gameId, modFullName, version, endpoint, target, [], [], resolution.Conflicts, [], "", "", "", "", "");

        // PAK-101 follow-up: a version the source marks unavailable must not reach a plan,
        // whichever caller asked (catalog button, profile update, direct service use). The
        // view model keeps its own gate for the button label; this one is the contract.
        if (resolution.Pinned.FirstOrDefault(IsUnavailable) is { } unavailable)
            throw new PhaketException(ErrorCodes.ModArtifactUnavailable,
                ("artifact", $"{unavailable.FullName} {unavailable.Version}"),
                ("arch", target.Arch ?? "unknown"), ("store", target.Store));

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
        // PAK-118: a dependency already installed at this exact game root with the same
        // version is not reinstalled. The requested root always is, so repair/upgrade works.
        var installed = new InstallRegistry(Path.Combine(appDataRoot, "installs.json")).ForGame(gameId);
        var unverified = new List<string>();
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
        // W1/PAK-14 probe: the appmanifest is the installation's own version evidence.
        // It carries the build id and, when that is not enough, the installed depot
        // manifest ids that the registry publishes even for rows without a build id.
        string[] depotManifests = [];
        SteamAppManifest? gameManifest = null;
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
                        if (manifest?.AppId == appId && manifest.InstallDir == gameDirectory.Name)
                        {
                            if (long.TryParse(manifest.BuildId, System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out var parsedBuild))
                                buildId = parsedBuild;
                            depotManifests = manifest.DepotManifests?.Values.Distinct(StringComparer.Ordinal).ToArray() ?? [];
                            gameManifest = manifest;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { }
            }
        }
        var plans = new List<OperationPlan>();
        var packageRoots = new List<string>();
        var previews = new List<InstallPreview>();
        var planProfileRoots = new List<string>();
        // PAK-96: buildId -> version label comes from the cached build registry. When the
        // registry does not know the build id, the installation's own depot manifest
        // identifies the version (W1 fallback); neither fact means unknown, and the
        // planner refuses instead of guessing.
        var builds = await LoadBuildsAsync(gameId, target.Store, buildId, depotManifests, endpoint, ct);
        var installedVersion = GameBuildRegistry.VersionForBuildId(builds, target.Store, buildId);
        var versionSource = installedVersion is null ? null : "registry";
        if (installedVersion is null)
        {
            installedVersion = GameBuildRegistry.VersionForManifests(builds, target.Store, depotManifests);
            if (installedVersion is not null) versionSource = "probe";
        }
        for (var pinnedIndex = 0; pinnedIndex < resolution.Pinned.Count; pinnedIndex++)
        {
            var mod = resolution.Pinned[pinnedIndex];
            ct.ThrowIfCancellationRequested();
            // The resolver appends the requested root last; only its dependencies are skippable.
            // PAK-129: the registry entry alone is not proof — its ledger files must still exist.
            if (pinnedIndex < resolution.Pinned.Count - 1 && installed.Any(entry =>
                string.Equals(entry.GameRoot, gameRoot, StringComparison.Ordinal)
                && string.Equals(entry.ModFullName, mod.FullName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.ModVersion, mod.Version, StringComparison.Ordinal)
                && IsInstalledWithFiles(entry)))
                continue;
            var modPackages = Path.Combine(packages, SafeFile(mod.FullName) + "-" + SafeFile(mod.Version));
            Directory.CreateDirectory(modPackages);
            var loader = mod.FullName.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase);
            var hash = new string('0', 64);
            string[] entries = [];
            byte[]? packageBytes = null;
            GeneratedRecipe generated;
            // A full package pins a concrete vanilla build; when the registry publishes a
            // manifest for that build and store, the provision step uses it verbatim.
            var pinnedVersion = mod.Packaging == "full" ? RecipeGenerator.NormalizeGameVersion(mod.AmongVersion) : null;
            var provisionManifests = new Dictionary<string, string>(StringComparer.Ordinal);
            if (pinnedVersion is not null)
                foreach (var store in new[] { "steam", "epic" })
                    if (GameBuildRegistry.ForLabel(builds, store, pinnedVersion)?.ManifestIds.FirstOrDefault() is { Length: > 0 } manifestId)
                        provisionManifests[store] = manifestId;
            if (mod.Packaging is "full" or "dll")
            {
                // Catalog artifact chosen for this installation's arch/store; refusal
                // (MOD_ARTIFACT_UNAVAILABLE) is clearer than planning a wrong variant.
                var selected = ModArtifactSelector.Select(mod.Artifacts, mod.DownloadUrl, mod.Sha256, mod.FileSize,
                    mod.Arch, mod.FullName, mod.Version, arch, target.Store);
                // CDN/GitHub is only trusted with a server-declared hash. Promoting the hash
                // observed after the download would pin whatever an attacker served (TOFU).
                if (selected.Sha256 is null)
                    throw new PhaketException(ErrorCodes.ModArtifactHashUndeclared,
                        ("artifact", $"{mod.FullName} {mod.Version}"));
                // The catalog is the trusted source for the artifact, but the GitHub
                // host allowlist is registered by the recipe generator. Register it
                // before the download, or a GitHub-hosted artifact is refused first.
                SourcePolicy.AllowModArtifact(selected.Url, selected.Sha256);
                // A bare .dll artifact is cached under its artifact id, not as a ZIP;
                // this matches where PackageDownloader looks for it below.
                var singleDll = RecipeGenerator.IsSingleDllArtifact(mod.Packaging, selected.Url, selected.AssetName);
                var staged = Path.Combine(modPackages, singleDll ? "package" : "package.zip");
                var (observed, length) = await DownloadAsync(packageTransport, selected.Url, staged, selected.SizeBytes, ct);
                if (!string.Equals(selected.Sha256, observed, StringComparison.OrdinalIgnoreCase))
                    throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", mod.FullName));
                hash = selected.Sha256;
                if (!singleDll)
                {
                    using var stagedZip = ZipFile.OpenRead(staged);
                    entries = stagedZip.Entries.Select(e => e.FullName).ToArray();
                }
                generated = RecipeGenerator.GenerateCatalogPackage(mod.FullName, mod.Version, mod.Packaging,
                    selected.Url, hash, selected.SizeBytes ?? length, selected.Arch ?? arch, mod.AmongVersion, entries, definition,
                    selected.AssetName, provisionManifests.Count == 0 ? null : provisionManifests, selected.Stores);
            }
            else
            {
                if (!loader)
                {
                    packageBytes = await PackageDownloader.ReadPackageAsync(packageTransport, mod.DownloadUrl,
                        mod.FullName, mod.FileSize, ct);
                    hash = Convert.ToHexStringLower(SHA256.HashData(packageBytes));
                    try
                    {
                        using var sourceZip = new ZipArchive(new MemoryStream(packageBytes, writable: false), ZipArchiveMode.Read);
                        entries = sourceZip.Entries.Select(e => e.FullName).ToArray();
                    }
                    catch (InvalidDataException)
                    {
                        throw new PhaketException(ErrorCodes.ArchiveInvalid, ("artifact", mod.FullName));
                    }
                    SafeArchive.ValidateEntryNames(entries, mod.FullName);
                }
                generated = RecipeGenerator.Generate(mod.FullName, mod.Version, mod.DownloadUrl, hash,
                    entries, definition, arch);
            }
            var recipe = generated.Recipe;
            if (loader && arch is null) throw new PhaketException("LOADER_ARCH_UNAVAILABLE", ("arch", "unknown"));
            await PackageDownloader.EnsureArtifactsAsync(recipe, modPackages,
                packageBytes is null ? packageTransport : new BufferedPackageTransport(mod.DownloadUrl, packageBytes, packageTransport), cancellationToken: ct);
            var artifact = recipe.Artifacts[0];
            var artifactPath = Path.Combine(modPackages, artifact.Id + (artifact.Format == "zip" ? ".zip" : ""));
            string[] previewFiles;
            long previewSize;
            if (artifact.Format == "zip")
            {
                using var zip = ZipFile.OpenRead(artifactPath);
                // The preview must show what the plan will actually extract: a full package
                // is filtered by the recipe's include list, not by "every archive entry".
                var filters = ArchiveFilters(recipe);
                var previewEntries = zip.Entries.Where(e => !e.FullName.EndsWith('/'))
                    .Where(e => filters.Count == 0 || filters.Any(f => SafeArchive.MatchesInclude(e.FullName, f.Strip, f.Include)))
                    .ToArray();
                previewFiles = previewEntries.Select(e => e.FullName).ToArray();
                previewSize = previewEntries.Sum(e => e.Length);
            }
            else
            {
                previewFiles = DllPreviewFiles(recipe);
                previewSize = new FileInfo(artifactPath).Length;
            }
            // A full package provisions the pinned vanilla version itself, so that is
            // the version fact. A dll addon takes the installation's own build from the
            // registry; without a known build the version stays unknown (PAK-96).
            var gameVersion = mod.Packaging switch
            {
                "full" => installedVersion ?? pinnedVersion,
                "dll" => installedVersion,
                _ => null,
            };
            var facts = new PlanFacts(OperatingSystem.IsWindows() ? "windows" : "linux", target.Store, gameVersion,
                OperationCatalog.Capabilities, uri => VirtualUri.TryParse(uri, out var scheme, out var path)
                    && scheme == "game" && File.Exists(Path.Combine(gameRoot, path)),
                mod.FullName.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase)
                    ? new Dictionary<string, string> { ["bepinex"] = mod.Version } : null,
                Arch: arch, BuildId: buildId);
            var planProfileRoot = Path.Combine(profileRoot, plans.Count.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(planProfileRoot);
            var context = new PlannerContext("install", "mod-profile", gameRoot, planProfileRoot, stagingRoot, modPackages, definition, facts,
                VanillaCacheRoot: Path.Combine(appDataRoot, "vanilla-cache"),
                // PAK-142: the plan compares against the agreed state of THIS installation, so a
                // two-sided edit becomes a per-file decision instead of a silent overwrite.
                Baseline: LoadBaseline(stateRoot));
            var plan = OperationPlanner.Plan(recipe, context, generated.Ownership.ToDictionary(o => o.StepId, o => o.Ownership));
            // Thunderstore v1 publishes no sha256. The hash computed above only pins the bytes
            // already downloaded, so the plan and the registry state that no hash was obtained.
            if (!loader && mod.Sha256 is null)
            {
                plan = plan with { Warnings = [.. plan.Warnings, "THUNDERSTORE_HASH_UNVERIFIED"] };
                unverified.Add(mod.FullName);
            }
            plans.Add(plan);
            planProfileRoots.Add(planProfileRoot);
            packageRoots.Add(modPackages);
            var previewPairs = PreviewEntries(plan, previewFiles, recipe);
            previews.Add(new(mod.FullName, mod.Version, previewPairs.Select(pair => pair.Target).ToArray(),
                previewSize, previewPairs));
        }
        return new(gameId, modFullName, version, endpoint, target, plans, packageRoots, [], previews, root, profileRoot, stagingRoot,
            stateRoot, packages, planProfileRoots, versionSource, unverified.Count == 0 ? null : unverified, gameManifest);
    }

    // The installation's agreed state lives with the ledger in the same state root the
    // executor writes (see InstallBaseline.LoadForPlan for the damaged-file rule).
    internal static InstallBaseline? LoadBaseline(string stateRoot) =>
        InstallBaseline.LoadForPlan(stateRoot, installationId: null);

    // PAK-127: the preview shows the destination the plan writes, never a bare archive entry
    // name (CHANGELOG.md) in place of it. A per-file recipe copies each archive entry through
    // its routed game path; a tree extract filters entries by include/stripComponents and the
    // destination is the stripped entry path.
    internal static IReadOnlyList<InstallPreviewEntry> PreviewEntries(OperationPlan plan,
        IReadOnlyList<string> files, Recipe recipe)
    {
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in plan.Operations)
        {
            // The planned TargetPath is resolved to an absolute host path; the recipe's
            // game-relative "to" parameter is the path the user sees under the game root.
            if (operation.Kind != "file.copy" || operation.Parameters is null
                || !operation.Parameters.TryGetValue("to", out var to)
                || !VirtualUri.TryParse(to, out var toScheme, out var toPath) || toScheme != "game" || toPath == ".")
                continue;
            var source = operation.SourceRef ?? operation.Parameters.GetValueOrDefault("from") ?? "";
            if (VirtualUri.TryParse(source, out var scheme, out var path) && scheme is "staging" or "package")
                source = path.StartsWith("package/", StringComparison.Ordinal) ? path["package/".Length..] : path;
            if (source.Length > 0) copies[source] = toPath;
        }
        var filters = ArchiveFilters(recipe);
        var pairs = new List<InstallPreviewEntry>(files.Count);
        foreach (var file in files)
        {
            if (copies.TryGetValue(file, out var copied)) { pairs.Add(new(file, copied)); continue; }
            var target = file;
            foreach (var (include, strip) in filters)
                if (SafeArchive.MatchesInclude(file, strip, include))
                {
                    target = StripEntry(file, strip) ?? file;
                    break;
                }
            pairs.Add(new(file, target));
        }
        return pairs;
    }

    private static string? StripEntry(string entry, int strip)
    {
        if (strip <= 0) return entry;
        var parts = entry.Replace('\\', '/').Split('/');
        return parts.Length <= strip ? null : string.Join('/', parts.Skip(strip));
    }

    // A dll artifact preview lists the plan's file.copy targets. A malformed step
    // without a "to" must not crash the preview with a raw KeyNotFoundException.
    internal static string[] DllPreviewFiles(Recipe recipe) => recipe.Steps
        .Where(s => s.Use == "file.copy" && s.With.TryGetValue("to", out var to)
            && to.ValueKind == System.Text.Json.JsonValueKind.String
            && to.GetString() is { } target && target.StartsWith("game://", StringComparison.Ordinal))
        .Select(s => s.With["to"].GetString()!["game://".Length..]).ToArray();

    // PAK-96 follow-up: a dll addon with a numeric amongVersion needs the build registry, but
    // only the Builds screen synced it. An empty cache, or a build id the cache does not know,
    // triggers one registry sync here. Offline with an existing cache keeps the cache; without
    // one the version stays unknown and the planner refuses with GAME_BUILD_UNAVAILABLE.
    private async Task<IReadOnlyList<CatalogBuild>> LoadBuildsAsync(string gameId, string store, long? buildId,
        IReadOnlyCollection<string> depotManifests, string endpoint, CancellationToken ct)
    {
        var key = $"{gameId}|{store}|{buildId}|{string.Join(',', depotManifests.Order(StringComparer.Ordinal))}";
        bool Known(IReadOnlyList<CatalogBuild> builds) =>
            GameBuildRegistry.VersionForBuildId(builds, store, buildId) is not null
            || GameBuildRegistry.VersionForManifests(builds, store, depotManifests) is not null;
        try
        {
            using var cache = new CatalogStore(Path.Combine(appDataRoot, "catalog.db"));
            var builds = cache.GetBuilds(gameId);
            if (builds.Count > 0 && Known(builds)) return builds;
            // One sync attempt per installation fact until the TTL expires; a concurrent
            // screen writing the same database must not turn into a raw SqliteException.
            var cachedKey = appDataRoot + "|" + key;
            var now = DateTimeOffset.UtcNow;
            if (UnresolvedBuilds.TryGetValue(cachedKey, out var retryAt) && retryAt > now) return builds;
            UnresolvedBuilds[cachedKey] = now + UnresolvedBuildsTtl;
            if (catalogTransport is { } provided)
            {
                if (!await TrySyncAsync(provided, cache, ct)) return builds;
            }
            else if (CatalogSettings.IsValid(endpoint))
            {
                using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(15) };
                if (!await TrySyncAsync(new HttpCatalogTransport(http), cache, ct)) return builds;
            }
            return cache.GetBuilds(gameId);
        }
        // Fail-safe: keep whatever the cache could give; an empty cache becomes the
        // planner's readable GAME_BUILD_UNAVAILABLE refusal, never a raw SQLite error.
        catch (Microsoft.Data.Sqlite.SqliteException) { return []; }
    }

    // False when the snapshot could not be written (locked database): the caller then
    // keeps the rows it already read instead of failing the whole preparation.
    internal static async Task<bool> TrySyncAsync(ICatalogTransport transport, CatalogStore cache, CancellationToken ct)
    {
        try { await new CatalogClient(transport, cache).FullSyncAsync(ct); return true; }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException
            or Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    public async Task<InstallOutcome> ExecuteAsync(InstallPreparation preparation, bool planAccepted,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default, string? targetProfileId = null,
        // PAK-142: the user's answer for the plans' two-sided conflicts. Without it a plan that
        // has one is refused by the executor before the first write.
        IReadOnlyDictionary<string, ConflictDecision>? decisions = null)
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
                    plan.RecipeRef.Id, "mod-profile", preparation.StateRoot,
                    baseGameSource ?? new ToolBaseGameSource(steamCredentials: steamCredentials,
                        steamPrompt: steamPrompt, epicTokens: epicTokens,
                        mobileConfirmation: steamMobileConfirmation),
                    VanillaCacheRoot: Path.Combine(appDataRoot, "vanilla-cache"),
                    InstallationId: plan.InstallationRef);
                activeAttempt = (plan, ctx, i);
                activeAttemptCompleted = false;
                var journal = new InstallOrchestrator(new PlanExecutor(ctx)).Run(plan, progress, ct,
                    new ReceiptContext(preparation.GameId, preparation.Target.Store, preparation.ModFullName), decisions);
                journals.Add(journal);
                executed.Add((plan, ctx, i));
                activeAttemptCompleted = true;
                new InstallRegistry(Path.Combine(appDataRoot, "installs.json")).Add(Pinned(new InstalledRecipe(plan.RecipeRef.Id,
                    plan.RecipeRef.Version, preparation.GameId, preparation.Target.Store, ctx.GameRoot, ctx.ProfileRoot,
                    ctx.StagingRoot, preparation.StateRoot, preparation.Target.Sandbox ? preparation.SandboxRoot : null, DateTimeOffset.UtcNow,
                    preparation.Preview[i].FullName, preparation.Preview[i].Version,
                    preparation.UnverifiedPackages?.Contains(preparation.Preview[i].FullName, StringComparer.OrdinalIgnoreCase) == true
                        ? "unverified" : null), preparation.Manifest));
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
                return new(false, code, journals, (ex as PhaketException)?.Error.Parameters);
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

    // archive.extract steps that read the downloaded package: their include/strip pair is
    // what the plan applies, so the preview applies the same one.
    private static IReadOnlyList<(IReadOnlyList<string>? Include, int Strip)> ArchiveFilters(Recipe recipe) =>
        recipe.Steps.Where(step => step.Use == "archive.extract"
                && step.With.TryGetValue("from", out var from) && from.GetString() == "package://package")
            .Select(step =>
            {
                var include = step.With.TryGetValue("include", out var element) && element.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? (IReadOnlyList<string>)element.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                    : null;
                var strip = step.With.TryGetValue("stripComponents", out var raw) && raw.TryGetInt32(out var value) ? value : 0;
                return (include, strip);
            }).ToArray();

    // PAK-144: a modded installation is pinned against the build it was installed on;
    // sandbox installs are throwaway and carry no store version worth pinning.
    private static InstalledRecipe Pinned(InstalledRecipe entry, SteamAppManifest? manifest) =>
        entry.SandboxRoot is not null ? entry : entry with
        {
            PinnedAt = DateTimeOffset.UtcNow,
            PinnedBuildId = manifest?.BuildId is { Length: > 0 } build ? build : null,
            PinnedManifestId = manifest?.ManifestId is { Length: > 0 } id ? id : null,
        };

    // A source row can say the version is gone; a row with neither a URL nor artifacts has
    // nothing to download either. Both are refused before any plan or download is made.
    private static bool IsUnavailable(PinnedMod mod) =>
        string.Equals(mod.Availability, "unavailable", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrWhiteSpace(mod.DownloadUrl) && mod.Artifacts is not { Count: > 0 });

    private static string SafeFile(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    // Streams directly to disk: full-game ZIPs are hundreds of MB and must not be
    // buffered in memory. The returned hash is of the bytes actually received.
    // PAK-129: installs.json only locates an installation. A dependency entry whose files were
    // removed by hand must be planned again, so the ledger's resources are confirmed on disk.
    private static bool IsInstalledWithFiles(InstalledRecipe entry)
    {
        try
        {
            string ledgerPath = Path.Combine(entry.StateRoot, "ledger.json");
            if (!File.Exists(ledgerPath)) return false;
            foreach (LedgerEntry resource in OwnershipLedger.Load(ledgerPath).ForRecipe(entry.RecipeId))
            {
                string path = resource.Resource;
                if (path.Contains("::", StringComparison.Ordinal)) continue; // a config key, not a file
                if (path.StartsWith("staging/", StringComparison.Ordinal)) path = Path.Combine(entry.StagingRoot, path[8..]);
                else if (path.StartsWith("profile/", StringComparison.Ordinal)) path = Path.Combine(entry.ProfileRoot, path[8..]);
                else if (path.StartsWith('/')) continue; // outside every root: not verifiable here
                else path = Path.Combine(entry.GameRoot, path);
                if (!File.Exists(path.Replace('/', Path.DirectorySeparatorChar))) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static async Task<(string Sha256, long Length)> DownloadAsync(IPackageTransport transport, string url,
        string path, long? expectedSize, CancellationToken ct)
    {
        // The declared size is catalog metadata: it sets the expected length, but never above the
    // 2 GiB policy cap (PAK-129).
    var limit = Math.Min(expectedSize ?? 2L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024);
        var temp = path + ".phaket-tmp";
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await transport.OpenReadAsync(url, ct))
            await using (var destination = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    received += read;
                    if (received > limit)
                        throw new PhaketException(ErrorCodes.ArtifactSizeMismatch, ("artifact", url),
                            ("actual", received.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            ("expected", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    hasher.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            File.Move(temp, path, overwrite: true);
            return (Convert.ToHexStringLower(hasher.GetHashAndReset()), received);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort temp cleanup */ }
            throw;
        }
    }

    private sealed class BufferedPackageTransport(string url, byte[] bytes, IPackageTransport fallback) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken) => source == url
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : fallback.OpenReadAsync(source, cancellationToken);
    }

    private sealed class CatalogVersionSource(ModCatalogClient client, IReadOnlyDictionary<string, ThunderstoreMod> mods,
        IReadOnlyDictionary<string, ThunderstoreMod> byId, string? modId) : IModVersionSource
    {
        public async Task<ThunderstoreModDetails?> GetDetailsAsync(string fullName, CancellationToken cancellationToken)
        {
            // The caller can name the exact catalog row (a composition item), which matters
            // when another source publishes the same full name.
            var mod = modId is not null && byId.TryGetValue(modId, out var pinned)
                && string.Equals(pinned.FullName, fullName, StringComparison.OrdinalIgnoreCase) ? pinned
                : mods.TryGetValue(fullName, out var byName) ? byName : null;
            if (mod is null) return null;
            try { return await client.GetModAsync(mod.Id, cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
        }
    }
}
