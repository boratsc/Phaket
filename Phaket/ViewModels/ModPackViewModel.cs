using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Catalog;
using Phaket.Models;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class ModPackViewModel : ViewModelBase, IDisposable
{
    private readonly ModpackStore store;
    private readonly Action close;
    private readonly ModpackDefinition pack;
    private readonly string appDataRoot;
    private readonly GitHubReleaseService? gitHubService;
    private readonly NexusManualViewModel? suppliedNexusPicker;
    private bool ownsGitHubPicker;

    public ModPackViewModel(ModpackDefinition pack, ModpackStore store, Action close)
        : this(pack, store, close, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket")) { }

    internal ModPackViewModel(ModpackDefinition pack, ModpackStore store, Action close, string appDataRoot,
        bool isNewPack = false, GitHubReleaseService? gitHubService = null, NexusManualViewModel? nexusPicker = null)
    {
        this.pack = pack;
        this.store = store;
        this.close = close;
        this.appDataRoot = appDataRoot;
        this.gitHubService = gitHubService;
        suppliedNexusPicker = nexusPicker;
        GameId = pack.GameId;
        Name = pack.Name;
        Author = pack.Author;
        Version = pack.Version;
        IsNewPack = isNewPack;
        ThunderstoreCatalog = new ModCatalogViewModel(pack.GameId, appDataRoot, autoSync: false);
        SelectedSourceFilter = SourceFilters[0];
        Reload();
        if (isNewPack) OpenAddMods();
    }

    public string GameId { get; }
    public ModCatalogViewModel ThunderstoreCatalog { get; }
    public GitHubReleasePickerViewModel? GitHubPicker { get; private set; }
    public NexusManualViewModel? NexusPicker { get; private set; }
    public bool IsThunderstoreSource => SelectedAddSource == "thunderstore";
    public bool IsGitHubSource => SelectedAddSource == "github";
    public bool IsNexusSource => SelectedAddSource == "nexus";
    public bool CanAddPinnedSource => IsGitHubSource ? GitHubPicker?.PinnedSelection is not null
        : IsNexusSource && NexusPicker?.Result is not null;

    [ObservableProperty] private bool isAddPanelOpen;
    [ObservableProperty] private string selectedAddSource = "thunderstore";
    [ObservableProperty] private string sourceModName = "";
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string author = "";
    [ObservableProperty] private string version = "";
    [ObservableProperty] private bool isNewPack;
    public ObservableCollection<ModpackSourceFilter> SourceFilters { get; } =
    [
        new("all", "Catalog.SourceAll"), new("thunderstore", "Catalog.Source.thunderstore"),
        new("susmodder", "Catalog.Source.susmodder"), new("github", "Catalog.Source.github"),
        new("nexus", "Catalog.Source.nexus")
    ];
    [ObservableProperty] private ModpackSourceFilter? selectedSourceFilter;
    public int EnabledCount => Mods.Count(mod => mod.Enabled);
    public string BulkToggleLabel => LocalizationService.Instance[AnySelectedDisabled ? "Catalog.EnableSelected" : "Catalog.DisableSelected"];
    public ObservableCollection<ModpackModItemViewModel> Mods { get; } = [];
    public ObservableCollection<ModpackModItemViewModel> VisibleMods { get; } = [];
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string sourceFilter = "all";
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string status = "";
    public bool HasSelection => VisibleMods.Any(m => m.IsSelected);
    public int SelectedCount => VisibleMods.Count(m => m.IsSelected);
    public bool CanBulkToggle => VisibleMods.Any(m => m.IsSelected && !m.Required);
    public bool CanBulkDelete => CanBulkToggle;
    public bool AllVisibleSelectable
    {
        get => VisibleMods.Where(m => !m.Required).Any() && VisibleMods.Where(m => !m.Required).All(m => m.IsSelected);
        set
        {
            foreach (var mod in VisibleMods.Where(m => !m.Required)) mod.IsSelected = value;
        }
    }
    public bool AnySelectedDisabled => VisibleMods.Any(m => m.IsSelected && !m.Enabled && !m.Required);
    public bool HasVisibleProtected => VisibleMods.Any(m => m.Required);

    partial void OnSelectedAddSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsThunderstoreSource));
        OnPropertyChanged(nameof(IsGitHubSource));
        OnPropertyChanged(nameof(IsNexusSource));
        OnPropertyChanged(nameof(CanAddPinnedSource));
        AddPinnedSourceCommand.NotifyCanExecuteChanged();
    }

    partial void OnSourceModNameChanged(string value) => AddPinnedSourceCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void OpenAddMods()
    {
        IsAddPanelOpen = true;
        SourceModName = "";
        Status = "";
        SelectAddSource("thunderstore");
    }

    [RelayCommand]
    private void CloseAddMods() => IsAddPanelOpen = false;

    [RelayCommand]
    private void SelectAddSource(string? source)
    {
        if (source is not ("thunderstore" or "github" or "nexus")) return;
        SelectedAddSource = source;
        if (source == "thunderstore")
            _ = ThunderstoreCatalog.RefreshCommand.ExecuteAsync(null);
        else if (source == "github")
            EnsureGitHubPicker();
        else
            EnsureNexusPicker();
        AddPinnedSourceCommand.NotifyCanExecuteChanged();
    }

    private void EnsureGitHubPicker()
    {
        if (GitHubPicker is not null) return;
        GitHubPicker = new GitHubReleasePickerViewModel(gitHubService);
        ownsGitHubPicker = true;
        GitHubPicker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitHubReleasePickerViewModel.PinnedSelection))
            {
                OnPropertyChanged(nameof(CanAddPinnedSource));
                AddPinnedSourceCommand.NotifyCanExecuteChanged();
            }
        };
        OnPropertyChanged(nameof(GitHubPicker));
    }

    private void EnsureNexusPicker()
    {
        if (NexusPicker is not null) return;
        NexusPicker = suppliedNexusPicker ?? new NexusManualViewModel(
            nexusSource: GameDefinitions.LoadAll().FirstOrDefault(definition => definition.Slug == GameId)?.Sources?.Nexus);
        NexusPicker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NexusManualViewModel.Result))
            {
                OnPropertyChanged(nameof(CanAddPinnedSource));
                AddPinnedSourceCommand.NotifyCanExecuteChanged();
            }
        };
        OnPropertyChanged(nameof(NexusPicker));
    }

    public bool CanAddPinnedSourceCommand() => CanAddPinnedSource;

    [RelayCommand(CanExecute = nameof(CanAddPinnedSourceCommand))]
    private void AddPinnedSource()
    {
        SourcePickResult? result = SelectedAddSource switch
        {
            "github" => GitHubPicker?.PinnedSelection,
            "nexus" => NexusPicker?.Result,
            _ => null
        };
        if (result is null)
        {
            Status = LocalizationService.Instance["Catalog.SourceAddInvalid"];
            return;
        }
        if (result is GitHubReleasePickResult { IsDemo: true })
        {
            Status = LocalizationService.Instance["Catalog.SourceDemoRejected"];
            return;
        }

        var item = result switch
        {
            GitHubReleasePickResult github => new ModpackMod
            {
                Name = string.IsNullOrWhiteSpace(SourceModName) ? github.Repo.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "" : SourceModName.Trim(),
                Version = github.Tag,
                Source = "github",
                Repository = github.Repo,
                Tag = github.Tag,
                AssetId = github.AssetId.ToString(CultureInfo.InvariantCulture),
                Sha256 = null,
                Link = string.IsNullOrWhiteSpace(github.DownloadUrl) ? null : github.DownloadUrl,
            },
            NexusManualPickResult nexus => new ModpackMod
            {
                Name = nexus.Name,
                Version = nexus.PinnedVersion,
                Source = "nexus",
                Link = nexus.Url,
                ManualPath = nexus.LocalArchivePath,
            },
            _ => null
        };
        if (item is null)
        {
            Status = LocalizationService.Instance["Catalog.SourceAddInvalid"];
            return;
        }
        AddMod(item);
    }

    [RelayCommand]
    private void AddThunderstoreMod(ThunderstoreMod? mod)
    {
        if (mod is null || string.IsNullOrWhiteSpace(mod.FullName) || string.IsNullOrWhiteSpace(mod.Latest.VersionNumber)) return;
        // PAK-101/PAK-103: a SusModder row keeps its own source; the server resolves the
        // artifact from its catalog and matches the item by full name or external id.
        var susmodder = string.Equals(mod.Source, "susmodder", StringComparison.OrdinalIgnoreCase);
        AddMod(new ModpackMod
        {
            Name = mod.FullName,
            Version = mod.Latest.VersionNumber,
            Source = susmodder ? "susmodder" : "thunderstore",
            ModId = susmodder ? mod.ExternalId : mod.Id,
            Link = susmodder ? null : mod.PackageUrl,
            Dependencies = [.. mod.Latest.Dependencies],
        });
    }

    private void AddMod(ModpackMod item)
    {
        if (string.IsNullOrWhiteSpace(item.Name) || Mods.Any(mod => mod.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = LocalizationService.Instance["Catalog.SourceDuplicate"];
            return;
        }
        var previousItems = pack.Items;
        pack.Items = [.. Mods.Select(mod => mod.ToModel()), item];
        Reload();
        if (!Save(previousItems))
        {
            Reload();
            return;
        }
        OnPropertyChanged(nameof(CanAddPinnedSource));
        Status = LocalizationService.Instance["Catalog.SourceAdded"];
        SourceModName = "";
        IsAddPanelOpen = false;
        if (GitHubPicker is not null) GitHubPicker.ClearPinCommand.Execute(null);
        if (NexusPicker is not null)
        {
            NexusPicker.Url = "";
            NexusPicker.Name = "";
            NexusPicker.PinnedVersion = "";
            NexusPicker.LocalArchivePath = "";
            NexusPicker.ValidationMessage = "";
        }
    }

    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnSourceFilterChanged(string value) => Filter();
    partial void OnSelectedSourceFilterChanged(ModpackSourceFilter? value)
    {
        SourceFilter = value?.Id ?? "all";
        Filter();
    }

    private void Reload()
    {
        Mods.Clear();
        foreach (var mod in pack.Items)
        {
            var viewModel = new ModpackModItemViewModel(mod, () => Save());
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModpackModItemViewModel.IsSelected)) RefreshSelectionState();
                if (e.PropertyName == nameof(ModpackModItemViewModel.Enabled))
                {
                    OnPropertyChanged(nameof(EnabledCount));
                    RefreshSelectionState();
                }
            };
            Mods.Add(viewModel);
        }
        Filter();
    }

    private void Filter()
    {
        VisibleMods.Clear();
        var query = SearchText.Trim();
        foreach (var mod in Mods.Where(m => (query.Length == 0 || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (SourceFilter == "all" || string.Equals(m.Source, SourceFilter, StringComparison.OrdinalIgnoreCase))))
            VisibleMods.Add(mod);
        IsEmpty = VisibleMods.Count == 0;
        RefreshSelectionState();
    }

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanBulkToggle));
        OnPropertyChanged(nameof(CanBulkDelete));
        OnPropertyChanged(nameof(AllVisibleSelectable));
        OnPropertyChanged(nameof(AnySelectedDisabled));
        OnPropertyChanged(nameof(HasVisibleProtected));
    }

    private bool Save(List<ModpackMod>? originalItems = null)
    {
        if (store.IsReadOnly)
        {
            Status = LocalizationService.Instance["Catalog.PackSaveFailed"];
            return false;
        }
        string name = Name.Trim();
        string author = Author.Trim();
        string version = Version.Trim();
        static bool Valid(string value, int max) => value.Length is > 0 && value.Length <= max && !value.Any(char.IsControl);
        if (!Valid(name, 100) || !Valid(author, 100) || !Valid(version, 100))
        {
            Status = LocalizationService.Instance["Catalog.PackFieldsRequired"];
            return false;
        }

        string oldName = pack.Name, oldAuthor = pack.Author, oldVersion = pack.Version;
        var oldItems = originalItems is null ? pack.Items : [.. originalItems];
        try
        {
            pack.Name = name;
            pack.Author = author;
            pack.Version = version;
            pack.Items = Mods.Select(m => m.ToModel()).ToList();
            store.Save(pack);
            Name = name;
            Author = author;
            Version = version;
            IsNewPack = false;
            Status = LocalizationService.Instance["Catalog.PackSaved"];
            OnPropertyChanged(nameof(EnabledCount));
            Filter();
            return true;
        }
        catch (Exception)
        {
            pack.Name = oldName;
            pack.Author = oldAuthor;
            pack.Version = oldVersion;
            pack.Items = oldItems;
            Status = LocalizationService.Instance["Catalog.PackSaveFailed"];
            Filter();
            return false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        Dispose();
        close();
    }

    [RelayCommand]
    private void SavePack()
    {
        bool wasNew = IsNewPack;
        if (Save() && wasNew) IsAddPanelOpen = false;
    }

    public void Dispose()
    {
        if (ownsGitHubPicker) GitHubPicker?.Dispose();
        ThunderstoreCatalog.Dispose();
    }

    [RelayCommand]
    private void ToggleSelected()
    {
        var selected = VisibleMods.Where(m => m.IsSelected && !m.Required).ToArray();
        if (selected.Length == 0) return;
        var enable = selected.Any(m => !m.Enabled);
        foreach (var mod in selected) mod.Enabled = enable;
        Save();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var removableIds = VisibleMods.Where(m => m.IsSelected && !m.Required).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        if (removableIds.Count == 0) return;
        foreach (var mod in Mods.Where(m => removableIds.Contains(m.Id)).ToArray()) Mods.Remove(mod);
        Save();
    }
}

