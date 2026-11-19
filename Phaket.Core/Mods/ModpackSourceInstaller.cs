using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Mods;

// Source fields mirror the desktop pickers while keeping Core independent of the UI assembly.
public sealed record ModpackSourceItem(
    string Source, string Name, string? Version = null,
    string? Repo = null, string? Tag = null, long? AssetId = null,
    string? AssetName = null, string? DownloadUrl = null, long? Size = null, string? Sha256 = null,
    string? Url = null, string? PinnedVersion = null, string? LocalArchivePath = null, bool IsDemo = false,
    string? Link = null, string? ManualPath = null);

public sealed record ModpackSourceInstallContext(
    string InstallationId, string ProfileId, string GameRoot, string ProfileRoot, string StagingRoot,
    string PackageRoot, string StateRoot, GameDefinition Game, PlanFacts Facts,
    IReadOnlyList<ResourceOwner>? ExistingOwners = null);

public sealed record PlannedModpackSource(
    string Name, string Version, OperationPlan Plan, PlanExecutionContext ExecutionContext);

public sealed record ModpackSourceInstallPlan(IReadOnlyList<PlannedModpackSource> Items)
{
    public bool CanInstall => Items.Count > 0 && Items.All(item => item.Plan.Operations.Count > 0);
    public IReadOnlyList<string> Warnings => Items.SelectMany(item => item.Plan.Warnings).Distinct(StringComparer.Ordinal).ToArray();
}

public sealed record ModpackSourceInstallOutcome(bool Success, string? ErrorCode, IReadOnlyList<ExecutionJournal> Journals);

// Pins GitHub assets and local Nexus ZIPs into the existing planner/executor transaction flow.
public sealed class ModpackSourceInstaller
{
    private const string ArtifactId = "package";
    private readonly IPackageTransport transport;
    private readonly TimeSpan downloadTimeout;
    private readonly TimeProvider timeProvider;

    public ModpackSourceInstaller(IPackageTransport? transport = null, TimeSpan? downloadTimeout = null,
        TimeProvider? timeProvider = null)
    {
        this.transport = transport ?? new HttpPackageTransport();
        this.downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(10);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (this.downloadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
    }

    public async Task<ModpackSourceInstallPlan> PrepareAsync(IReadOnlyList<ModpackSourceItem> items,
        ModpackSourceInstallContext context, CancellationToken cancellationToken = default)
    {
        if (items.Count is 0 or > 500)
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "modpack"), ("detail", "expected 1 to 500 items"));

