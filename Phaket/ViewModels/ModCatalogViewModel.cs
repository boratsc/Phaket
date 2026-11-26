using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Accounts;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class ModCatalogViewModel : ViewModelBase, IDisposable
{
    private readonly string gameId, cachePath, appDataRoot;
    private readonly Action? installedChanged;
    private InstallPreparation? preparation;
    private string requestedName = "", requestedVersion = "";
    private InstallTarget target = new("", "steam", true);
    private readonly Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase);
    internal Func<string, string, string, InstallTarget, IReadOnlyDictionary<string, string>?, Task<InstallPreparation>>? PrepareOverride;
    internal Func<InstallPreparation, bool, IProgress<InstallProgress>, Task<InstallOutcome>>? ExecuteOverride;
    private readonly ICatalogTransport? transport;
    private readonly SemaphoreSlim gate = new(1, 1);
    // A page replaced by a game switch stops its own refresh instead of writing into a dead view.
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private IReadOnlyList<ThunderstoreMod> all = [];
    private int selectionGeneration;
    public ModCatalogViewModel(string gameId, string appDataRoot, ICatalogTransport? transport = null, bool autoSync = true, Action? installedChanged = null, GameCatalogItem? game = null)
    {
        this.gameId = gameId; this.transport = transport; this.appDataRoot = appDataRoot; this.installedChanged = installedChanged;
        cachePath = Path.Combine(appDataRoot, "modcatalog.db");
        Endpoint = CatalogSettings.Load(CatalogSettings.SettingsFile(appDataRoot));
        Targets.Add(new InstallTargetChoice(LocalizationService.Instance["ModInstall.Sandbox"], new("", "steam", true)));
        if (game is not null)
        {
            foreach (var installation in game.AvailableInstallations)
                Targets.Add(new InstallTargetChoice(installation.Label, new(installation.RootPath, installation.Store, false)));
        }
        SelectedTarget = Targets[0];
        SelectedArch = game?.Definition.DefaultArch;
        if (autoSync) _ = RefreshCommand.ExecuteAsync(null);
    }
    public ObservableCollection<ThunderstoreMod> Mods { get; } = [];
    // Only the owner (page or pack editor) knows when this view model is detached.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
    // PAK-101: the browser shows both catalog sources; filtering never hides the source fact.
    [ObservableProperty] private string sourceFilter = "all";
    public bool IsAllSource => SourceFilter == "all";
    public bool IsThunderstoreSource => SourceFilter == "thunderstore";
    public bool IsSusmodderSource => SourceFilter == "susmodder";
    // The selected mod's versions with the artifact variant the planner would pick (PAK-101).
    public ObservableCollection<CatalogVersionItem> Versions { get; } = [];
    [ObservableProperty] private string endpoint = CatalogSettings.DefaultEndpoint;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string detailsStatus = "";
    [ObservableProperty] private bool isOffline;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string lastSyncText = "—";
    [ObservableProperty] private ThunderstoreMod? selectedMod;
    [ObservableProperty] private ThunderstoreModDetails? details;
    public bool HasDetails => Details is not null;
    partial void OnDetailsChanged(ThunderstoreModDetails? value) { OnPropertyChanged(nameof(HasDetails)); RebuildVersions(); }
    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnSourceFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsAllSource));
        OnPropertyChanged(nameof(IsThunderstoreSource));
        OnPropertyChanged(nameof(IsSusmodderSource));
        Filter();
    }
    [RelayCommand]
    private void SetSourceFilter(string? source) => SourceFilter = source is "thunderstore" or "susmodder" ? source : "all";
    partial void OnSelectedModChanged(ThunderstoreMod? value)
    {
        var generation = ++selectionGeneration;
        Details = null; DetailsStatus = "";
        if (value is not null) PendingDetails = LoadDetailsAsync(value, generation);
    }
    // Tests must close the cache handle before deleting their fixture directory (Windows).
    internal Task? PendingDetails { get; private set; }
    internal string? LatestVersion(string fullName) =>
        all.Concat(Mods).FirstOrDefault(m => string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase))
            is { Latest.IsAvailable: true } mod ? mod.Latest.VersionNumber : null;
    private void Filter()
    {
        var q = SearchText.Trim();
        var selected = SelectedMod;
        Mods.Clear();
        foreach (var mod in all)
            if ((q.Length == 0 || mod.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || mod.Owner.Contains(q, StringComparison.OrdinalIgnoreCase))
                && (SourceFilter == "all" || string.Equals(mod.Source ?? "thunderstore", SourceFilter, StringComparison.OrdinalIgnoreCase))) Mods.Add(mod);
        IsEmpty = Mods.Count == 0;
        if (selected is not null && Mods.Contains(selected)) SelectedMod = selected;
    }
    // The artifact the planner will use for a chosen sandbox arch/store; a real installation's
    // arch is probed at plan time, so there is no variant to name before the plan exists.
    private void RebuildVersions()
    {
        Versions.Clear();
        if (Details is null || SelectedMod is null) return;
        var arch = SelectedTarget?.Target.Sandbox == true ? SelectedArch : null;
        var store = SelectedTarget?.Target.Store ?? "steam";
        foreach (var version in Details.Versions)
            Versions.Add(arch is null
                ? new(version, null, false)
                : DescribeVariant(version, SelectedMod.FullName, arch, store));
    }
    private static CatalogVersionItem DescribeVariant(ThunderstoreVersion version, string fullName, string arch, string store)
    {
        if (version.Artifacts is not { Count: > 0 }) return new(version, null, false);
        try
        {
            var selected = ModArtifactSelector.Select(version, fullName, arch, store);
            var artifact = version.Artifacts.FirstOrDefault(a => a.Url == selected.Url);
            var parts = new List<string>();
            if (selected.Arch is { Length: > 0 } selectedArch) parts.Add(selectedArch);
            if (selected.Stores is { Count: > 0 } stores) parts.Add(string.Join("/", stores));
            if (artifact?.Source is { Length: > 0 } source) parts.Add(source);
            if (selected.SizeBytes is { } size) parts.Add((size / 1024d / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " MB");
            return new(version, string.Join(" · ", parts), false);
        }
        catch (PhaketException) { return new(version, null, true); }
    }
    private HttpClient CreateHttp() {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) throw new UriFormatException();
        return new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15) };
    }
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await gate.WaitAsync();
        try
        {
            var token = lifetime.Token;
            var result = await Task.Run(async () => {
                IReadOnlyList<ThunderstoreMod> mods = []; DateTimeOffset? last = null; string? error = null; bool offline = false;
                try {
                    using var store = new ModCatalogStore(cachePath);
                    try {
                        using var http = transport is null ? CreateHttp() : null;
                        await new ModCatalogClient(transport ?? new HttpCatalogTransport(http!), store).SyncAsync(gameId, token);
                    } catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException) { offline = true; error = "Catalog.Offline"; }
                    catch (UriFormatException) { error = "Builds.InvalidEndpoint"; }
                    catch (Exception) { error = "Builds.SyncError"; }
                    mods = store.GetMods(gameId); last = store.LastSync;
                } catch (Exception) { error = "Builds.CacheError"; }
                return (mods, last, error, offline);
            });
            await Dispatcher.UIThread.InvokeAsync(() => { all = result.mods; Filter(); IsOffline = result.offline; StatusMessage = result.error is null ? "" : LocalizationService.Instance[result.error]; LastSyncText = result.last?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—"; });
        }
        finally { gate.Release(); }
    }
    private async Task LoadDetailsAsync(ThunderstoreMod mod, int generation)
    {
        await gate.WaitAsync();
        try {
            var token = lifetime.Token;
            var result = await Task.Run(async () => {
                try {
                    using var store = new ModCatalogStore(cachePath);
                    using var http = transport is null ? CreateHttp() : null;
                    return await new ModCatalogClient(transport ?? new HttpCatalogTransport(http!), store).GetModAsync(mod.Id, token);
                } catch (Exception) { return null; }
            });
            await Dispatcher.UIThread.InvokeAsync(() => { if (generation != selectionGeneration) return; Details = result; DetailsStatus = result is null ? LocalizationService.Instance["ModCatalog.DetailsUnavailable"] : ""; });
        } finally { gate.Release(); }
    }
    [ObservableProperty] private bool planAccepted;
    [ObservableProperty] private bool isPreparing;
    [ObservableProperty] private string installStatus = "";
    [ObservableProperty] private double installProgress;
    [ObservableProperty] private bool hasPreview;
    [ObservableProperty] private bool hasConflicts;
    [ObservableProperty] private string targetDescription = "";
    [ObservableProperty] private bool isSandboxTarget;
    public ObservableCollection<InstallTargetChoice> Targets { get; } = [];
    [ObservableProperty] private InstallTargetChoice? selectedTarget;
    partial void OnSelectedTargetChanged(InstallTargetChoice? value) { OnPropertyChanged(nameof(SandboxSelected)); if (!IsPreparing) ClearInstall(); RebuildVersions(); }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (IsPreparing) return;
        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var storage = desktop?.MainWindow?.StorageProvider;
        if (storage is null) return;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                { Title = LocalizationService.Instance["ModInstall.ChooseFolder"], AllowMultiple = false });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            var choice = new InstallTargetChoice($"Steam — {path}", new(path, "steam", false));
            Targets.Add(choice); SelectedTarget = choice;
        }
        catch (Exception) { InstallStatus = LocalizationService.Instance["ModInstall.FolderFailed"]; }
    }
    // PAK-65: a sandbox has no binary to probe, so its architecture is an explicit choice that
    // defaults to the game definition's current retail build. Real installations keep the
    // service-side PE probe; the choice is not stamped onto them.
    public ObservableCollection<string> Architectures { get; } = ["x64", "x86"];
    [ObservableProperty] private string? selectedArch;
    // Only a sandbox needs a declared architecture; a real install is probed from its binary.
    public bool SandboxSelected => SelectedTarget?.Target.Sandbox == true;
    partial void OnSelectedArchChanged(string? value) { if (!IsPreparing) ClearInstall(); RebuildVersions(); }

    public ObservableCollection<InstallPreview> Preview { get; } = [];
    public ObservableCollection<PlannedOperation> Operations { get; } = [];
    public ObservableCollection<string> PlanWarnings { get; } = [];
    public ObservableCollection<ConflictProposal> Conflicts { get; } = [];
    partial void OnPlanAcceptedChanged(bool value) => ExecuteInstallCommand.NotifyCanExecuteChanged();
    partial void OnHasPreviewChanged(bool value) => ExecuteInstallCommand.NotifyCanExecuteChanged();
    partial void OnIsPreparingChanged(bool value) => ExecuteInstallCommand.NotifyCanExecuteChanged();
    private bool CanExecuteInstall() => PlanAccepted && HasPreview && !IsPreparing && preparation is not null;

    [RelayCommand]
    private async Task InstallVersionAsync(ThunderstoreVersion? version)
    {
        if (version is null || SelectedMod is null) return;
        if (SelectedTarget is null) return;
        // PAK-101: an unavailable version stays listed, but must not start a plan that cannot
        // download anything; the reason is shown next to the disabled button.
        if (!version.IsAvailable)
        {
            InstallStatus = LocalizationService.Instance["ModInstall.VersionUnavailable"];
            return;
        }
        await BeginInstallAsync(SelectedMod.FullName, version.VersionNumber, SelectedTarget.Target);
    }
    internal async Task BeginInstallAsync(string name, string version, InstallTarget installTarget)
    {
        if (IsPreparing) return;
        requestedName = name; requestedVersion = version; target = installTarget; overrides.Clear();
        await PrepareInstallAsync();
    }
    private async Task PrepareInstallAsync()
    {
        PlanAccepted = false; HasPreview = false; HasConflicts = false; preparation = null;
        Preview.Clear(); Operations.Clear(); PlanWarnings.Clear(); Conflicts.Clear(); InstallStatus = ""; IsPreparing = true;
        try
        {
            // Only the sandbox has no binary to probe, so only there the chosen architecture is a fact.
            var installTarget = target.Sandbox && SelectedArch is { } chosen ? target with { Arch = chosen } : target;
            InstallPreparation result;
            if (PrepareOverride is not null) result = await PrepareOverride(requestedName, requestedVersion, Endpoint, installTarget, overrides);
            else
            {
                using var http = transport is null ? CreateHttp() : null;
                using var db = new ModCatalogStore(Path.Combine(appDataRoot, "catalog.db"));
                var service = new ModInstallService(appDataRoot, new ModCatalogClient(transport ?? new HttpCatalogTransport(http!), db));
                result = await service.PrepareAsync(gameId, requestedName, requestedVersion, Endpoint, installTarget, overrides);
            }
            foreach (var conflict in result.Conflicts) Conflicts.Add(conflict);
            HasConflicts = Conflicts.Count > 0;
            // PAK-65: a recipe that matched no operations must not offer an "install" that
            // changes nothing; there is nothing to accept and nothing to register.
            if (!HasConflicts && result.Plans.Count > 0 && result.Plans.Any(p => p.Operations.Count == 0))
            {
                InstallStatus = ErrorMessages.Format(ErrorCodes.PlanEmpty, LocalizationService.Instance.CurrentLanguage);
                return;
            }
            if (!HasConflicts)
            {
                preparation = result;
                foreach (var package in result.Preview) Preview.Add(package);
                var warnings = result.Plans.SelectMany(p => p.Warnings).Distinct(StringComparer.Ordinal).ToArray();
                var loc = LocalizationService.Instance;
                foreach (var warning in warnings.Where(w => !w.StartsWith("executable-library:", StringComparison.Ordinal)))
                {
                    var text = warning switch
                    {
                        "ARCH_UNKNOWN" => loc["ModInstall.WarningArchUnknown"],
                        "BUILD_UNKNOWN" => loc["ModInstall.WarningBuildUnknown"],
                        "loader-overwrite-unknown" => loc["ModInstall.WarningLoaderOverwriteUnknown"],
                        "THUNDERSTORE_HASH_UNVERIFIED" => ErrorMessages.Format("THUNDERSTORE_HASH_UNVERIFIED", loc.CurrentLanguage),
                        _ => loc["ModInstall.WarningOther"] + " " + warning
                    };
                    PlanWarnings.Add(text);
                }
                // One DLL pack lists a dozen executable libraries; one counted line is the
                // warning, thirteen identical entries are noise.
                var executables = warnings.Count(w => w.StartsWith("executable-library:", StringComparison.Ordinal));
                if (executables > 0) PlanWarnings.Add(string.Format(loc["ModInstall.WarningExecutableLibrary"], executables));
                IsSandboxTarget = result.Target.Sandbox;
                TargetDescription = result.Target.Sandbox ? Path.Combine(result.SandboxRoot, "game") : result.Target.GameRoot;
                foreach (var operation in result.Plans.SelectMany(p => p.Operations))
                    Operations.Add(operation.TargetPath.StartsWith("game://", StringComparison.OrdinalIgnoreCase)
                        ? operation with { TargetPath = Path.Combine(TargetDescription, operation.TargetPath[7..].Replace('/', Path.DirectorySeparatorChar)) }
                        : operation);
                HasPreview = true;
            }
        }
        catch (PhaketException ex) when (ex.Error.Code == "LOADER_ARCH_UNAVAILABLE") { InstallStatus = LocalizationService.Instance["ModInstall.LoaderArchUnavailable"]; }
        // The install service refuses a version the source marked unavailable; say why
        // instead of a generic prepare failure (the catalog button has its own gate).
        catch (PhaketException ex) when (ex.Error.Code == ErrorCodes.ModArtifactUnavailable)
        { InstallStatus = ex.LocalizedMessage(LocalizationService.Instance.CurrentLanguage); }
        // The planner refused because a needed fact (arch/build) is unknown: say which one.
        catch (PhaketException ex) when (ex.Error.Code == ErrorCodes.InstallFactsMissing)
        { InstallStatus = ex.LocalizedMessage(LocalizationService.Instance.CurrentLanguage); }
        // PAK-129: an artifact whose real size differs from the catalog's is a size mismatch,
        // not a generic prepare failure; the message names both sizes.
        catch (PhaketException ex) when (ex.Error.Code == ErrorCodes.ArtifactSizeMismatch)
        { InstallStatus = ex.LocalizedMessage(LocalizationService.Instance.CurrentLanguage); }
        catch (Exception) { InstallStatus = LocalizationService.Instance["ModInstall.PrepareFailed"]; }
        finally { IsPreparing = false; }
    }
    [RelayCommand]
    private async Task UseSuggestedAsync()
    {
        if (IsPreparing || !HasConflicts) return;
        foreach (var conflict in Conflicts) overrides[conflict.Package] = conflict.SuggestedVersion;
        await PrepareInstallAsync();
    }
    [RelayCommand]
    private void CancelInstall()
    {
        if (IsPreparing) return;
        ClearInstall();
    }
    private void ClearInstall()
    {
        PlanAccepted = false; HasPreview = false; HasConflicts = false; preparation = null;
        Preview.Clear(); Operations.Clear(); PlanWarnings.Clear(); Conflicts.Clear();
    }
    [RelayCommand(CanExecute = nameof(CanExecuteInstall))]
    private async Task ExecuteInstallAsync()
    {
        if (!CanExecuteInstall()) return;
        var acceptedPlan = preparation!; IsPreparing = true; InstallStatus = ""; InstallProgress = 0;
        try
        {
            var progress = new Progress<InstallProgress>(p => InstallProgress = p.Fraction * 100);
            InstallOutcome result;
            if (ExecuteOverride is not null) result = await ExecuteOverride(acceptedPlan, true, progress);
            else
            {
                // PAK-102: a full package provisions the pinned vanilla; Steam auth uses the
                // same store and window-level dialog as the library install. The context is
                // captured here (UI thread) because the executor runs on a worker thread.
                var context = SynchronizationContext.Current;
                var prompt = SteamPromptViewModel.Shared;
                using var http = transport is null ? CreateHttp() : null;
                using var db = new ModCatalogStore(Path.Combine(appDataRoot, "catalog.db"));
                // Epic provisions reuse the PAK-58 token file in a private legendary config;
                // an unsigned account is refused with the sign-in instruction.
                var epicTokens = LegendaryTokenProvider.From(new EpicAccountService());
                var service = new ModInstallService(appDataRoot, new ModCatalogClient(transport ?? new HttpCatalogTransport(http!), db),
                    steamCredentials: new SteamCredentialStore(), steamPrompt: (p, ct) => prompt.RequestAsync(p, ct, context),
                    epicTokens: epicTokens, steamMobileConfirmation: SteamPromptViewModel.MobileConfirmationSink());
                result = await Task.Run(() => service.ExecuteAsync(acceptedPlan, true, progress));
            }
            var loc = LocalizationService.Instance;
            InstallStatus = result.Success ? loc["ModInstall.Success"]
                : result.ErrorCode is { } code && ErrorMessages.TryGetTemplate(code, loc.CurrentLanguage, out _)
                    ? ErrorMessages.Format(code, loc.CurrentLanguage, result.ErrorParameters)
                    : loc["ModInstall.Failed"];
            ClearInstall(); installedChanged?.Invoke();
        }
        catch (Exception) { ClearInstall(); installedChanged?.Invoke(); InstallStatus = LocalizationService.Instance["ModInstall.Failed"]; }
        finally { IsPreparing = false; }
    }
    [RelayCommand]
    private void OpenPackage()
    {
        if (SelectedMod is null || !Uri.TryCreate(SelectedMod.PackageUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { DetailsStatus = LocalizationService.Instance["ModCatalog.OpenFailed"]; }
    }
}

public sealed record InstallTargetChoice(string Label, InstallTarget Target);

// One version row in the catalog details: the server's availability plus the artifact
// variant the planner would pick for the current sandbox target (null when not determined,
// VariantMissing when the version has artifacts but none matches).
public sealed record CatalogVersionItem(ThunderstoreVersion Version, string? VariantText, bool VariantMissing)
{
    public bool IsAvailable => Version.IsAvailable;
    public bool HasVariant => VariantText is not null;
}
