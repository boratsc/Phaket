using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Phaket.Core.Accounts;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

public class LibrarySteamTests
{
    private sealed class Account(string provider) : IAccountService
    {
        public string Provider => provider;
        public AccountInfo? Current { get; private set; }
        public event Action? AccountChanged;
        public Task<AccountInfo> LoginAsync(CancellationToken ct = default)
        {
            Current = new(provider, "76561198000000000", "name", null, DateTimeOffset.UtcNow);
            AccountChanged?.Invoke();
            return Task.FromResult(Current);
        }
        public Task LogoutAsync(CancellationToken ct = default) { Current = null; AccountChanged?.Invoke(); return Task.CompletedTask; }
    }

    private static LibraryViewModel Create(Account steam, Func<AccountInfo, Task<IReadOnlyList<OwnedSteamGame>>> fetch,
        Func<IReadOnlyList<DepotRecordedInstallation>>? records = null) =>
        new(_ => { }, new Account("epic"), null, _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([]), () => [], steam, fetch, records ?? (() => []));

    [AvaloniaFact]
    public void SessionCleanupRequiresConfirmation()
    {
        var vm = Create(new Account("steam"), _ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>([]));
        // The current adapter intentionally never offers an attributable store.
        var paths = typeof(LibraryViewModel).GetField("sessionPaths", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        paths.SetValue(vm, new[] { "synthetic-session" });
        vm.ConfirmDepotSessionCleanupCommand.Execute(null);
        Assert.Equal("", vm.SessionCleanupStatus);
        vm.ConfirmSessionCleanup = true;
        vm.CancelDepotSessionCleanupCommand.Execute(null);
        vm.ConfirmDepotSessionCleanupCommand.Execute(null);
        Assert.Equal("", vm.SessionCleanupStatus);
        paths.SetValue(vm, new[] { "synthetic-session" });
        vm.ConfirmSessionCleanup = true;
        vm.ConfirmDepotSessionCleanupCommand.Execute(null);
        Assert.Equal(LocalizationService.Instance["SteamInstall.Error.Failed"], vm.SessionCleanupStatus);
        Assert.False(vm.ConfirmSessionCleanup);
    }

    [AvaloniaFact]
    public async Task InstallFlowOpensAndRendersPlanAndSessionHint()
    {
        var steam = new Account("steam");
        await steam.LoginAsync();
        var vm = Create(steam, _ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>([new("945360", "Among Us", null, null, "api", false)]));
        await vm.ScanCommand.ExecuteAsync(null);
        var game = Assert.Single(vm.Games, g => g.SteamAppId == "945360");
        Assert.True(game.CanInstallSteam);
        await vm.OpenSteamInstallCommand.ExecuteAsync(game);
        var flow = Assert.IsType<SteamInstallFlowViewModel>(vm.SteamInstall);
        flow.OsArch = "64";
        flow.Preview = new(new("945360", "/tmp/games", "/tmp/games/945360", "Among Us.exe", "linux", "64", null, null, []), null);
        vm.ListDepotSessionsCommand.Execute(null);
        Assert.False(vm.ConfirmSessionCleanup);
        Assert.Contains("IsolatedStorage", vm.SessionCleanupStatus);
        var hint = vm.SessionCleanupStatus;
        vm.ConfirmDepotSessionCleanupCommand.Execute(null);
        Assert.Equal(hint, vm.SessionCleanupStatus);
        var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
        window.Show(); Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(RenderArtifacts.Directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(RenderArtifacts.Path("steam-install-plan.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public async Task InstalledHookRescansAndReadsRecordedInstallations()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-steam-refresh-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var steam = new Account("steam");
            await steam.LoginAsync();
            int fetches = 0, recordReads = 0;
            bool installed = false;
            var vm = Create(steam, _ =>
            {
                fetches++;
                return Task.FromResult<IReadOnlyList<OwnedSteamGame>>([new("945360", "Among Us", null, null, "api", false)]);
            }, () =>
            {
                recordReads++;
                return installed ? [new("945360", root, "Among Us.exe", DateTimeOffset.UtcNow)] : [];
            });
            await vm.ScanCommand.ExecuteAsync(null);
            var game = Assert.Single(vm.Games, g => g.SteamAppId == "945360");
            Assert.False(game.SteamInstalled);
            await vm.OpenSteamInstallCommand.ExecuteAsync(game);
            var flow = Assert.IsType<SteamInstallFlowViewModel>(vm.SteamInstall);
            var beforeFetches = fetches;
            var beforeReads = recordReads;
            await File.WriteAllTextAsync(Path.Combine(root, "Among Us.exe"), "fixture");
            installed = true;
            flow.OnInstalled!();
            await vm.ScanCommand.ExecutionTask!;
            Assert.True(fetches > beforeFetches);
            Assert.True(recordReads > beforeReads);
            Assert.True(game.SteamInstalled);
            Assert.Equal(root, game.RootPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Owned_games_and_verified_records_merge_and_logout_refreshes()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-steam-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Among Us.exe"), "fixture");
            var steam = new Account("steam");
            var vm = Create(steam, _ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>([
                new("945360", "Among Us", null, null, "api", false), new("1234", "Other", null, null, "api", false)]),
                () => [new("945360", root, "Among Us.exe", DateTimeOffset.UtcNow)]);
            await steam.LoginAsync(); Dispatcher.UIThread.RunJobs();
            var game = Assert.Single(vm.Games, g => g.SteamAppId == "945360");
            Assert.True(game.SteamOwned);
            Assert.True(game.SteamInstalled);
            Assert.Equal(root, game.RootPath);
            Assert.True(Assert.Single(vm.Games, g => g.Title == "Other").SteamOwnedNotInstalled);
            await steam.LogoutAsync(); Dispatcher.UIThread.RunJobs();
            Assert.False(game.SteamOwned);
            Assert.True(game.SteamInstalled);
            Assert.DoesNotContain(vm.Games, g => g.Title == "Other");
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData("STEAM_API_KEY_MISSING", "Library.SteamKeyMissing")]
    [InlineData("STEAM_API_KEY_INVALID", "Library.SteamKeyInvalid")]
    [InlineData("STEAM_LIBRARY_PRIVATE", "Library.SteamPrivate")]
    [InlineData("STEAM_LIBRARY_UNAVAILABLE", "Library.SteamUnavailable")]
    public async Task Errors_are_localized(string code, string key)
    {
        var steam = new Account("steam");
        await steam.LoginAsync();
        var vm = Create(steam, _ => Task.FromException<IReadOnlyList<OwnedSteamGame>>(new PhaketException(code)));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance[key], vm.SteamStatus);
    }
}
