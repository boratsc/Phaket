using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Tests;
using Phaket.Services;
using Phaket.ViewModels;
using Velopack.Exceptions;

namespace Phaket.Tests;

// PAK-121: in-app update integration. Everything here is offline — a fake IUpdateSource
// stands in for Velopack, so no feed is contacted and VelopackApp.Run() never executes.
public sealed class UpdatesTests
{
    private sealed class FakeSource : IUpdateSource
    {
        public bool IsInstalled { get; set; } = true;
        public string? CurrentVersion { get; set; } = "0.1.0";
        public string? Available { get; set; }
        public Exception? CheckError { get; set; }
        public Exception? DownloadError { get; set; }
        public bool Applied { get; private set; }
        public string? AppliedVersion { get; private set; }

        public Task<string?> CheckAsync(CancellationToken cancellationToken) =>
            CheckError is { } error ? Task.FromException<string?>(error) : Task.FromResult(Available);

        public Task DownloadAsync(string version, Action<int> progress, CancellationToken cancellationToken)
        {
            if (DownloadError is { } error) return Task.FromException(error);
            progress(50);
            progress(100);
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(string version)
        {
            Applied = true;
            AppliedVersion = version;
        }
    }

    private sealed class ProgressCollector : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }

    private static string TempRoot() =>
        Directory.CreateTempSubdirectory("phaket-updates-").FullName;

    [Fact]
    public void Velopack_channel_is_os_plus_user_preference()
    {
        Assert.Equal("win-x64-stable", UpdateService.FeedChannel(UpdateChannel.Stable, windows: true));
        Assert.Equal("win-x64-beta", UpdateService.FeedChannel(UpdateChannel.Beta, windows: true));
        Assert.Equal("linux-x64-stable", UpdateService.FeedChannel(UpdateChannel.Stable, windows: false));
        Assert.Equal("linux-x64-beta", UpdateService.FeedChannel(UpdateChannel.Beta, windows: false));
    }

