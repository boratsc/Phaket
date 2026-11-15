using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Accounts;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

public class LibraryEpicTests
{
    [AvaloniaFact]
    public void Install_plan_requires_all_consents_and_renders_conflicts()
    {
        var vm = Create(new Account(), _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([]));
        vm.InstallPlan = new("example-app", "/home/user/Games", "/home/user/Games/example-app", 1024, "game.exe", ["existing files"]);
        Assert.True(vm.ExecuteGameInstallCommand.CanExecute(null));
        vm.TokenConsent = true; vm.PlanAccepted = true;
        Assert.True(vm.ExecuteGameInstallCommand.CanExecute(null));
        vm.OverwriteConsent = true;
        Assert.True(vm.ExecuteGameInstallCommand.CanExecute(null));
        var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
        window.Show(); Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory("/tmp/opencode/phaket-render");
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save("/tmp/opencode/phaket-render/library-install-plan.png");
        window.Close();
    }
    private sealed class Account : IAccountService
    {
        public string Provider => "epic";
        public AccountInfo? Current { get; private set; }
        public event Action? AccountChanged;
        public Task<AccountInfo> LoginAsync(CancellationToken ct = default)
        {
            Current = new("epic", "id", "name", null, DateTimeOffset.UtcNow);
            AccountChanged?.Invoke();
            return Task.FromResult(Current);
        }
        public Task LogoutAsync(CancellationToken ct = default) { Current = null; AccountChanged?.Invoke(); return Task.CompletedTask; }
    }

    private static readonly OwnedEpicGame AmongUs = new("963137e4c29d4c79a81323b8fab03a40", "", "", "Among Us", true, "assets");
    private static readonly OwnedEpicGame Other = new("other", "", "other-id", "Other Epic Game", true, "assets");
    private static LibraryViewModel Create(Account account, Func<AccountInfo, Task<IReadOnlyList<OwnedEpicGame>>> fetch,
        Func<IReadOnlyList<LegendaryRecordedInstallation>>? records = null) =>
        new(_ => { }, account, () => { }, fetch, records ?? (() => []));

    [AvaloniaFact]
    public async Task Owned_games_merge_with_verified_record_and_keep_unsupported_titles()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-owned-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "game.exe"), "fixture");
            var account = new Account();
            await account.LoginAsync();
            var vm = Create(account, _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([AmongUs, Other]),
                () => [new(AmongUs.AppName, root, "game.exe", DateTimeOffset.UtcNow)]);
            await vm.ScanCommand.ExecuteAsync(null);
            var item = Assert.Single(vm.Games, g => g.Definition.Slug == "among-us");
            Assert.True(item.EpicOwned);
            Assert.True(item.EpicInstalled);
            Assert.True(item.IsInstalled);
            Assert.True(item.CanOpenGame);
            Assert.Equal(root, item.RootPath);
            Assert.Equal("epic", item.SelectedInstallation?.Store);
            var catalog = new ModCatalogViewModel(item.Definition.Slug, root, autoSync: false, game: item);
            var target = Assert.Single(catalog.Targets, t => t.Target.Store == "epic");
            Assert.Equal(root, target.Target.GameRoot);
            var detail = new GameViewModel(item, () => { });
            detail.PlayCommand.Execute(null);
            Assert.Equal(LocalizationService.Instance["Game.EpicLaunchManual"], detail.StatusMessage);
            var window = new Window { Width = 1280, Height = 800, Content = new GameView { DataContext = detail } };
            window.Show(); Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                frame.Save("/tmp/opencode/phaket-render/game-epic-model.png");
            }
            window.Close();
            Assert.False(item.OwnedNotInstalled);
            Assert.True(Assert.Single(vm.Games, g => g.Title == Other.Title).OwnedNotInstalled);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Invalid_token_shows_localized_error_without_crash()
    {
        var account = new Account(); await account.LoginAsync();
        var vm = Create(account, _ => Task.FromException<IReadOnlyList<OwnedEpicGame>>(new PhaketException("EPIC_TOKEN_INVALID")));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance["Library.EpicTokenInvalid"], vm.Status);
    }

    [AvaloniaFact]
    public async Task Signed_out_prompts_and_account_change_refreshes_owned()
    {
        var account = new Account(); int calls = 0;
        var vm = Create(account, _ => { calls++; return Task.FromResult<IReadOnlyList<OwnedEpicGame>>([AmongUs]); });
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance["Library.EpicSignInHint"], vm.Status);
        Assert.Equal(0, calls);
        await account.LoginAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, calls);
        Assert.True(Assert.Single(vm.Games, g => g.Definition.Slug == "among-us").OwnedNotInstalled);
        await account.LogoutAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.False(Assert.Single(vm.Games, g => g.Definition.Slug == "among-us").EpicOwned);
    }

    [AvaloniaFact]
    public async Task Refresh_preserves_detected_steam_installation_and_ignores_missing_record()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-owned-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var account = new Account();
            var vm = Create(account, _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([AmongUs]),
                () => [new(AmongUs.AppName, root, "missing.exe", DateTimeOffset.UtcNow)]);
            var item = Assert.Single(vm.Games, g => g.Definition.Slug == "among-us");
            item.Detected = new SteamAdapter([item.Definition]).FromManualFolder(root, item.SteamAppId);
            await account.LoginAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(item.EpicOwned);
            Assert.True(item.SteamInstalled);
            Assert.False(item.EpicInstalled);
            Assert.True(item.IsInstalled);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Unavailable_library_exposes_relogin_action()
    {
        var account = new Account(); await account.LoginAsync();
        var vm = Create(account, _ => Task.FromException<IReadOnlyList<OwnedEpicGame>>(new PhaketException("EPIC_LIBRARY_UNAVAILABLE")));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance["Library.EpicUnavailable"], vm.Status);
        Assert.True(vm.EpicError);
        Assert.True(vm.OpenAccountsCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Logout_discards_inflight_results()
    {
        var account = new Account(); await account.LoginAsync();
        var pending = new TaskCompletionSource<IReadOnlyList<OwnedEpicGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(account, _ => pending.Task);
        var scan = vm.ScanCommand.ExecuteAsync(null);
        await account.LogoutAsync();
        Dispatcher.UIThread.RunJobs();
        pending.SetResult([AmongUs]);
        await scan;
        Assert.False(Assert.Single(vm.Games, g => g.Definition.Slug == "among-us").EpicOwned);
        Assert.Equal(LocalizationService.Instance["Library.EpicSignInHint"], vm.Status);
    }

    [AvaloniaFact]
    public async Task Render_owned_library()
    {
        var account = new Account(); await account.LoginAsync();
        var vm = Create(account, _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([AmongUs, Other]));
        await vm.ScanCommand.ExecuteAsync(null);
        var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory("/tmp/opencode/phaket-render");
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save("/tmp/opencode/phaket-render/library-owned.png");
        window.Close();
    }
}
