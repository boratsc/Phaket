using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

// PAK-89: the mod counter in the catalog must be pluralized per language instead of
// a hardcoded Polish "modów".
public sealed class PluralCountTests
{
    private static string Text(int count, string language)
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = language;
        return (string)PluralCountConverter.Instance.Convert(
            [count, loc["Catalog.ModsCount.One"], loc["Catalog.ModsCount.Few"], loc["Catalog.ModsCount.Many"]],
            typeof(string), null, CultureInfo.InvariantCulture)!;
    }

    [AvaloniaTheory]
    [InlineData(0, "0 modów")]
    [InlineData(1, "1 mod")]
    [InlineData(2, "2 mody")]
    [InlineData(4, "4 mody")]
    [InlineData(5, "5 modów")]
    [InlineData(12, "12 modów")]
    [InlineData(13, "13 modów")]
    [InlineData(14, "14 modów")]
    [InlineData(22, "22 mody")]
    [InlineData(112, "112 modów")]
    [InlineData(122, "122 mody")]
    [InlineData(114, "114 modów")]
    public void Polish_uses_one_few_many_forms(int count, string expected)
    {
        var previous = LocalizationService.Instance.CurrentLanguage;
        try { Assert.Equal(expected, Text(count, "pl-PL")); }
        finally { LocalizationService.Instance.CurrentLanguage = previous; }
    }

    [AvaloniaTheory]
    [InlineData(0, "0 mods")]
    [InlineData(1, "1 mod")]
    [InlineData(2, "2 mods")]
    public void English_uses_one_and_other_forms(int count, string expected)
    {
        var previous = LocalizationService.Instance.CurrentLanguage;
        try { Assert.Equal(expected, Text(count, "en")); }
        finally { LocalizationService.Instance.CurrentLanguage = previous; }
    }

    [AvaloniaFact]
    public void Catalog_row_counter_is_localized_and_follows_language_switch()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        var root = TestAccounts.TempPath();
        try
        {
            new ModpackStore(root).Save(new ModpackDefinition
            {
                Id = "pack", Name = "Pakiet", Author = "Test", GameId = "among-us",
                Items = [new ModpackMod { Name = "A", Version = "1", Source = "thunderstore" }, new ModpackMod { Name = "B", Version = "1", Source = "thunderstore" }]
            });
            loc.CurrentLanguage = "pl-PL";
            var game = new GameCatalogItem(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            using var vm = new ModpackCatalogViewModel(game, root, autoLoad: false);
            var window = new Window { Width = 1280, Height = 800, Content = new ModpackCatalogView { DataContext = vm } };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("2 mody", Texts(window));
                loc.CurrentLanguage = "en";
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("2 mods", Texts(window));
            }
            finally { window.Close(); }
        }
        finally
        {
            loc.CurrentLanguage = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Game_active_mods_forms_are_localized()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        string[] forms() => new[] { "Game.ActiveModsCount.One", "Game.ActiveModsCount.Few", "Game.ActiveModsCount.Many" }.Select(key => loc[key]).ToArray();
        try
        {
            loc.CurrentLanguage = "pl-PL";
            Assert.Equal(["aktywny mod", "aktywne mody", "aktywnych modów"], forms());
            loc.CurrentLanguage = "en";
            Assert.Equal(["active mod", "active mods", "active mods"], forms());
        }
        finally { loc.CurrentLanguage = previous; }
    }

    [AvaloniaFact]
    public void Modpack_subtitle_uses_plural_forms_and_follows_language()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        var root = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(root);
            var pack = new ModpackDefinition
            {
                Id = "pack", Name = "Pakiet", Author = "Test", Version = "1.0.0", GameId = "among-us",
                Items = [new ModpackMod { Name = "A", Version = "1", Source = "thunderstore" }]
            };
            store.Save(pack);
            loc.CurrentLanguage = "pl-PL";
            var vm = new ModPackViewModel(pack, store, () => { }, root);
            var window = new Window { Width = 1280, Height = 800, Content = new ModPackView { DataContext = vm } };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("1 mod w pakiecie", Texts(window));
                Assert.Contains("1 włączony", Texts(window));
                loc.CurrentLanguage = "en";
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("1 mod in pack", Texts(window));
                Assert.Contains("1 enabled", Texts(window));
            }
            finally { window.Close(); }
        }
        finally
        {
            loc.CurrentLanguage = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Catalog_contents_label_uses_the_counted_noun_in_both_languages()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        var root = TestAccounts.TempPath();
        try
        {
            new ModpackStore(root).Save(new ModpackDefinition
            {
                Id = "pack", Name = "Pakiet", Author = "Test", GameId = "among-us",
                Items = [new ModpackMod { Name = "A", Version = "1", Source = "thunderstore" }]
            });
            loc.CurrentLanguage = "pl-PL";
            var game = new GameCatalogItem(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            using var vm = new ModpackCatalogViewModel(game, root, autoLoad: false);
            vm.SelectedPack = vm.LocalPacks.Single();
            Assert.Equal("Skład pakietu · 1 mod", vm.ContentsLabel);
            loc.CurrentLanguage = "en";
            Assert.Equal("Modpack contents · 1 mod", vm.ContentsLabel);
        }
        finally
        {
            loc.CurrentLanguage = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // Renders every counter that used to say "1 modów" and checks the text is complete in both languages.
    [AvaloniaFact]
    public void Counters_render_the_counted_noun_and_follow_the_language()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        try
        {
            loc.CurrentLanguage = "pl-PL";
            var game = new GameCatalogItem(new Phaket.Core.Omrl.GameDefinition("among-us", [], MainExecutable: "game.exe"), "", "")
            { ModCount = 1, ProfileName = "Profil" };

            var library = new LibraryViewModel(_ => { }, TestAccounts.Create(), null);
            library.Games.Add(game);
            library.SelectedGame = game;
            var libraryWindow = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = library } };
            libraryWindow.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("1 mod", Texts(libraryWindow));
                AssertNotClipped(libraryWindow, "1 mod");
                Save(libraryWindow, "45-counter-library.png");
            }
            finally { libraryWindow.Close(); }

            var gameVm = new GameViewModel(game, () => { });
            game.EpicInstallPath = root;
            game.ModCount = 1;
            var gameWindow = new Window { Width = 1280, Height = 800, Content = new GameView { DataContext = gameVm } };
            gameWindow.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("1 aktywny mod", Texts(gameWindow));
                AssertNotClipped(gameWindow, "1 aktywny mod");
                Save(gameWindow, "46-counter-game.png");
                game.ModCount = 5;
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("5 aktywnych modów", Texts(gameWindow));
                AssertNotClipped(gameWindow, "5 aktywnych modów");
                Save(gameWindow, "47-counter-game-many.png");
                loc.CurrentLanguage = "en";
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("5 active mods", Texts(gameWindow));
                AssertNotClipped(gameWindow, "5 active mods");
            }
            finally { gameWindow.Close(); }

            loc.CurrentLanguage = "pl-PL";
            var profile = new ProfileStore(root).AddProfile("among-us", "Modded",
                [new ModEntry { Name = "A", Version = "1" }, new ModEntry { Name = "B", Version = "1" }]);
            var profilesVm = new ProfilesViewModel(game, () => { }, root);
            profilesVm.SelectCommand.Execute(profilesVm.Profiles.Single(item => item.Id == profile.Id));
            var profilesWindow = new Window { Width = 1280, Height = 800, Content = new ProfilesView { DataContext = profilesVm } };
            profilesWindow.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("2 mody", Texts(profilesWindow));
                AssertNotClipped(profilesWindow, "2 mody");
                Save(profilesWindow, "48-counter-profiles.png");
                loc.CurrentLanguage = "en";
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain("2 mody", Texts(profilesWindow));
                Assert.Contains("2 mods", Texts(profilesWindow));
            }
            finally { profilesWindow.Close(); profilesVm.Dispose(); }

            loc.CurrentLanguage = "pl-PL";
            new ModpackStore(root).Save(new ModpackDefinition
            {
                Id = "pack", Name = "Pakiet", Author = "Test", GameId = "among-us",
                Items = [new ModpackMod { Name = "A", Version = "1", Source = "thunderstore" }]
            });
            using var catalogVm = new ModpackCatalogViewModel(game, root, autoLoad: false);
            catalogVm.SelectedPack = catalogVm.LocalPacks.Single();
            var catalogWindow = new Window { Width = 1280, Height = 800, Content = new ModpackCatalogView { DataContext = catalogVm } };
            catalogWindow.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("Skład pakietu · 1 mod", Texts(catalogWindow));
                AssertNotClipped(catalogWindow, "Skład pakietu · 1 mod");
                Save(catalogWindow, "49-counter-catalog.png");
                loc.CurrentLanguage = "en";
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("Modpack contents · 1 mod", Texts(catalogWindow));
            }
            finally { catalogWindow.Close(); }
        }
        finally
        {
            loc.CurrentLanguage = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Save(Window window, string name)
    {
        const string dir = "/tmp/opencode/phaket-render";
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        frame.Save(Path.Combine(dir, name));
    }

    private static void AssertNotClipped(Window window, string text)
    {
        var block = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);
        Assert.True(block.Bounds.Width >= block.DesiredSize.Width - 1 && block.Bounds.Height >= block.DesiredSize.Height - 1,
            $"'{text}' clipped: bounds={block.Bounds.Size}, desired={block.DesiredSize}");
    }

    private static HashSet<string?> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToHashSet();
}