public sealed record ModpackSourceFilter(string Id, string LabelKey)
{
    public string Label => LocalizationService.Instance[LabelKey];
}

public partial class ModpackModItemViewModel : ViewModelBase
{
    private readonly ModpackMod model;
    private readonly Action changed;
    public ModpackModItemViewModel(ModpackMod model, Action changed) { this.model = model; this.changed = changed; }
    public string Id => IdFor(model);
    public string Name => model.Name;
    public string Version => model.Version;
    public string Source => model.Source;
    public string SourceLabel => LocalizationService.Instance["Catalog.Source." + model.Source];
    public bool Required => model.Required;
    public bool IsSelectable => !Required;
    public bool IsNotRequired => !Required;
    public string EnabledLabel => LocalizationService.Instance[model.Enabled ? "Catalog.EnabledState" : "Catalog.Disabled"];
    public string StateAutomationName => Name + " · " + EnabledLabel;
    public string SelectionAutomationName => Name + " · " + (Required ? ProtectedReason : LocalizationService.Instance["Catalog.SelectMod"]);
    // null (nie pusty tekst) dla modów niezależnych: pusty ToolTip.Tip otwiera pustą ramkę.
    public string? ProtectedReason => Required ? LocalizationService.Instance["Catalog.RequiredDependencyProtected"] : null;
    public bool Enabled
    {
        get => model.Enabled;
        set { if (model.Enabled == value) return; model.Enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(EnabledLabel)); OnPropertyChanged(nameof(StateAutomationName)); changed(); }
    }
    [ObservableProperty] private bool isSelected;
    public ModpackMod ToModel() => new()
    {
        Name = model.Name, Version = model.Version, Source = model.Source, Required = model.Required,
        Enabled = model.Enabled, Dependencies = [.. model.Dependencies], ModId = model.ModId,
        Repository = model.Repository, Tag = model.Tag, AssetId = model.AssetId, Sha256 = model.Sha256, Link = model.Link,
        ManualPath = model.ManualPath
    };
    public static string IdFor(ModpackMod mod) => mod.Source + ":" + mod.Name + ":" + mod.Version;
}
