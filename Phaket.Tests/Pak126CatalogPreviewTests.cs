using System.IO.Compression;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-126: Thunderstore rows carry artifacts with sha256=null (the v1 API publishes no hash).
// PAK-127: the plan preview must name the destination path, not the archive entry.
public sealed class Pak126CatalogPreviewTests
{
    private const string Url = "https://thunderstore.io/package/download/Owner/Mod/1.0.0/";

    private static byte[] Zip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in new[]
                     {
                         ("CHANGELOG.md", "changes"),
                         ("plugins/OwnerMod.dll", "dll"),
                         ("config/OwnerMod.cfg", "cfg")
                     })
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        return buffer.ToArray();
    }

    private static ThunderstoreMod Mod(byte[] zip) => new("id", "among-us", "Owner", "Mod", "Owner-Mod",
        "desc", "Owner", "https://thunderstore.io/c/among-us/p/Owner/Mod/", null, false, DateTimeOffset.UnixEpoch,
        new ThunderstoreVersion("1.0.0", "Owner-Mod-1.0.0", Url, zip.Length, DateTimeOffset.UnixEpoch, [],
            Artifacts: [new ModArtifact(Url, null, [], null, zip.Length, "thunderstore", "Owner-Mod-1.0.0")],
            Packaging: null, Source: "thunderstore", Availability: "available"),
        Packaging: null, Source: "thunderstore", ExternalId: "id");

    private sealed class Catalog(ThunderstoreMod mod) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            object body = path.StartsWith("/mods/index", StringComparison.Ordinal)
                ? new { revision = 1, mods = new[] { mod }, nextCursor = (string?)null }
                : path.StartsWith("/mods/changes", StringComparison.Ordinal)
                    ? new { revision = 1, reset = false, changes = Array.Empty<object>() }
                    : new { mod, versions = new[] { mod.Latest } };
            return Task.FromResult(new CatalogResponse(200, null,
                JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
    }

    private sealed class Packages(string url, byte[] bytes) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(source == url
                ? new MemoryStream(bytes, writable: false)
                : throw new InvalidOperationException($"unexpected download: {source}"));
    }

    private sealed class Receipts : IReceiptTransport
    {
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static InstallPreparation Preparation() => new(
        "among-us", "Owner-Mod", "1.0.0", "http://localhost", new("", "steam", true),
        [new OperationPlan("1", new RecipeRef("mod", "2"), "i", "p",
            [new PlannedOperation("op", "file.copy", "game://BepInEx/plugins/OwnerMod.dll", null, null, null, [], "", "", "", [])],
            [], [], false, [])],
        [], [], [], "/tmp/sandbox", "", "", "", "");

    // PAK-126: neither the "Update" action nor the install button may treat a hash-less
    // Thunderstore row as unavailable.
    [AvaloniaFact]
    public async Task Thunderstore_version_without_a_hash_stays_installable_and_updatable()
    {
        var zip = Zip();
        var mod = Mod(zip);
        var root = TestAccounts.TempPath();
        ModCatalogViewModel? vm = null;
        try
        {
            vm = new ModCatalogViewModel("among-us", root, new Catalog(mod), false);
            await vm.RefreshCommand.ExecuteAsync(null);

            var version = Assert.Single(vm.Mods).Latest;
            Assert.True(version.IsAvailable);
            Assert.Equal("1.0.0", vm.LatestVersion("Owner-Mod"));

            var prepared = false;
            vm.SelectedMod = vm.Mods[0];
            vm.PrepareOverride = (_, _, _, _, _) => { prepared = true; return Task.FromResult(Preparation()); };
            await vm.InstallVersionCommand.ExecuteAsync(version);

            Assert.True(prepared);
            Assert.NotEqual(LocalizationService.Instance["ModInstall.VersionUnavailable"], vm.InstallStatus);
        }
        finally
        {
            // Selecting the row starts a details lookup that holds modcatalog.db; the fixture
            // directory cannot be removed on Windows until that task closes the store.
            if (vm?.PendingDetails is { } pending) await pending;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // PAK-127: the preview maps every archive entry to the game path the plan writes, so a
    // user never reads "CHANGELOG.md" as if it were the installed file name.
    [Fact]
    public async Task Plan_preview_pairs_archive_entries_with_their_destination_paths()
    {
        var zip = Zip();
        var mod = Mod(zip);
        var root = Directory.CreateTempSubdirectory("pak127-preview-").FullName;
        try
        {
            using var store = new ModCatalogStore(Path.Combine(root, "catalog.db"));
            var service = new ModInstallService(root, new ModCatalogClient(new Catalog(mod), store),
                new Packages(Url, zip), new Receipts());

            var preparation = await service.PrepareAsync("among-us", "Owner-Mod", "1.0.0", "",
                new("", "steam", true, "x64"), ct: TestContext.Current.CancellationToken);

            var preview = Assert.Single(preparation.Preview);
            var entries = preview.Entries!;
            Assert.Contains(entries, entry => entry.Source == "CHANGELOG.md"
                && entry.Target == "BepInEx/plugins/Owner-Mod/CHANGELOG.md");
            Assert.Contains(entries, entry => entry.Source == "plugins/OwnerMod.dll"
                && entry.Target == "BepInEx/plugins/Owner-Mod/OwnerMod.dll");
            Assert.Contains(entries, entry => entry.Source == "config/OwnerMod.cfg"
                && entry.Target == "BepInEx/config/OwnerMod.cfg");
            // Files stays the destination list, and the plan writes exactly those targets.
            Assert.Equal(entries.Select(entry => entry.Target), preview.Files);
            var planned = Assert.Single(preparation.Plans).Operations
                .Where(operation => operation.Kind == "file.copy" && operation.Parameters is not null)
                .Select(operation => operation.Parameters!.GetValueOrDefault("to") ?? "")
                .Where(to => to.StartsWith("game://", StringComparison.Ordinal))
                .Select(to => to["game://".Length..]);
            Assert.Equal(planned.Order(StringComparer.Ordinal), preview.Files.Order(StringComparer.Ordinal));

            var outcome = await service.ExecuteAsync(preparation, true, ct: TestContext.Current.CancellationToken);
            Assert.True(outcome.Success, outcome.ErrorCode);
            var gameRoot = Path.Combine(preparation.SandboxRoot, "game");
            foreach (var file in preview.Files)
                Assert.True(File.Exists(Path.Combine(gameRoot, file.Replace('/', Path.DirectorySeparatorChar))),
                    $"missing installed file: {file}");
        }
        finally { Directory.Delete(root, true); }
    }

    // PAK-127 follow-up: a pack with a dozen DLLs must not print the same warning a dozen times.
    [AvaloniaFact]
    public async Task Executable_library_warnings_are_summarized_once()
    {
        var vm = new ModCatalogViewModel("among-us", TestAccounts.TempPath(), autoSync: false);
        var plan = Preparation().Plans[0] with
        {
            Warnings = [.. Enumerable.Range(0, 13).Select(i => $"executable-library:game://BepInEx/core/Lib{i}.dll"), "ARCH_UNKNOWN"]
        };
        vm.PrepareOverride = (_, _, _, _, _) => Task.FromResult(Preparation() with { Plans = [plan] });

        await vm.BeginInstallAsync("Owner-Mod", "1.0.0", new("", "steam", true));

        Assert.Equal(2, vm.PlanWarnings.Count);
        Assert.Contains(LocalizationService.Instance["ModInstall.WarningArchUnknown"], vm.PlanWarnings);
        Assert.Contains(string.Format(LocalizationService.Instance["ModInstall.WarningExecutableLibrary"], 13), vm.PlanWarnings);
    }

    // The XAML preview binding is verified by rendering the real ModsView at the target size.
    [AvaloniaTheory]
    [InlineData("pl-PL", "56-mods-plan-preview-pl-PL.png")]
    [InlineData("en", "56-mods-plan-preview-en.png")]
    public async Task Plan_preview_renders_the_destination_path_in_both_languages(string locale, string file)
    {
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        var language = LocalizationService.Instance.CurrentLanguage;
        Window? window = null;
        try
        {
            LocalizationService.Instance.CurrentLanguage = locale;
            // Keep the catalog refresh offline: the view model starts it in its constructor.
            File.WriteAllText(Path.Combine(root, "settings.json"), """{"catalogEndpoint":"http://127.0.0.1:1"}""");
            var game = new GameCatalogItem(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            var vm = new ModsViewModel(game, () => { }, root);
            if (vm.Catalog.RefreshCommand.ExecutionTask is { } refresh) await refresh;

            var preparation = Preparation() with
            {
                Preview = [new InstallPreview("Owner-Mod", "1.0.0",
                    ["BepInEx/plugins/Owner-Mod/CHANGELOG.md"], 7,
                    [new InstallPreviewEntry("CHANGELOG.md", "BepInEx/plugins/Owner-Mod/CHANGELOG.md")])]
            };
            vm.Catalog.PrepareOverride = (_, _, _, _, _) => Task.FromResult(preparation);
            await vm.Catalog.BeginInstallAsync("Owner-Mod", "1.0.0", new("", "steam", true));
            Assert.True(vm.Catalog.HasPreview);
            vm.SelectedTabIndex = 1;

            window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.ModsView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text))
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("CHANGELOG.md → BepInEx/plugins/Owner-Mod/CHANGELOG.md", texts);

            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = window.CaptureRenderedFrame();
            frame!.Save($"/tmp/opencode/phaket-render/{file}");
        }
        finally
        {
            window?.Close();
            LocalizationService.Instance.CurrentLanguage = language;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}