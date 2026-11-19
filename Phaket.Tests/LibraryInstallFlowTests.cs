using System.Net.Http;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Phaket.Core.Accounts;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Tools;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Material.Icons.Avalonia;

namespace Phaket.Tests;

public class LibraryInstallFlowTests
{
    // Every request fails as if there were no network, so ToolProvider reports TOOL_OFFLINE.
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("no network"));
    }

    private sealed class Account : IAccountService
    {
        public string Provider => "epic";
        public AccountInfo? Current { get; } = new("epic", "id", "name", null, DateTimeOffset.UtcNow);
        public event Action? AccountChanged { add { } remove { } }
        public Task<AccountInfo> LoginAsync(CancellationToken ct = default) => Task.FromResult(Current!);
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Runner : IProcessRunner
    {
        public List<string>? Arguments;
        public int ExitCode;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, Action<string> output, CancellationToken cancellationToken)
        {
            Arguments = arguments.ToList();
            output("installer output");
            if (ExitCode == 0)
            {
                var target = Path.Combine(arguments[3], arguments[1]);
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "game.exe"), "fixture");
            }
            return Task.FromResult(ExitCode);
        }
    }

    private static readonly OwnedEpicGame Game = new("963137e4c29d4c79a81323b8fab03a40", "", "", "Among Us", true, "assets");
    private static void Inject(LibraryViewModel vm, LegendaryInstallAdapter adapter) =>
        typeof(LibraryViewModel).GetField("installAdapter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, adapter);
    private static void SetBinary(LegendaryInstallAdapter adapter, string? value) =>
        typeof(LegendaryInstallAdapter).GetField("binary", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adapter, value);
    private static LegendaryTokens Tokens() => new("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "id", "name");
    // Core checks the binary exists before running it, and a missing one starts a real download:
    // the fixture is a file on disk that the fake runner never opens.
    private static string FakeBinary(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "legendary-fake");
        File.WriteAllText(path, "fixture");
        return path;
    }
    private static LegendaryInstallPlan Plan(string root, IReadOnlyList<string>? conflicts = null) =>
        new(Game.AppName, root, Path.Combine(root, Game.AppName), null, "game.exe", conflicts ?? []);

    [AvaloniaFact]
    public async Task Missing_consent_acceptance_or_overwrite_blocks_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-flow-" + Guid.NewGuid());
        var runner = new Runner();
        var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(Tokens()), runner, Path.Combine(root, "config"));
        SetBinary(adapter, FakeBinary(root + "-bin"));
        var vm = new LibraryViewModel(_ => { }, new Account(), null, _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([Game]), () => []);
        Inject(vm, adapter);
        vm.InstallPlan = Plan(root, ["conflict"]);
        var language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            foreach (var locale in new[] { "pl-PL", "en" })
            {
                LocalizationService.Instance.CurrentLanguage = locale;
                vm.TokenConsent = vm.PlanAccepted = vm.OverwriteConsent = false;
                Assert.True(vm.ExecuteGameInstallCommand.CanExecute(null));
                await vm.ExecuteGameInstallCommand.ExecuteAsync(null);
                Assert.Equal(LocalizationService.Instance["Library.InstallError.ConsentRequired"], vm.Status);
                Assert.NotEqual("Library.InstallError.ConsentRequired", vm.Status);
                vm.TokenConsent = true;
                await vm.ExecuteGameInstallCommand.ExecuteAsync(null);
                Assert.Equal(LocalizationService.Instance["Library.InstallError.PlanNotAccepted"], vm.Status);
                Assert.NotEqual("Library.InstallError.PlanNotAccepted", vm.Status);
                vm.PlanAccepted = true;
                await vm.ExecuteGameInstallCommand.ExecuteAsync(null);
                Assert.Equal(LocalizationService.Instance["Library.InstallError.OverwriteRequired"], vm.Status);
                Assert.NotEqual("Library.InstallError.OverwriteRequired", vm.Status);
                Assert.Null(runner.Arguments);
            }
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; }
        vm.OverwriteConsent = true;
        Assert.True(vm.ExecuteGameInstallCommand.CanExecute(null));
        Assert.Equal("", vm.InstallError);
        Assert.False(Directory.Exists(root));
        Directory.Delete(root + "-bin", true);
    }

    [AvaloniaFact]
    public async Task Successful_install_refreshes_owned_and_recorded_installations_and_exposes_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-flow-" + Guid.NewGuid());
        try
        {
            var runner = new Runner();
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(Tokens()), runner, Path.Combine(root, "config"));
            SetBinary(adapter, FakeBinary(root));
            int ownedCalls = 0, recordCalls = 0;
            var vm = new LibraryViewModel(_ => { }, new Account(), null,
                _ => { ownedCalls++; return Task.FromResult<IReadOnlyList<OwnedEpicGame>>([Game]); },
                () => { recordCalls++; return adapter.GetRecordedInstallations(); });
            Inject(vm, adapter);
            await vm.ScanCommand.ExecuteAsync(null);
            var plan = Plan(Path.Combine(root, "games"));
            vm.InstallPlan = plan; vm.TokenConsent = true; vm.PlanAccepted = true;
            await vm.ExecuteGameInstallCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "install", plan.AppName, "--base-path", plan.BasePath, "--yes" }, runner.Arguments);
            Assert.Equal(2, ownedCalls); Assert.Equal(2, recordCalls);
            var game = Assert.Single(vm.Games, g => g.Definition.Slug == "among-us");
            Assert.True(game.EpicInstalled); Assert.Equal("epic", game.SelectedInstallation?.Store);
            Assert.Equal(plan.TargetPath, game.EpicInstallPath);
            Assert.Contains("installer output", vm.InstallLog);
            Assert.Null(vm.InstallPlan); Assert.Equal("", vm.InstallError);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // A missing binary is no longer an error: the plan builds and the run asks ToolProvider to
    // fetch the tool, so the failure the user sees comes from the download (TOOL_OFFLINE).
    [AvaloniaTheory]
    [InlineData("TOOL_OFFLINE", "Failed")]
    [InlineData("LEGENDARY_INSTALL_FAILED", "Failed")]
    [InlineData("EPIC_TOKEN_INVALID", "TokenInvalid")]
    [InlineData("PATH_OUT_OF_BOUNDS", "PathOutOfBounds")]
    public async Task Adapter_failure_is_localized_and_does_not_refresh_library(string code, string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-flow-" + Guid.NewGuid());
        var language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            foreach (var locale in new[] { "pl-PL", "en" })
            {
                LocalizationService.Instance.CurrentLanguage = locale;
                var runner = new Runner { ExitCode = code == "LEGENDARY_INSTALL_FAILED" ? 1 : 0 };
                // The offline ToolProvider is injected in every case: no test of this flow may reach
                // the network, and a binary that does not exist on disk would make Core download one.
                var adapter = new LegendaryInstallAdapter(_ => code == "EPIC_TOKEN_INVALID"
                    ? Task.FromException<LegendaryTokens>(new PhaketException(code)) : Task.FromResult(Tokens()), runner, Path.Combine(root, "config"),
                    toolProvider: new ToolProvider(Path.Combine(root, "tools"), new HttpClient(new OfflineHandler())));
                SetBinary(adapter, code == "TOOL_OFFLINE" ? null : FakeBinary(root));
                int ownedCalls = 0, recordCalls = 0;
                var vm = new LibraryViewModel(_ => { }, new Account(), null,
                    _ => { ownedCalls++; return Task.FromResult<IReadOnlyList<OwnedEpicGame>>([Game]); },
                    () => { recordCalls++; return []; });
                Inject(vm, adapter);
                await vm.ScanCommand.ExecuteAsync(null);
                var item = Assert.Single(vm.Games, g => g.Definition.Slug == "among-us");
                var plan = Plan(Path.Combine(root, "games"));
                if (code == "PATH_OUT_OF_BOUNDS") plan = plan with { TargetPath = Path.Combine(root, "outside") };
                vm.InstallPlan = plan; vm.TokenConsent = true; vm.PlanAccepted = true;
                await vm.ExecuteGameInstallCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(LocalizationService.Instance["Library.InstallError." + key], vm.InstallError);
                Assert.NotEqual("Library.InstallError." + key, vm.InstallError);
                Assert.Equal(1, ownedCalls); Assert.Equal(1, recordCalls);
                Assert.True(item.EpicOwned); Assert.False(item.EpicInstalled);
                Assert.Same(plan, vm.InstallPlan);
                if (code == "LEGENDARY_INSTALL_FAILED") Assert.Contains("installer output", vm.InstallLog);
            }
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Unknown_owned_game_is_shown_as_unsupported_without_crashing()
    {
        var other = new OwnedEpicGame("unknown-app", "", "unknown-id", "Unknown game", true, "assets");
        var vm = new LibraryViewModel(_ => { }, new Account(), null, _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([other]), () => []);
        await vm.ScanCommand.ExecuteAsync(null);
        var item = Assert.Single(vm.Games, g => g.Title == "Unknown game");
        vm.SelectedGame = item;
        Assert.True(item.EpicOwned); Assert.False(item.IsSupported); Assert.False(item.CanOpenGame);
        Assert.True(item.EpicOwnedNotInstalled);
        Assert.False(item.CanInstallEpic); Assert.True(item.EpicUnsupported); Assert.True(item.UnsupportedOwned);
        var noExecutable = new GameCatalogItem(new GameDefinition("supported", []), "", "") { EpicOwned = true };
        Assert.False(noExecutable.CanInstallEpic); Assert.True(noExecutable.EpicUnsupported);
        var language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            foreach (var locale in new[] { "pl-PL", "en" })
            {
                LocalizationService.Instance.CurrentLanguage = locale;
                Assert.NotEqual("Ui.Library.Unsupported", LocalizationService.Instance["Ui.Library.Unsupported"]);
            }
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; }
        var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var row = window.GetVisualDescendants().OfType<ListBoxItem>().Single(listItem => ReferenceEquals(listItem.DataContext, item));
        var rowText = row.GetVisualDescendants().OfType<TextBlock>().ToArray();
        var localizedUnsupported = LocalizationService.Instance["Ui.Library.Unsupported"];
        Assert.Contains(rowText, text => text.IsVisible && text.Text == localizedUnsupported);
        Assert.NotEqual("Ui.Library.Unsupported", localizedUnsupported);
        Assert.Contains(row.GetVisualDescendants().OfType<MaterialIcon>(), icon => icon.IsVisible);
        Assert.False(item.CanOpenGame);
        window.Close();
        await vm.PrepareGameInstallCommand.ExecuteAsync(item);
        Assert.Null(vm.InstallPlan);
    }
}
