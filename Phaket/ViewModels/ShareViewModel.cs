using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Profiles;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class ShareViewModel(GameCatalogItem game, Action back) : ViewModelBase
{
    private ProfileSharePackage? incoming;
    public string Title => game.Title;
    public ObservableCollection<string> PreviewAdded { get; } = [];
    public ObservableCollection<string> PreviewRemoved { get; } = [];
    public ObservableCollection<string> PreviewChanged { get; } = [];
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasPreview;
    [ObservableProperty] private string incomingName = "";

    private static IStorageProvider? Storage =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;

    [RelayCommand] private void Back() => back();

    [RelayCommand]
    private async Task ExportAsync()
    {
        try
        {
            StatusMessage = "";
            var profile = new ProfileStore().Active(game.Definition.Slug);
            if (profile is null)
            {
                StatusMessage = LocalizationService.Instance["Share.NoProfile"];
                return;
            }
            var json = ProfileShareService.Export(game.Definition.Slug, profile.Name, profile.Mods.Select(m => (m.Name, m.Version)));
            var storage = Storage ?? throw new InvalidOperationException();
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["Share.Export"],
                SuggestedFileName = "profile.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
                ShowOverwritePrompt = true,
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            await writer.FlushAsync();
            StatusMessage = LocalizationService.Instance["Share.Exported"];
        }
        catch (Exception) { StatusMessage = LocalizationService.Instance["Share.Error"]; }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        try
        {
            StatusMessage = "";
            var storage = Storage ?? throw new InvalidOperationException();
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["Share.Import"],
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0) return;
            HasPreview = false;
            incoming = null;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var package = ProfileShareService.Parse(await reader.ReadToEndAsync());
            if (!string.Equals(package.GameId, game.Definition.Slug, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(package.ProfileName) || package.Mods is null ||
                package.Mods.Any(m => m is null || string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Version)))
                throw new InvalidDataException();
            var current = new ProfileStore().Active(game.Definition.Slug);
            var diff = ProfileShareService.Diff(current?.Mods.Select(m => (m.Name, m.Version)), package);
            Replace(PreviewAdded, diff.Added);
            Replace(PreviewRemoved, diff.Removed);
            Replace(PreviewChanged, diff.Changed);
            incoming = package;
            IncomingName = package.ProfileName;
            HasPreview = true;
        }
        catch (Exception) { StatusMessage = LocalizationService.Instance["Share.Error"]; }
    }

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    [RelayCommand]
    private void ConfirmImport()
    {
        if (!HasPreview || incoming is null) return;
        try
        {
            var store = new ProfileStore();
            var profile = store.AddProfile(game.Definition.Slug, incoming.ProfileName + " (import)");
            profile.Mods.AddRange(incoming.Mods.Select(m => new ModEntry { Name = m.Name, Version = m.Version }));
            store.Save();
            incoming = null;
            HasPreview = false;
            StatusMessage = LocalizationService.Instance["Share.Imported"];
        }
        catch (Exception) { HasPreview = false; incoming = null; StatusMessage = LocalizationService.Instance["Share.Error"]; }
    }

    [RelayCommand]
    private void CancelImport() { incoming = null; HasPreview = false; }
}
