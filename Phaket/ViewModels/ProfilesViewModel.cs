using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Phaket.Core.Execution;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly ProfileStore store;
    private readonly ProfileHistoryStore history;
    private readonly Action back;
    private readonly GameCatalogItem game;
    private readonly string appDataRoot;
    private readonly string gameId;
    private readonly IProfileSnapshotClient snapshotClient;
    private readonly IProfilePublishService? publisher;
    private ProfileHistoryEntry? restoreTarget;
    private ProfileModItem? modRemoveTarget;
    private List<ModEntry>? incomingMods;
    private string importName = "";

    public ProfilesViewModel(GameCatalogItem game, Action back, IProfilePublishService? publisher = null)
        : this(game, back, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket"),
            new HttpProfileSnapshotClient(), publisher) { }

    internal ProfilesViewModel(GameCatalogItem game, Action back, string appDataRoot, IProfileSnapshotClient? snapshotClient = null,
        IProfilePublishService? publisher = null)
    {
        this.game = game;
        this.back = back;
        this.appDataRoot = appDataRoot;
        this.snapshotClient = snapshotClient ?? new HttpProfileSnapshotClient();
        this.publisher = publisher;
        gameId = game.Definition.Slug;
        GameTitle = game.Title;
        store = new ProfileStore(appDataRoot);
        history = new ProfileHistoryStore(appDataRoot);
        EnsureCleanProfile();
        Reload();
    }

    public string GameTitle { get; }
    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<ProfileModItem> Composition { get; } = [];
    public ObservableCollection<ProfileHistoryItem> History { get; } = [];
    public ObservableCollection<ProfileDiffItem> Diff { get; } = [];
    public ObservableCollection<InstallationChoice> Installations { get; } = [];
    [ObservableProperty] private ProfileItem? selectedProfile;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string profileName = "";

    [ObservableProperty] private string importLink = "";
    [ObservableProperty] private string shareLink = "";
    [ObservableProperty] private bool isModalOpen;
    [ObservableProperty] private bool isNewProfileModal;
    [ObservableProperty] private bool isEditProfileModal;
    [ObservableProperty] private bool isRemoveModModal;
    [ObservableProperty] private bool isHistoryModal;
    [ObservableProperty] private bool isDiffModal;
    [ObservableProperty] private bool isRollbackModal;
    [ObservableProperty] private bool isShareModal;
    [ObservableProperty] private bool isImportModal;
    [ObservableProperty] private bool isPathsModal;
    [ObservableProperty] private bool hasImportPreview;
    [ObservableProperty] private string importProfileName = "";
    [ObservableProperty] private string shareStatus = "";
    [ObservableProperty] private bool isToastVisible;
    [ObservableProperty] private bool isPublishing;

    public bool CanPublish => publisher is not null && !IsPublishing && SelectedProfile is { IsClean: false };
    public string PublishLabel => LocalizationService.Instance[IsPublishing ? "Profiles.Publishing" : "Profiles.Publish"];

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(IsStatusEmpty));
    partial void OnHasImportPreviewChanged(bool value) => OnPropertyChanged(nameof(ShowCancelButton));

    public bool IsCleanProfile => SelectedProfile?.IsClean == true;
    public bool HasComposition => Composition.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool IsStatusEmpty => string.IsNullOrEmpty(StatusMessage);
    public bool IsProfileEditModal => IsNewProfileModal || IsEditProfileModal;
    public bool ShowCancelButton => IsNewProfileModal || IsEditProfileModal || IsRemoveModModal || IsRollbackModal || IsShareModal || IsImportModal;
    public bool IsRequiredMod => modRemoveTarget?.Model.Required == true;
    public string ProtectedModMessage => IsRequiredMod ? LocalizationService.Instance["Profiles.Required"] : "";
    public string RestoreTargetSummary => restoreTarget is null ? "" : $"{restoreTarget.Description} · {restoreTarget.CreatedAt.ToLocalTime():g}";
    public string ModalTitle => IsNewProfileModal ? LocalizationService.Instance["Profiles.New"]
        : IsEditProfileModal ? LocalizationService.Instance["Profiles.Rename"]
        : IsRemoveModModal ? LocalizationService.Instance["Profiles.RemoveMod"]
        : IsHistoryModal ? LocalizationService.Instance["Profiles.History"]
        : IsDiffModal ? LocalizationService.Instance["Profiles.CompareVersions"]
        : IsRollbackModal ? LocalizationService.Instance["Profiles.Rollback"]
        : IsShareModal ? LocalizationService.Instance["Profiles.Share"]
        : IsImportModal ? LocalizationService.Instance["Profiles.Import"]
        : LocalizationService.Instance["Profiles.Paths"];
    public string ActiveProfileName => SelectedProfile?.Name ?? "";
    public string SelectedProfileSummary => SelectedProfile is null ? "" : SelectedProfile.IsClean
        ? LocalizationService.Instance["Profiles.CleanHint"]
        : $"{SelectedProfile.ModCount} {LocalizationService.Instance["Profiles.Mods"]} · {LocalizationService.Instance["Profiles.Installed"]}";
    public string HistoryEmptyText => LocalizationService.Instance["Profiles.History.Empty"];
    public bool HasInstallations => Installations.Count > 0;
    public bool HasDiff => Diff.Count > 0;
    public string ComparisonTitle => restoreTarget is null ? LocalizationService.Instance["Profiles.CompareVersions"]
        : $"{restoreTarget.Snapshot.Name} · {restoreTarget.CreatedAt.ToLocalTime():g}";

    private static bool IsCleanName(string name) =>
        name.Equals("Czysta gra", StringComparison.OrdinalIgnoreCase) || name.Equals("Clean game", StringComparison.OrdinalIgnoreCase);

    private bool IsClean(GameProfile profile) =>
        profile.Id == "clean-game" || IsCleanName(profile.Name);

    private void EnsureCleanProfile()
    {
        var data = store.For(gameId);
        if (store.IsLoadFailed) return;
        var clean = data.Profiles.FirstOrDefault(IsClean);
        if (clean is null)
        {
            clean = store.AddCleanProfile(gameId, LocalizationService.Instance["Profiles.Clean"]);
            if (data.ActiveProfileId is null) store.SetActive(gameId, clean.Id);
        }
        else if (clean.Id != "clean-game" || clean.Name != LocalizationService.Instance["Profiles.Clean"] || clean.Mods.Count != 0)
        {
            clean.Id = "clean-game";
            clean.Name = LocalizationService.Instance["Profiles.Clean"];
            clean.Mods.Clear();
            store.Save();
        }
    }

    private void Reload()
    {
        var data = store.For(gameId);
        if (!store.IsLoadFailed)
        {
            try
            {
                foreach (var profile in data.Profiles) history.Record(gameId, profile);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
        }
        Profiles.Clear();
        foreach (var profile in data.Profiles)
            Profiles.Add(new ProfileItem(profile, IsClean(profile)));
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == data.ActiveProfileId) ?? Profiles.FirstOrDefault();
        if (SelectedProfile is not null && data.ActiveProfileId != SelectedProfile.Id) store.SetActive(gameId, SelectedProfile.Id);
        foreach (var profileItem in Profiles) profileItem.IsActive = profileItem.Id == SelectedProfile?.Id;
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        Composition.Clear();
        History.Clear();
        if (SelectedProfile is { IsClean: false, Model: { } profile })
        {
            foreach (var item in history.For(gameId, profile.Id).Reverse())
                History.Add(new ProfileHistoryItem(item));
            var installed = new InstallRegistry(Path.Combine(appDataRoot, "installs.json")).ForGame(gameId)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ModFullName))
                .GroupBy(entry => entry.ModFullName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.InstalledAt).First().ModVersion, StringComparer.OrdinalIgnoreCase);
            foreach (var mod in profile.Mods)
            {
                if (installed.TryGetValue(mod.Name, out var installedVersion)) mod.Compatible = string.Equals(installedVersion, mod.Version, StringComparison.OrdinalIgnoreCase);
                Composition.Add(new ProfileModItem(mod));
            }
        }
        else if (SelectedProfile?.IsClean == true)
        {
            // Clean game is a protected, empty profile; it has no mod composition/history.
        }
        OnPropertyChanged(nameof(IsCleanProfile));
        OnPropertyChanged(nameof(HasComposition));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(ActiveProfileName));
        OnPropertyChanged(nameof(SelectedProfileSummary));
        OnPropertyChanged(nameof(HistoryEmptyText));
        OnPropertyChanged(nameof(ModalTitle));
        OnPropertyChanged(nameof(IsStatusEmpty));
        OnPropertyChanged(nameof(IsProfileEditModal));
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(IsRequiredMod));
        OnPropertyChanged(nameof(ProtectedModMessage));
        OnPropertyChanged(nameof(RestoreTargetSummary));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(PublishLabel));
    }

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        RefreshDetails();
    }

    [RelayCommand] private void Back() => back();

    [RelayCommand]
    private void Select(ProfileItem? item)
    {
        if (item is null || item.Id == SelectedProfile?.Id) return;
        store.SetActive(gameId, item.Id);
        SelectedProfile = Profiles.First(p => p.Id == item.Id);
        foreach (var profile in Profiles) profile.IsActive = profile.Id == item.Id;
        StatusMessage = "";
    }

    [RelayCommand]
    private void NewProfile()
    {
        ProfileName = string.Format(LocalizationService.Instance["Profiles.DefaultName"], store.For(gameId).Profiles.Count(p => p.Name != LocalizationService.Instance["Profiles.Clean"]) + 1);
        OpenModal(isNew: true);
    }

    [RelayCommand]
    private void EditProfile()
    {
        if (SelectedProfile is null || SelectedProfile.IsClean) return;
        ProfileName = SelectedProfile.Name;
        OpenModal(isEdit: true);
    }

    [RelayCommand]
    private void SaveProfile()
    {
        var name = ProfileName.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
        {
            StatusMessage = LocalizationService.Instance["Profiles.NameRequired"];
            return;
        }
        var data = store.For(gameId);
        if ((IsNewProfileModal || IsEditProfileModal) && IsCleanName(name))
        {
            StatusMessage = LocalizationService.Instance["Profiles.CleanProtected"];
            return;
        }
        if (data.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Id != SelectedProfile?.Id))
        {
            StatusMessage = LocalizationService.Instance["Profiles.NameExists"];
            return;
        }
        if (IsNewProfileModal)
        {
            try
            {
                store.AddProfile(gameId, name, []);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                return;
            }
        }
        else if (IsEditProfileModal && SelectedProfile is { IsClean: false, Model: { } model })
        {
            var oldName = model.Name;
            model.Name = name;
            try
            {
                store.Save();
            }
            catch (Exception ex)
            {
                model.Name = oldName;
                StatusMessage = ex.Message;
                return;
            }
        }
        CloseModal();
        Reload();
    }

    [RelayCommand]
    private void ShowHistory()
    {
        if (SelectedProfile?.IsClean == true) return;
        OpenModal(isHistory: true);
    }

    [RelayCommand]
    private void Compare(ProfileHistoryItem? item)
    {
        if (item is null || SelectedProfile is not { IsClean: false, Model: { } profile } || item.Entry.Snapshot.Id != profile.Id) return;
        restoreTarget = item.Entry;
        BuildDiff(profile, item.Entry.Snapshot);
        OpenModal(isDiff: true);
    }

    private void BuildDiff(GameProfile current, GameProfile target)
    {
        Diff.Clear();
        var from = current.Mods.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var to = target.Mods.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in from.Keys.Union(to.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            from.TryGetValue(name, out var a);
            to.TryGetValue(name, out var b);
            var state = a is null ? "Profiles.Added" : b is null ? "Profiles.Removed"
                : a.Version != b.Version || a.Enabled != b.Enabled ? "Profiles.Updated" : "Profiles.Unchanged";
            Diff.Add(new ProfileDiffItem(name, a?.Version ?? "—", b?.Version ?? "—", state));
        }
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(ComparisonTitle));
    }

    [RelayCommand]
    private void AskRollback(ProfileHistoryItem? item)
    {
        if (item is null || SelectedProfile is not { IsClean: false } || item.Entry.Snapshot.Id != SelectedProfile.Id) return;
        restoreTarget = item.Entry;
        OpenModal(isRollback: true);
    }

    [RelayCommand]
    private void ConfirmRollback()
    {
        if (restoreTarget is null || SelectedProfile is not { IsClean: false, Model: { } profile }) return;
        BackupEntry backup;
        try { backup = store.CreateBackup(gameId, $"{LocalizationService.Instance["Profiles.Rollback"]} · {DateTimeOffset.UtcNow:O}", "profile-history-rollback"); }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return;
        }
        var oldName = profile.Name;
        try
        {
            profile.Name = restoreTarget.Snapshot.Name;
            profile.Mods = restoreTarget.Snapshot.Mods.Select(CloneMod).ToList();
            store.Save();
            history.Record(gameId, profile);
            CloseModal();
            StatusMessage = LocalizationService.Instance["Profiles.RollbackDone"];
            Reload();
        }
        catch (Exception ex)
        {
            profile.Name = oldName;
            try { store.Restore(gameId, backup); }
            catch (Exception rollbackError) { System.Diagnostics.Trace.TraceError("Profile rollback failed: {0}", rollbackError); }
            StatusMessage = ex.Message;
            Reload();
        }
    }

    [RelayCommand]
    private void AskRemoveMod(ProfileModItem? item)
    {
        if (item is null || IsCleanProfile) return;
        modRemoveTarget = item;
        OnPropertyChanged(nameof(IsRequiredMod));
        OnPropertyChanged(nameof(ProtectedModMessage));
        OpenModal(isRemoveMod: true);
    }

    [RelayCommand]
    private void ConfirmRemoveMod()
    {
        if (modRemoveTarget is null || modRemoveTarget.Model.Required || SelectedProfile is not { IsClean: false, Model: { } profile }) return;
        var target = modRemoveTarget.Model;
        var index = profile.Mods.FindIndex(m => ReferenceEquals(m, target));
        if (index < 0) return;
        profile.Mods.RemoveAt(index);
        try
        {
            store.Save();
            history.Record(gameId, profile);
            modRemoveTarget = null;
            CloseModal();
            Reload();
        }
        catch (Exception ex)
        {
            profile.Mods.Insert(index, target);
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void ShareProfile()
    {
        if (SelectedProfile is null || SelectedProfile.IsClean) return;
        var slug = ToSlug(SelectedProfile.Name);
        var version = Math.Max(1, history.For(gameId, SelectedProfile.Id).Count);
        ShareLink = new ProfileLink(slug, version).ToUri();
        ShareStatus = LocalizationService.Instance["Profiles.ShareOffline"];
        IsToastVisible = true;
        OpenModal(isShare: true);
    }

    [RelayCommand]
    private async Task CopyShareLinkAsync()
    {
        try
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            IClipboard? clipboard = desktop?.MainWindow?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException();
            await clipboard.SetTextAsync(ShareLink);
            ShareStatus = LocalizationService.Instance["Profiles.LinkCopied"];
            IsToastVisible = true;
        }
        catch (Exception)
        {
            ShareStatus = LocalizationService.Instance["Profiles.LinkCopyFailed"];
            IsToastVisible = true;
        }
    }

    // Publishing the profile snapshot is the signed counterpart of the offline phaket:// link:
    // the shell hands the accounts page publisher to this view model (see MainWindowViewModel).
    [RelayCommand]
    private async Task PublishProfileAsync()
    {
        if (publisher is null || IsPublishing || SelectedProfile is not { IsClean: false, Model: { } profile }) return;
        var items = profile.Mods.Where(mod => mod.Enabled)
            .Select(mod => new { modName = mod.Name, modVersion = mod.Version })
            .ToArray();
        if (items.Length == 0)
        {
            ShareStatus = LocalizationService.Instance["Profiles.PublishEmpty"];
            return;
        }

        IsPublishing = true;
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(PublishLabel));
        try
        {
            var snapshot = JsonSerializer.SerializeToElement(new { schemaVersion = 1, items });
            // The slug the server handed back stays with the profile: the first publication creates the
            // package, every later one adds a version to it, so republishing never collides with a taken slug.
            bool firstPublication = string.IsNullOrWhiteSpace(profile.Slug);
            var request = new ProfilePublishRequest(firstPublication ? ToSlug(profile.Name) : profile.Slug!,
                gameId, profile.Name, "", snapshot, CreateNew: firstPublication);
            var result = await publisher.PublishAsync(request);
            profile.Slug = result.Slug;
            try { store.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { System.Diagnostics.Trace.TraceWarning("The published profile slug could not be saved: {0}", ex); }
            ShareStatus = string.Format(LocalizationService.Instance["Profiles.PublishDone"], result.Slug, result.Version);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Profile snapshot publication failed: {0}", ex);
            ShareStatus = LocalizationService.Instance["Profiles.PublishFailed"];
        }
        finally
        {
            IsPublishing = false;
            OnPropertyChanged(nameof(CanPublish));
            OnPropertyChanged(nameof(PublishLabel));
        }
    }

    [RelayCommand]
    private void ImportProfile()    {
        ImportLink = "";
        incomingMods = null;
        HasImportPreview = false;
        ImportProfileName = "";
        StatusMessage = "";
        OpenModal(isImport: true);
    }

    [RelayCommand]
    private async Task PreviewImportAsync()
    {
        incomingMods = null;
        HasImportPreview = false;
        if (!ProfileLink.TryParse(ImportLink, out var link) || link is null)
        {
            StatusMessage = LocalizationService.Instance["Profiles.ImportInvalid"];
            return;
        }
        try
        {
            using var doc = await snapshotClient.FetchAsync(link);
            var root = doc.RootElement;
            if (!root.TryGetProperty("gameId", out var gameValue) || gameValue.GetString() != gameId ||
                !root.TryGetProperty("name", out var nameValue) || nameValue.GetString() is not { Length: > 0 and <= 100 } name ||
                !root.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object ||
                !snapshot.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
                !snapshot.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 500)
                throw new InvalidDataException("Invalid profile snapshot.");
            var mods = new List<ModEntry>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("modName", out var modName) || modName.GetString() is not { Length: > 0 and <= 160 } mod ||
                    !item.TryGetProperty("modVersion", out var modVersion) || modVersion.GetString() is not { Length: > 0 and <= 100 } version ||
                    mods.Any(m => m.Name.Equals(mod, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Invalid mod in profile snapshot.");
                mods.Add(new ModEntry { Name = mod, Version = version });
            }
            if (mods.Count == 0) throw new InvalidDataException("Empty profile snapshots cannot be imported.");
            incomingMods = mods;
            importName = name;
            ImportProfileName = name;
            var temp = new GameProfile { Id = SelectedProfile?.Id ?? "", Name = name, Mods = mods };
            if (SelectedProfile?.Model is { } current) BuildDiff(current, temp);
            else Diff.Clear();
            HasImportPreview = true;
            StatusMessage = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or UriFormatException or InvalidOperationException)
        {
            StatusMessage = LocalizationService.Instance["Profiles.ImportOffline"];
        }
    }

    [RelayCommand]
    private void ConfirmImport()
    {
        if (!HasImportPreview || incomingMods is null) return;
        if (store.For(gameId).Profiles.Any(p => p.Name.Equals(importName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = LocalizationService.Instance["Profiles.NameExists"];
            return;
        }
        try
        {
            store.AddProfile(gameId, importName + " (import)", incomingMods.Select(CloneMod));
            incomingMods = null;
            HasImportPreview = false;
            CloseModal();
            Reload();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void ShowPaths()
    {
        Installations.Clear();
        foreach (var installation in game.AvailableInstallations) Installations.Add(installation);
        OnPropertyChanged(nameof(HasInstallations));
        OpenModal(isPaths: true);
    }

    [RelayCommand]
    private void CloseModal()
    {
        IsModalOpen = false;
        IsToastVisible = false;
        IsNewProfileModal = IsEditProfileModal = IsRemoveModModal = IsHistoryModal = IsDiffModal =
            IsRollbackModal = IsShareModal = IsImportModal = IsPathsModal = false;
        restoreTarget = null;
        modRemoveTarget = null;
        OnPropertyChanged(nameof(ModalTitle));
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(IsProfileEditModal));
        OnPropertyChanged(nameof(IsRequiredMod));
        OnPropertyChanged(nameof(ProtectedModMessage));
        OnPropertyChanged(nameof(RestoreTargetSummary));
    }

    private void OpenModal(bool isNew = false, bool isEdit = false, bool isRemoveMod = false, bool isHistory = false,
        bool isDiff = false, bool isRollback = false, bool isShare = false, bool isImport = false, bool isPaths = false)
    {
        IsNewProfileModal = isNew;
        IsEditProfileModal = isEdit;
        IsRemoveModModal = isRemoveMod;
        IsHistoryModal = isHistory;
        IsDiffModal = isDiff;
        IsRollbackModal = isRollback;
        IsShareModal = isShare;
        IsImportModal = isImport;
        IsPathsModal = isPaths;
        IsModalOpen = true;
        OnPropertyChanged(nameof(ModalTitle));
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(IsProfileEditModal));
        OnPropertyChanged(nameof(IsStatusEmpty));
        OnPropertyChanged(nameof(IsRequiredMod));
        OnPropertyChanged(nameof(ProtectedModMessage));
        OnPropertyChanged(nameof(RestoreTargetSummary));
    }

    private static ModEntry CloneMod(ModEntry mod) => new()
    {
        Name = mod.Name,
        Version = mod.Version,
        Enabled = mod.Enabled,
        Compatible = mod.Compatible,
        Required = mod.Required,
    };

    private static string ToSlug(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var ascii = new string(normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var slug = Regex.Replace(ascii.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        return string.IsNullOrEmpty(slug) ? "profile" : slug;
    }
}

public partial class ProfileItem : ViewModelBase
{
    public ProfileItem(GameProfile model, bool isClean)
    {
        Model = model;
        IsClean = isClean;
    }
    public GameProfile Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public int ModCount => IsClean ? 0 : Model.Mods.Count;
    public bool IsClean { get; }
    [ObservableProperty] private bool isActive;
    public string Summary => IsClean ? LocalizationService.Instance["Profiles.CleanHint"]
        : $"{ModCount} {LocalizationService.Instance["Profiles.Mods"]}";
}

public sealed class ProfileModItem
{
    public ProfileModItem(ModEntry model) => Model = model;
    public ModEntry Model { get; }
    public string Name => Model.Name;
    public string Version => Model.Version;
    public string StateKey => !Model.Compatible ? "Profiles.ModState.Incompatible" : Model.Enabled ? "Profiles.ModState.Enabled" : "Profiles.ModState.Disabled";
    public string StateText => LocalizationService.Instance[StateKey];
    public bool CanRemove => !Model.Required;
}

public sealed class ProfileHistoryItem
{
    public ProfileHistoryItem(ProfileHistoryEntry entry) => Entry = entry;
    public ProfileHistoryEntry Entry { get; }
    public string Description => LocalizationService.Instance[Entry.Description];
    public string CreatedAt => Entry.CreatedAt.ToLocalTime().ToString("g");
}

public sealed record ProfileDiffItem(string Name, string FromVersion, string ToVersion, string StateKey)
{
    public string StateText => LocalizationService.Instance[StateKey];
}

