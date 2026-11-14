using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class ModItemViewModel : ViewModelBase
{
    private readonly ModEntry entry;
    private readonly Action changed;

    public ModItemViewModel(ModEntry entry, Action changed)
    {
        this.entry = entry;
        this.changed = changed;
    }

    public string Name => entry.Name;
    public string Version => "v" + entry.Version;
    public bool Compatible => entry.Compatible;
    public string CompatibilityKey => entry.Compatible ? "Mods.Compatible" : "Mods.Incompatible";

    public bool IsEnabled
    {
        get => entry.Enabled;
        set
        {
            if (entry.Enabled == value) return;
            entry.Enabled = value;
            OnPropertyChanged();
            changed();
        }
    }
}

public partial class ModsViewModel : ViewModelBase
{
    private readonly ProfileStore store = new();
    private readonly Action back;
    private readonly string gameId;

    public ModsViewModel(GameCatalogItem game, Action back)
    {
        this.back = back;
        gameId = game.Definition.Slug;
        GameTitle = game.Title;
        Cover = game.Cover;
        Reload();
    }

    public string GameTitle { get; }
    public string Cover { get; }
    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    [ObservableProperty] private string filter = "all";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string activeProfileName = "";

    public string AllLabel => $"{LocalizationService.Instance["Mods.All"]} ({CountAll})";
    private int CountAll => store.Active(gameId)?.Mods.Count ?? 0;

    private void Reload()
    {
        ActiveProfileName = store.Active(gameId)?.Name ?? "";
        Mods.Clear();
        var profile = store.Active(gameId);
        if (profile is null) return;
        foreach (var mod in profile.Mods)
        {
            if (Filter == "active" && !mod.Enabled) continue;
            if (Filter == "inactive" && mod.Enabled) continue;
            if (!string.IsNullOrWhiteSpace(SearchText) &&
                !mod.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) continue;
            Mods.Add(new ModItemViewModel(mod, () => store.Save()));
        }
        OnPropertyChanged(nameof(AllLabel));
    }

    partial void OnFilterChanged(string value) => Reload();
    partial void OnSearchTextChanged(string value) => Reload();

    [RelayCommand] private void Back() => back();
    [RelayCommand] private void SetFilter(string f) => Filter = f;

    [RelayCommand]
    private void AddMod()
    {
        var profile = store.Active(gameId);
        if (profile is null) return;
        profile.Mods.Add(new ModEntry { Name = $"Nowy mod {profile.Mods.Count + 1}", Version = "1.0.0" });
        store.Save();
        Reload();
    }
}

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly ProfileStore store = new();
    private readonly Action back;
    private readonly string gameId;

    public ProfilesViewModel(GameCatalogItem game, Action back)
    {
        this.back = back;
        gameId = game.Definition.Slug;
        GameTitle = game.Title;
        Reload();
    }

    public string GameTitle { get; }
    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    private void Reload()
    {
        Profiles.Clear();
        var data = store.For(gameId);
        foreach (var profile in data.Profiles)
            Profiles.Add(new ProfileItem
            {
                Id = profile.Id,
                Name = profile.Name,
                ModCount = profile.Mods.Count,
                IsActive = profile.Id == data.ActiveProfileId,
            });
    }

    [RelayCommand] private void Back() => back();

    [RelayCommand]
    private void Select(ProfileItem? item)
    {
        if (item is null) return;
        store.SetActive(gameId, item.Id);
        Reload();
    }

    [RelayCommand]
    private void NewProfile()
    {
        store.AddProfile(gameId, $"Profil {store.For(gameId).Profiles.Count + 1}");
        Reload();
    }
}

public partial class ProfileItem : ViewModelBase
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int ModCount { get; set; }
    [ObservableProperty] private bool isActive;
}

public partial class BackupViewModel : ViewModelBase
{
    private readonly ProfileStore store = new();
    private readonly Action back;
    private readonly string gameId;

    public BackupViewModel(GameCatalogItem game, Action back)
    {
        this.back = back;
        gameId = game.Definition.Slug;
        GameTitle = game.Title;
        Reload();
    }

    public string GameTitle { get; }
    public ObservableCollection<BackupItem> Backups { get; } = [];
    [ObservableProperty] private string status = "";

    private void Reload()
    {
        Backups.Clear();
        foreach (var backup in store.Backups(gameId))
            Backups.Add(new BackupItem
            {
                Id = backup.Id,
                Name = backup.Name,
                Date = backup.CreatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm"),
                Size = $"{backup.SizeBytes / 1024.0:F0} KB",
                Kind = backup.Kind,
                Entry = backup,
            });
    }

    [RelayCommand] private void Back() => back();

    [RelayCommand]
    private void Create()
    {
        store.CreateBackup(gameId, $"{LocalizationService.Instance["Backup.Manual"]} – {DateTime.Now:HH:mm}", "manual");
        Reload();
        Status = LocalizationService.Instance["Backup.Created"];
    }

    [RelayCommand]
    private void Restore(BackupItem? item)
    {
        if (item is null) return;
        store.Restore(gameId, item.Entry);
        Status = LocalizationService.Instance["Backup.Restored"];
    }
}

public partial class BackupItem : ViewModelBase
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Date { get; set; } = "";
    public string Size { get; set; } = "";
    public string Kind { get; set; } = "";
    public BackupEntry Entry { get; set; } = new();
}