        var packageRoot = Path.GetFullPath(context.PackageRoot);
        var profileRoot = Path.GetFullPath(context.ProfileRoot);
        var stagingRoot = Path.GetFullPath(context.StagingRoot);
        var gameRoot = Path.GetFullPath(context.GameRoot);
        var planned = new List<PlannedModpackSource>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            var itemPackageRoot = Path.Combine(packageRoot, i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            var itemProfileRoot = Path.Combine(profileRoot, i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            var itemStagingRoot = Path.Combine(stagingRoot, i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(itemPackageRoot);

            string name, version, sourceRef;
            string? sha256;
            long? size;
            string archivePath;
            if (item.Source == "github")
            {
                (name, version) = await DownloadGitHubAsync(item, itemPackageRoot, cancellationToken);
                sourceRef = item.DownloadUrl!;
                sha256 = item.Sha256;
                size = item.Size;
                archivePath = Path.Combine(itemPackageRoot, ArtifactId + ".zip");
            }
            else if (item.Source == "nexus")
            {
                (name, version, archivePath) = await StageNexusAsync(item, itemPackageRoot, cancellationToken);
                sourceRef = "package://package.zip";
                sha256 = HashFile(archivePath);
                size = new FileInfo(archivePath).Length;
            }
            else
            {
                throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", item.Source), ("detail", "only github and nexus are handled by this Core contract"));
            }

            var archiveEntries = ReadAndValidateZip(archivePath, Path.Combine(itemStagingRoot, "package"), item.Name);
            if (archiveEntries.Count == 0)
                throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", item.Source), ("detail", "ZIP contains no files"));

            var recipeId = RecipeId(item, name, version);
            var generated = RecipeGenerator.GenerateModpackPackage(item.Source, name, version, sourceRef,
                sha256, size, archiveEntries, context.Game, recipeId, item.Repo, item.Tag, item.AssetName);
            var planner = new PlannerContext(context.InstallationId, context.ProfileId, gameRoot, itemProfileRoot,
                itemStagingRoot, itemPackageRoot, context.Game, context.Facts, context.ExistingOwners);
            var plan = OperationPlanner.Plan(generated.Recipe, planner,
                generated.Ownership.ToDictionary(owner => owner.StepId, owner => owner.Ownership));
            var execution = new PlanExecutionContext(gameRoot, itemProfileRoot, itemStagingRoot,
                itemPackageRoot, context.Game, recipeId, context.ProfileId, context.StateRoot);
            planned.Add(new PlannedModpackSource(name, version, plan, execution));
        }
        return new ModpackSourceInstallPlan(planned);
    }

    // A false approval never reaches the filesystem executor.
    public ModpackSourceInstallOutcome Execute(ModpackSourceInstallPlan plan, bool planAccepted,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!planAccepted) throw new PhaketException(ErrorCodes.PlanNotAccepted);
        if (!plan.CanInstall) throw new PhaketException(ErrorCodes.PlanEmpty);

        var completed = new List<PlannedModpackSource>();
        var journals = new List<ExecutionJournal>();
        PlannedModpackSource? active = null;
        try
        {
            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                active = item;
                var journal = new InstallOrchestrator(new PlanExecutor(item.ExecutionContext)).Run(item.Plan, progress, cancellationToken);
                journals.Add(journal);
                completed.Add(item);
                active = null;
            }
            return new(true, null, journals);
        }
        catch (OperationCanceledException)
        {
            Rollback(active, completed, journals);
            throw;
        }
        catch (Exception ex)
        {
            Rollback(active, completed, journals);
            return new(false, ex is PhaketException phaket ? phaket.Error.Code : ex.GetType().Name, journals);
        }
    }

    private async Task<(string Name, string Version)> DownloadGitHubAsync(ModpackSourceItem item,
        string packageRoot, CancellationToken cancellationToken)
    {
        var repo = item.Repo?.Trim();
        var tag = item.Tag?.Trim();
        var assetName = item.AssetName?.Trim();
        var name = string.IsNullOrWhiteSpace(item.Name) && assetName is not null
            ? Path.GetFileNameWithoutExtension(assetName) : item.Name?.Trim() ?? "";
        if (item.IsDemo || !SourcePolicy.IsValidGitHubRepository(repo) || string.IsNullOrWhiteSpace(tag)
            || item.AssetId is null or <= 0 || string.IsNullOrWhiteSpace(assetName)
            || assetName.IndexOfAny(['/', '\\']) >= 0 || assetName.Any(char.IsControl)
            || !Path.GetExtension(assetName).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || item.Size is <= 0 || item.Sha256 is not null && !Regex.IsMatch(item.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "GitHub"), ("detail", "invalid pinned repository, tag, asset, size, or checksum"));

        SourcePolicy.AllowGitHubRepository(repo!);
        if (!SourcePolicy.IsGitHubAssetUrl(item.DownloadUrl ?? ""))
            throw new PhaketException(ErrorCodes.ModSourceHostNotAllowed,
                ("host", Uri.TryCreate(item.DownloadUrl, UriKind.Absolute, out var rejected) ? rejected.Host : "invalid"));
        var downloadRecipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1", Id = RecipeId(item, name, tag!), Version = RecipeVersion(tag!),
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Artifacts = [new RecipeArtifact { Id = ArtifactId, Source = item.DownloadUrl!, Sha256 = item.Sha256, SizeBytes = item.Size }],
            Steps = []
        };
        try
        {
            using var timeout = new CancellationTokenSource(downloadTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await PackageDownloader.EnsureArtifactsAsync(downloadRecipe, packageRoot, transport, cancellationToken: linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhaketException(ErrorCodes.ArtifactDownloadTimeout, ("artifact", assetName));
        }
        catch (HttpRequestException ex)
        {
            throw new PhaketException(ErrorCodes.ArtifactDownloadUnavailable, ("artifact", assetName), ("detail", ex.Message));
        }
        return (name, tag!);
    }

    // Delegate to the single semver-folding rule so symbol-only tags get a valid "0.0.0-pinned" instead of a trailing "-".
    private static string RecipeVersion(string pinnedTag) => RecipeGenerator.ToRecipeVersion(pinnedTag);

    private static async Task<(string Name, string Version, string ArchivePath)> StageNexusAsync(
        ModpackSourceItem item, string packageRoot, CancellationToken cancellationToken)
    {
        var name = item.Name?.Trim() ?? "";
        var version = item.PinnedVersion?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 160 || version.Length == 0 || version.Length > 100
            || !IsNexusUrl(item.Url ?? item.Link) || string.IsNullOrWhiteSpace(item.LocalArchivePath ?? item.ManualPath))
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "Nexus"), ("detail", "invalid mod name, pinned version, URL, or local archive path"));

        string sourcePath;
        try { sourcePath = Path.GetFullPath(item.LocalArchivePath ?? item.ManualPath!); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "Nexus"), ("detail", "invalid local archive path"));
        }
        if (!File.Exists(sourcePath))
            throw new PhaketException(ErrorCodes.ArtifactMissing, ("artifact", "Nexus local archive"));
        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "Nexus"), ("detail", "archive extension must be zip, 7z, or rar"));
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new PhaketException(ErrorCodes.ArchiveFormatUnsupported, ("artifact", Path.GetFileName(sourcePath)), ("format", extension));

        var destination = Path.Combine(packageRoot, ArtifactId + ".zip");
        var temp = destination + ".phaket-tmp";
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(temp, destination, overwrite: true);
        }
        catch (OperationCanceledException) { TryDelete(temp); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new PhaketException(ErrorCodes.ArtifactMissing, ("artifact", "Nexus local archive"));
        }
        return (name, version, destination);
    }

    private static IReadOnlyList<string> ReadAndValidateZip(string path, string targetRoot, string artifact)
    {
        try
        {
            _ = SafeArchive.PlanWrites(path, targetRoot, artifact);
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Select(entry => entry.FullName).ToArray();
        }
        catch (PhaketException) { throw; }
        catch (InvalidDataException)
        {
            throw new PhaketException(ErrorCodes.ArchiveInvalid, ("artifact", artifact));
        }
        catch (IOException)
        {
            throw new PhaketException(ErrorCodes.ArchiveInvalid, ("artifact", artifact));
        }
    }

    private void Rollback(PlannedModpackSource? active, List<PlannedModpackSource> completed,
        List<ExecutionJournal> journals)
    {
        if (active is not null)
        {
            try
            {
                var journal = PlanExecutor.LoadJournal(active.ExecutionContext.ProfileRoot);
                if (journal is not null && journal.State != JournalState.RolledBack)
                {
                    var rolled = new PlanExecutor(active.ExecutionContext).Rollback(journal, DivergencePolicy.Skip);
                    ReplaceJournal(journals, active.Plan.PlanId, rolled);
                }
            }
            catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Modpack rollback failed: {0}", rollbackError); }
        }
        foreach (var item in completed.AsEnumerable().Reverse())
        {
            try { new PlanExecutor(item.ExecutionContext).Uninstall(item.Plan.RecipeRef.Id, DivergencePolicy.Skip); }
            catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Modpack rollback failed: {0}", rollbackError); }
        }
    }

    private static void ReplaceJournal(List<ExecutionJournal> journals, string planId, ExecutionJournal journal)
    {
        var index = journals.FindIndex(item => item.PlanId == planId);
        if (index >= 0) journals[index] = journal;
        else journals.Add(journal);
    }

    private static string RecipeId(ModpackSourceItem item, string name, string version)
    {
        var identity = string.Join('\n', item.Source, name, version, item.Repo, item.Tag, item.AssetId, item.Url ?? item.Link);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        return $"modpack.{item.Source}.{hash}";
    }

    private static bool IsNexusUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Contains("@", StringComparison.Ordinal)
            || !(uri.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase))) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = parts.Length >= 3 && parts[1] == "mods" ? 2
            : parts.Length >= 4 && parts[0] == "games" && parts[2] == "mods" ? 3 : -1;
        return index >= 0 && long.TryParse(parts[index], out var id) && id > 0;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
