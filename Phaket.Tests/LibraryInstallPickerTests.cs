using System.Reflection;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Accounts;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

// Regression for the install flows: a null plan must not break the library view, and the
// folder picker must never end the click as an unobserved exception ("Open → nothing").
public class LibraryInstallPickerTests
{
    private sealed class Account : IAccountService
    {
        public string Provider => "epic";
        public AccountInfo? Current { get; } = new("epic", "id", "name", null, DateTimeOffset.UtcNow);
        public event Action? AccountChanged { add { } remove { } }
        public Task<AccountInfo> LoginAsync(CancellationToken ct = default) => Task.FromResult(Current!);
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Sink : ILogSink
    {
        public readonly List<string> Lines = [];
        public bool IsEnabled(LogEventLevel level, string area) => true;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Lines.Add(messageTemplate);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values) => Lines.Add(messageTemplate);
        public List<string> BindingErrors() => Lines.Where(l => l.Contains("binding", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private sealed class Runner : IProcessRunner
    {
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, Action<string> output, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private static readonly OwnedEpicGame EpicGame = new("963137e4c29d4c79a81323b8fab03a40", "", "", "Among Us", true, "assets");

    private static LibraryViewModel EpicVm(LegendaryInstallAdapter adapter)
    {
        var vm = new LibraryViewModel(_ => { }, new Account(), null, _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([EpicGame]), () => []);
        typeof(LibraryViewModel).GetField("installAdapter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, adapter);
        return vm;
    }

    private static LegendaryInstallAdapter Adapter(string root)
    {
        // The tokens are never read here (these tests stop at the plan), but a failure code must
        // still be one the UI can localize, so an accidental run cannot show a raw code.
        var adapter = new LegendaryInstallAdapter(_ => Task.FromException<LegendaryTokens>(new PhaketException("EPIC_TOKEN_INVALID")), new Runner(), Path.Combine(root, "config"));
        typeof(LegendaryInstallAdapter).GetField("binary", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adapter, "fake");
        return adapter;
    }

    private static async Task<GameCatalogItem> AmongUsAsync(LibraryViewModel vm)
    {
        await vm.ScanCommand.ExecuteAsync(null);
        var item = Assert.Single(vm.Games, g => g.Definition.Slug == "among-us");
        Assert.True(item.CanInstallEpic);
        return item;
    }

    private static T? Field<T>(Window window, Func<T, bool> match) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(match);

    [AvaloniaFact]
    public async Task Library_view_with_no_plan_renders_without_binding_errors()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-picker-" + Guid.NewGuid());
        var vm = EpicVm(Adapter(root));
        await AmongUsAsync(vm);
        Assert.Null(vm.InstallPlan);
        var sink = new Sink();
        var previous = Logger.Sink;
        Logger.Sink = sink;
        try
        {
            var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Close();
            Assert.Empty(sink.BindingErrors());
            // The null-safe accessors keep the plan card empty and hidden rather than broken.
            Assert.Equal("", vm.InstallPlanAppName);
            Assert.Equal("", vm.InstallPlanTargetPath);
            Assert.False(vm.HasInstallPlan);
        }
        finally { Logger.Sink = previous; }
    }

    // The sink check above only means something if Avalonia reports binding failures to it.
    [AvaloniaFact]
    public void Binding_sink_reports_a_failing_binding()
    {
        var sink = new Sink();
        var previous = Logger.Sink;
        Logger.Sink = sink;
        try
        {
            var text = new TextBlock();
            text.Bind(TextBlock.TextProperty, new Binding("Plan.TargetPath"));
            var window = new Window { Width = 400, Height = 200, Content = text, DataContext = new { } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Close();
            Assert.Contains(sink.BindingErrors(), line => line.Contains("An error occurred", StringComparison.OrdinalIgnoreCase));
        }
        finally { Logger.Sink = previous; }
    }

    [AvaloniaFact]
    public async Task Cancelled_epic_picker_leaves_no_plan_and_no_error()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-picker-" + Guid.NewGuid());
        var vm = EpicVm(Adapter(root));
        var item = await AmongUsAsync(vm);
        vm.InstallError = "stale";
        vm.InstallFolderPicker = () => Task.FromResult<string?>(null);
        await vm.PrepareGameInstallCommand.ExecuteAsync(item);
        Assert.Null(vm.InstallPlan);
        Assert.Equal("", vm.InstallError);
    }

    [AvaloniaFact]
    public async Task Chosen_epic_folder_builds_a_visible_plan()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-picker-" + Guid.NewGuid());
        var chosen = Path.Combine(root, "games");
        var vm = EpicVm(Adapter(root));
        var item = await AmongUsAsync(vm);
        vm.InstallFolderPicker = () => Task.FromResult<string?>(chosen);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await vm.PrepareGameInstallCommand.ExecuteAsync(item);
        Assert.NotNull(vm.InstallPlan);
        Assert.Equal(EpicGame.AppName, vm.InstallPlanAppName);
        Assert.Equal(Path.Combine(chosen, EpicGame.AppName), vm.InstallPlanTargetPath);
        Assert.Contains(nameof(vm.InstallPlanAppName), changed);
        Assert.Contains(nameof(vm.InstallPlanTargetPath), changed);
        var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.NotNull(Field<TextBlock>(window, t => t.Text == vm.InstallPlanTargetPath));
            Assert.NotNull(Field<TextBlock>(window, t => t.Text == vm.InstallPlanAppName));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Unusable_epic_picker_is_reported_localized_and_does_not_throw()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-picker-" + Guid.NewGuid());
        var vm = EpicVm(Adapter(root));
        var item = await AmongUsAsync(vm);
        var language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            foreach (var locale in new[] { "pl-PL", "en" })
            {
                LocalizationService.Instance.CurrentLanguage = locale;
                vm.InstallError = "";
                vm.InstallFolderPicker = () => Task.FromException<string?>(new IOException("no portal"));
                await vm.PrepareGameInstallCommand.ExecuteAsync(item);
                Assert.Equal(LocalizationService.Instance["ModInstall.FolderFailed"], vm.InstallError);
                Assert.NotEqual("ModInstall.FolderFailed", vm.InstallError);
                Assert.Null(vm.InstallPlan);
            }
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; }
    }

    [AvaloniaFact]
    public async Task Steam_flow_reports_picker_failure_and_shows_plan_after_choice()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-steam-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binary = Path.Combine(root, "depot");
            File.WriteAllText(binary, "fake");
            var steam = new SteamAccount();
            var vm = new LibraryViewModel(_ => { }, new Account(), null, _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([]), () => [],
                steam, _ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>([new("945360", "Among Us", null, null, "api", false)]), () => []);
            await vm.ScanCommand.ExecuteAsync(null);
            typeof(LibraryViewModel).GetField("depotAdapter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(vm, new DepotDownloaderInstallAdapter(new SteamRunner(), Path.Combine(root, "config"), binary));
            var item = Assert.Single(vm.Games, g => g.SteamAppId == "945360");
            Assert.True(item.CanInstallSteam);

            // A picker that cannot open must not bubble out of the command.
            vm.SteamFolderPicker = () => Task.FromException<string?>(new UnauthorizedAccessException("locked"));
            await vm.OpenSteamInstallCommand.ExecuteAsync(item);
            var flow = Assert.IsType<SteamInstallFlowViewModel>(vm.SteamInstall);
            var beforePick = flow.TargetDir;
            await flow.PickFolderCommand.ExecuteAsync(null);
            Assert.Equal(LocalizationService.Instance["ModInstall.FolderFailed"], vm.InstallError);
            Assert.Equal(beforePick, flow.TargetDir);
            Assert.Null(flow.Preview);

            // A real choice fills the folder and a prepared plan is visible in the flow card.
            var chosen = Path.Combine(root, "games");
            vm.SteamFolderPicker = () => Task.FromResult<string?>(chosen);
            await flow.PickFolderCommand.ExecuteAsync(null);
            Assert.Equal(chosen, flow.TargetDir);
            Assert.Equal("", vm.InstallError);
            // The sample catalog build carries placeholder depot ids, so the plan is prepared
            // from a valid build option, exactly as a real depot build would be.
            flow.SelectedBuild = new BuildOption("valid build", "10", "123", Arch: "x64");
            flow.PrepareCommand.Execute(null);
            Assert.NotNull(flow.Preview);
            Assert.Equal(Path.Combine(chosen, "945360"), flow.PreviewTargetPath);
            var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try { Assert.NotNull(Field<TextBlock>(window, t => t.Text == flow.PreviewTargetPath)); }
            finally { window.Close(); }

            // Cancelling the Steam picker is silent: folder and error stay as they were.
            flow.TargetDir = chosen;
            vm.SteamFolderPicker = () => Task.FromResult<string?>(null);
            await flow.PickFolderCommand.ExecuteAsync(null);
            Assert.Equal(chosen, flow.TargetDir);
            Assert.Equal("", vm.InstallError);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class SteamAccount : IAccountService
    {
        public string Provider => "steam";
        public AccountInfo? Current { get; } = new("steam", "76561198000000000", "name", null, DateTimeOffset.UtcNow);
        public event Action? AccountChanged { add { } remove { } }
        public Task<AccountInfo> LoginAsync(CancellationToken ct = default) => Task.FromResult(Current!);
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SteamRunner : IDepotProcessRunner
    {
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct) => Task.FromResult(0);
    }
}
