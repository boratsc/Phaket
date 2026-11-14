using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class ModCatalogViewModel : ViewModelBase
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
    private IReadOnlyList<ThunderstoreMod> all = [];
    private int selectionGeneration;
    public ModCatalogViewModel(string gameId, string appDataRoot, ICatalogTransport? transport = null, bool autoSync = true, Action? installedChanged = null, GameCatalogItem? game = null)
    {
        this.gameId = gameId; this.transport = transport; this.appDataRoot = appDataRoot; this.installedChanged = installedChanged;
        cachePath = Path.Combine(appDataRoot, "modcatalog.db");
        Targets.Add(new InstallTargetChoice(LocalizationService.Instance["ModInstall.Sandbox"], new("", "steam", true)));
        if (game is not null)
        {
            var installations = game.Installations.AsEnumerable();
            if (game.Detected is { } detected) installations = installations.Append(detected);
            foreach (var path in installations.Select(i => i.Installation.RootPath).Where(Directory.Exists).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                Targets.Add(new InstallTargetChoice($"Steam — {path}", new(path, "steam", false)));
        }
        SelectedTarget = Targets[0];
        if (autoSync) _ = RefreshCommand.ExecuteAsync(null);
    }
    public ObservableCollection<ThunderstoreMod> Mods { get; } = [];
    [ObservableProperty] private string endpoint = "http://127.0.0.1:18080";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string detailsStatus = "";
    [ObservableProperty] private bool isOffline;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string lastSyncText = "—";
    [ObservableProperty] private ThunderstoreMod? selectedMod;
    [ObservableProperty] private ThunderstoreModDetails? details;
    public bool HasDetails => Details is not null;
    partial void OnDetailsChanged(ThunderstoreModDetails? value) => OnPropertyChanged(nameof(HasDetails));
    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnSelectedModChanged(ThunderstoreMod? value)
    {
        var generation = ++selectionGeneration;
        Details = null; DetailsStatus = "";
        if (value is not null) _ = LoadDetailsAsync(value, generation);
    }
    internal string? LatestVersion(string fullName) =>
        all.Concat(Mods).FirstOrDefault(m => string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase))?.Latest.VersionNumber;
    private void Filter()
    {
        var q = SearchText.Trim();
        var selected = SelectedMod;
        Mods.Clear();
        foreach (var mod in all)
            if (q.Length == 0 || mod.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || mod.Owner.Contains(q, StringComparison.OrdinalIgnoreCase)) Mods.Add(mod);
        IsEmpty = Mods.Count == 0;
        if (selected is not null && Mods.Contains(selected)) SelectedMod = selected;
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
            var result = await Task.Run(async () => {
                IReadOnlyList<ThunderstoreMod> mods = []; DateTimeOffset? last = null; string? error = null; bool offline = false;
                try {
                    using var store = new ModCatalogStore(cachePath);
                    try {
                        using var http = transport is null ? CreateHttp() : null;
                        await new ModCatalogClient(transport ?? new HttpCatalogTransport(http!), store).SyncAsync(gameId);
                    } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { offline = true; error = "Builds.Offline"; }
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
            var result = await Task.Run(async () => {
                try {
                    using var store = new ModCatalogStore(cachePath);
                    using var http = transport is null ? CreateHttp() : null;
                    return await new ModCatalogClient(transport ?? new HttpCatalogTransport(http!), store).GetModAsync(mod.Id);
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
    partial void OnSelectedTargetChanged(InstallTargetChoice? value) { if (!IsPreparing) ClearInstall(); }

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
    public ObservableCollection<InstallPreview> Preview { get; } = [];
    public ObservableCollection<PlannedOperation> Operations { get; } = [];
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
        Preview.Clear(); Operations.Clear(); Conflicts.Clear(); InstallStatus = ""; IsPreparing = true;
        try
        {
            InstallPreparation result;
            if (PrepareOverride is not null) result = await PrepareOverride(requestedName, requestedVersion, Endpoint, target, overrides);
            else
            {
                using var http = transport is null ? CreateHttp() : null;
                using var db = new ModCatalogStore(Path.Combine(appDataRoot, "catalog.db"));
                var service = new ModInstallService(appDataRoot, new ModCatalogClient(transport ?? new HttpCatalogTransport(http!), db));
                result = await service.PrepareAsync(gameId, requestedName, requestedVersion, Endpoint, target, overrides);
            }
            foreach (var conflict in result.Conflicts) Conflicts.Add(conflict);
            HasConflicts = Conflicts.Count > 0;
            if (!HasConflicts)
            {
                preparation = result;
                foreach (var package in result.Preview) Preview.Add(package);
                IsSandboxTarget = result.Target.Sandbox;
                TargetDescription = result.Target.Sandbox ? Path.Combine(result.SandboxRoot, "game") : result.Target.GameRoot;
                foreach (var operation in result.Plans.SelectMany(p => p.Operations))
                    Operations.Add(operation.TargetPath.StartsWith("game://", StringComparison.OrdinalIgnoreCase)
                        ? operation with { TargetPath = Path.Combine(TargetDescription, operation.TargetPath[7..].Replace('/', Path.DirectorySeparatorChar)) }
                        : operation);
                HasPreview = true;
            }
        }
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
        Preview.Clear(); Operations.Clear(); Conflicts.Clear();
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
                using var http = transport is null ? CreateHttp() : null;
                using var db = new ModCatalogStore(Path.Combine(appDataRoot, "catalog.db"));
                var service = new ModInstallService(appDataRoot, new ModCatalogClient(transport ?? new HttpCatalogTransport(http!), db));
                result = await Task.Run(() => service.ExecuteAsync(acceptedPlan, true, progress));
            }
            InstallStatus = LocalizationService.Instance[result.Success ? "ModInstall.Success" : "ModInstall.Failed"];
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
