using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Phaket.Core.Accounts;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

public class LibraryInstallFlowTests
{
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
    private static LegendaryInstallPlan Plan(string root, IReadOnlyList<string>? conflicts = null) =>
        new(Game.AppName, root, Path.Combine(root, Game.AppName), null, "game.exe", conflicts ?? []);

    [AvaloniaFact]
    public async Task Missing_consent_acceptance_or_overwrite_blocks_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-flow-" + Guid.NewGuid());
        var runner = new Runner();
        var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(Tokens()), runner, Path.Combine(root, "config"));
        SetBinary(adapter, "fake");
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
    }

    [AvaloniaFact]
    public async Task Successful_install_refreshes_owned_and_recorded_installations_and_exposes_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-flow-" + Guid.NewGuid());
        try
        {
            var runner = new Runner();
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(Tokens()), runner, Path.Combine(root, "config"));
            SetBinary(adapter, "fake");
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

    [AvaloniaTheory]
    [InlineData("LEGENDARY_NOT_FOUND", "LegendaryNotFound")]
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
                var adapter = new LegendaryInstallAdapter(_ => code == "EPIC_TOKEN_INVALID"
                    ? Task.FromException<LegendaryTokens>(new PhaketException(code)) : Task.FromResult(Tokens()), runner, Path.Combine(root, "config"));
                SetBinary(adapter, code == "LEGENDARY_NOT_FOUND" ? null : "fake");
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
        Assert.True(item.EpicOwned); Assert.False(item.IsSupported); Assert.False(item.CanOpenGame);
        Assert.True(item.EpicOwnedNotInstalled);
        Assert.False(item.CanInstallEpic); Assert.True(item.EpicUnsupported);
        var noExecutable = new GameCatalogItem(new GameDefinition("supported", []), "", "") { EpicOwned = true };
        Assert.False(noExecutable.CanInstallEpic); Assert.True(noExecutable.EpicUnsupported);
        var language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            foreach (var locale in new[] { "pl-PL", "en" })
            {
                LocalizationService.Instance.CurrentLanguage = locale;
                Assert.NotEqual("Library.State.Unsupported", LocalizationService.Instance["Library.State.Unsupported"]);
            }
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; }
        var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var controls = window.GetVisualDescendants().OfType<Control>().Where(c => ReferenceEquals(c.DataContext, item)).ToList();
        Assert.Contains(controls.OfType<TextBlock>(), text => text.IsVisible && text.Text == LocalizationService.Instance["Library.State.Unsupported"]);
        Assert.DoesNotContain(controls.OfType<Button>(), button => button.IsVisible && Equals(button.Content, LocalizationService.Instance["Library.Install"]));
        window.Close();
        await vm.PrepareGameInstallCommand.ExecuteAsync(item);
        Assert.Null(vm.InstallPlan);
    }
}