    [Fact]
    public void Channel_preference_round_trips_and_preserves_other_settings()
    {
        string root = TempRoot();
        try
        {
            string path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, """{"language":"en"}""");
            var service = new UpdateService(path, _ => new FakeSource());

            Assert.Equal(UpdateChannel.Stable, service.Channel);
            Assert.True(service.SetChannel(UpdateChannel.Beta));

            Assert.Equal(UpdateChannel.Beta, UpdateService.LoadChannel(path));
            Assert.Contains("\"language\": \"en\"", File.ReadAllText(path));
            Assert.Contains("\"updateChannel\": \"beta\"", File.ReadAllText(path));
            Assert.Equal(UpdateChannel.Beta, new UpdateService(path, _ => new FakeSource()).Channel);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Dev_build_is_information_not_an_error()
    {
        string root = TempRoot();
        try
        {
            var service = new UpdateService(Path.Combine(root, "settings.json"),
                _ => new FakeSource { IsInstalled = false, CurrentVersion = "0.1.0" });

            UpdateCheckResult result = await service.CheckAsync();

            Assert.Equal(UpdateOutcome.DevelopmentMode, result.Outcome);
            Assert.Equal(UpdateError.None, result.Error);
            Assert.Equal("Settings.Updates.DevMode", UpdateService.KeyFor(UpdateError.NotInstalled));
        }
        finally { Directory.Delete(root, true); }
    }

    // Velopack's constructors are internal; the guard must be exercised with a real instance.
    private static NotInstalledException NotInstalled() =>
        (NotInstalledException)Activator.CreateInstance(typeof(NotInstalledException), nonPublic: true)!;

    [Fact]
    public async Task NotInstalled_exception_during_check_maps_to_dev_mode()
    {
        string root = TempRoot();
        try
        {
            var service = new UpdateService(Path.Combine(root, "settings.json"),
                _ => new FakeSource { CheckError = NotInstalled() });

            UpdateCheckResult result = await service.CheckAsync();

            Assert.Equal(UpdateOutcome.DevelopmentMode, result.Outcome);
            Assert.Equal(UpdateError.NotInstalled, result.Error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Checksum_failure_maps_to_its_localized_key()
    {
        string root = TempRoot();
        try
        {
            var source = new FakeSource
            {
                Available = "0.2.0",
                DownloadError = new ChecksumFailedException("bad sha")
            };
            var service = new UpdateService(Path.Combine(root, "settings.json"), _ => source);
            await service.CheckAsync();

            UpdateDownloadResult result = await service.DownloadAsync();

            Assert.False(result.Success);
            Assert.Equal(UpdateError.Checksum, result.Error);
            Assert.Equal("Settings.Updates.Error.Checksum", UpdateService.KeyFor(result.Error));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Network_failure_maps_to_the_network_key()
    {
        string root = TempRoot();
        try
        {
            var service = new UpdateService(Path.Combine(root, "settings.json"),
                _ => new FakeSource { CheckError = new HttpRequestException("offline") });

            UpdateCheckResult result = await service.CheckAsync();

            Assert.Equal(UpdateOutcome.Error, result.Outcome);
            Assert.Equal("Settings.Updates.Error.Network", UpdateService.KeyFor(result.Error));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Check_download_and_apply_use_the_available_version()
    {
        string root = TempRoot();
        try
        {
            var source = new FakeSource { Available = "0.2.0" };
            var service = new UpdateService(Path.Combine(root, "settings.json"), _ => source);

            UpdateCheckResult check = await service.CheckAsync();
            var progress = new ProgressCollector();
            UpdateDownloadResult download = await service.DownloadAsync(progress);
            UpdateError apply = service.ApplyAndRestart();

            Assert.Equal(UpdateOutcome.Available, check.Outcome);
            Assert.Equal("0.2.0", check.Version);
            Assert.True(download.Success);
            Assert.Equal(new[] { 50, 100 }, progress.Values);
            Assert.Equal(UpdateError.None, apply);
            Assert.True(source.Applied);
            Assert.Equal("0.2.0", source.AppliedVersion);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Switching_channel_rebuilds_the_source_with_the_new_explicit_channel()
    {
        string root = TempRoot();
        try
        {
            var requested = new List<UpdateChannel>();
            var service = new UpdateService(Path.Combine(root, "settings.json"), channel =>
            {
                requested.Add(channel);
                return new FakeSource { IsInstalled = false };
            });

            await service.CheckAsync();
            service.SetChannel(UpdateChannel.Beta);
            await service.CheckAsync();

            Assert.Equal(new[] { UpdateChannel.Stable, UpdateChannel.Beta }, requested);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Default_channel_is_inherited_and_downgrade_needs_an_explicit_choice()
    {
        string root = TempRoot();
        try
        {
            string path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, """{"language":"en"}""");
            var service = new UpdateService(path, _ => new FakeSource());

            // PAK-124: no channel in settings means the installed package's channel wins. No
            // ExplicitChannel and no downgrade, so a beta build is never cut back to stable.
            Assert.False(service.ChannelExplicit);
            var inherited = UpdateService.BuildOptions(UpdateChannel.Stable, explicitChannel: false, windows: true);
            Assert.Null(inherited.ExplicitChannel);
            Assert.False(inherited.AllowVersionDowngrade);

            // A deliberate choice pins the channel; only then a downgrade is allowed.
            Assert.True(service.SetChannel(UpdateChannel.Beta));
            Assert.True(service.ChannelExplicit);
            var pinned = UpdateService.BuildOptions(UpdateChannel.Beta, explicitChannel: true, windows: false);
            Assert.Equal("linux-x64-beta", pinned.ExplicitChannel);
            Assert.True(pinned.AllowVersionDowngrade);
            Assert.Contains("\"updateChannel\": \"beta\"", File.ReadAllText(path));
            Assert.True(new UpdateService(path, _ => new FakeSource()).ChannelExplicit);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Choosing_the_already_active_channel_still_records_the_preference()
    {
        string root = TempRoot();
        try
        {
            string path = Path.Combine(root, "settings.json");
            var service = new UpdateService(path, _ => new FakeSource());
            Assert.False(service.ChannelExplicit);

            // Stable is the default but not yet a choice; picking it must persist, otherwise
            // the inherited channel would stay allowed to downgrade silently.
            Assert.True(service.SetChannel(UpdateChannel.Stable));
            Assert.True(service.ChannelExplicit);
            Assert.Contains("\"updateChannel\": \"stable\"", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Settings_show_dev_mode_information_after_a_check()
    {
        string root = TempRoot();
        string language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            var vm = CreateSettings(root, new FakeSource { IsInstalled = false, CurrentVersion = "0.1.0" });

            vm.SelectSectionCommand.Execute("updates");
            await vm.CheckForAppUpdatesCommand.ExecuteAsync(null);

            Assert.True(vm.IsUpdatesSection);
            Assert.False(vm.UpdateAvailable);
            Assert.Equal(string.Format(LocalizationService.Instance["Settings.Updates.DevMode"], "0.1.0"),
                vm.UpdateStatusText);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Settings_offer_install_when_an_update_is_available()
    {
        string root = TempRoot();
        string language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            var source = new FakeSource { Available = "0.2.0" };
            var vm = CreateSettings(root, source);

            await vm.CheckForAppUpdatesCommand.ExecuteAsync(null);
            Assert.True(vm.UpdateAvailable);
            Assert.Contains("0.2.0", vm.UpdateStatusText);

            await vm.DownloadAndInstallUpdateCommand.ExecuteAsync(null);
            Assert.True(source.Applied);
            Assert.False(vm.UpdateAvailable);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Channel_choice_is_persisted_in_settings_json()
    {
        string root = TempRoot();
        try
        {
            var vm = CreateSettings(root, new FakeSource());
            Assert.True(vm.IsStableChannel);

            vm.IsBetaChannel = true;

            Assert.True(vm.IsBetaChannel);
            Assert.False(vm.IsStableChannel);
            Assert.Equal(UpdateChannel.Beta, UpdateService.LoadChannel(Path.Combine(root, "settings.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Re_selecting_the_active_channel_still_persists_the_preference()
    {
        string root = TempRoot();
        try
        {
            var vm = CreateSettings(root, new FakeSource());
            Assert.True(vm.IsStableChannel);
            Assert.False(vm.Updates.ChannelExplicit);

            // Same channel as the inherited default: the old IsChecked path wrote nothing.
            vm.SelectChannelCommand.Execute("stable");

            Assert.True(vm.Updates.ChannelExplicit);
            Assert.Equal(UpdateChannel.Stable, UpdateService.LoadChannel(Path.Combine(root, "settings.json")));

            vm.SelectChannelCommand.Execute("beta");
            vm.SelectChannelCommand.Execute("beta");

            Assert.True(vm.IsBetaChannel);
            Assert.Equal(UpdateChannel.Beta, UpdateService.LoadChannel(Path.Combine(root, "settings.json")));
            var fresh = new UpdateService(Path.Combine(root, "settings.json"), _ => new FakeSource());
            Assert.True(fresh.ChannelExplicit);
            Assert.Equal(UpdateChannel.Beta, fresh.Channel);
        }
        finally { Directory.Delete(root, true); }
    }

    // AvaloniaFact: the language switch notifies live bindings, which must happen on the UI thread.
    [AvaloniaFact]
    public void Update_strings_exist_in_both_languages()
    {
        string[] keys =
        [
            "Settings.Updates.Tab", "Settings.Updates.Title", "Settings.Updates.Hint",
            "Settings.Updates.CurrentVersion", "Settings.Updates.Channel",
            "Settings.Updates.Channel.Stable", "Settings.Updates.Channel.Beta",
            "Settings.Updates.ChannelHint", "Settings.Updates.Check", "Settings.Updates.Install",
            "Settings.Updates.Checking", "Settings.Updates.UpToDate", "Settings.Updates.Available",
            "Settings.Updates.Downloading", "Settings.Updates.DownloadingProgress",
            "Settings.Updates.Installing", "Settings.Updates.DevMode",
            "Settings.Updates.Error.Checksum", "Settings.Updates.Error.Network",
            "Settings.Updates.Error.Download"
        ];
        string language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            foreach (string locale in new[] { "pl-PL", "en" })
            {
                LocalizationService.Instance.CurrentLanguage = locale;
                foreach (string key in keys)
                    Assert.NotEqual(key, LocalizationService.Instance[key]);
            }
            Assert.NotEqual(LocalizationService.Instance["Settings.Updates.Title"],
                LocalizationService.Instance["Settings.Updates.Tab"]);
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; }
    }

    [AvaloniaFact]
    public async Task Settings_updates_section_renders_in_both_languages()
    {
        string language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            foreach (string locale in new[] { "pl-PL", "en" })
            {
                LocalizationService.Instance.CurrentLanguage = locale;
                string root = TempRoot();
                try
                {
                    var vm = CreateSettings(root, new FakeSource { Available = "0.2.0" });
                    var window = new MainWindow(new MainWindowViewModel(TestAccounts.Create(), vm))
                    {
                        Width = 1280,
                        Height = 800
                    };
                    window.Show();
                    try
                    {
                        ((MainWindowViewModel)window.DataContext!).NavigateCommand.Execute("settings");
                        vm.SelectSectionCommand.Execute("updates");
                        Dispatcher.UIThread.RunJobs();
                        await vm.CheckForAppUpdatesCommand.ExecuteAsync(null);
                        Dispatcher.UIThread.RunJobs();

                        var buttons = window.GetVisualDescendants().OfType<Button>()
                            .Select(button => button.Content as string)
                            .Where(text => !string.IsNullOrWhiteSpace(text)).ToHashSet(StringComparer.Ordinal);
                        Assert.Contains(LocalizationService.Instance["Settings.Updates.Check"], buttons);
                        Assert.Contains(LocalizationService.Instance["Settings.Updates.Install"], buttons);
                        Assert.True(vm.UpdateAvailable);
                        // PAK-129: the channel radios must run the command, not only bind IsChecked.
                        var channelRadios = window.GetVisualDescendants().OfType<RadioButton>()
                            .Where(radio => radio.GroupName == "UpdateChannel").ToArray();
                        Assert.Equal(2, channelRadios.Length);
                        Assert.All(channelRadios, radio => Assert.NotNull(radio.Command));
                        // A click on the already active radio still records the explicit choice.
                        var stableRadio = channelRadios.Single(radio =>
                            AutomationProperties.GetAutomationId(radio) == "settings.updates.stable");
                        var center = stableRadio.TranslatePoint(
                            new Point(stableRadio.Bounds.Width / 2, stableRadio.Bounds.Height / 2), window);
                        Assert.NotNull(center);
                        window.MouseDown(center.Value, MouseButton.Left);
                        window.MouseUp(center.Value, MouseButton.Left);
                        Dispatcher.UIThread.RunJobs();
                        Assert.True(vm.Updates.ChannelExplicit);
                        Assert.Equal(UpdateChannel.Stable, UpdateService.LoadChannel(Path.Combine(root, "settings.json")));

                        Directory.CreateDirectory("/tmp/opencode/phaket-render");
                        using var frame = window.CaptureRenderedFrame();
                        frame!.Save($"/tmp/opencode/phaket-render/55-settings-updates-{locale}.png");
                    }
                    finally { window.Close(); }
                }
                finally { Directory.Delete(root, true); }
            }
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; }
    }

    private static SettingsViewModel CreateSettings(string root, IUpdateSource source) =>
        new(Path.Combine(root, "settings.json"),
            new SettingsViewModel.SecretStoreSeam(new InMemorySecretStore(), root),
            updateSource: _ => source);
}