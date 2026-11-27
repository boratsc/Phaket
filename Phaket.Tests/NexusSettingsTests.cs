using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Nexus;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

// PAK-117 settings section: OAuth sign-in/sign-out, API-key fallback and the
// browser hand-off, all against a fake INexusService (no keyring, no network).
public sealed class NexusSettingsTests
{
    private sealed class FakeNexus : INexusService
    {
        public NexusKeyStatus Status { get; set; } = new(new NexusUser("Tester", true, null), new NexusRateLimits(99, 2400));
        public bool SignedIn { get; set; }
        public string? SignedInClientId { get; private set; }
        public string? SavedKey { get; private set; }
        public string? FailCode { get; set; }
        public NexusDownloadResult? Download { get; set; }
        public NexusRateLimits LastLimits => Status.Limits;
        public bool LastReadFromStaleCache => false;

        public Task<NexusKeyStatus> ValidateKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            if (FailCode is { } code) return Task.FromException<NexusKeyStatus>(new NexusApiException(code));
            return Task.FromResult(Status);
        }

        public Task<NexusKeyStatus> ValidateStoredKeyAsync(CancellationToken cancellationToken = default)
        {
            if (FailCode is { } code) return Task.FromException<NexusKeyStatus>(new NexusApiException(code));
            return Task.FromResult(Status);
        }

        public Task<bool> SaveKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            SavedKey = key;
            return Task.FromResult(true);
        }

        public Task DeleteKeyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void ConfigureClientId(string? clientId) { }
        public Task<bool> HasOAuthSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(SignedIn);
        public Task<NexusKeyStatus> SignInAsync(string? clientId, CancellationToken cancellationToken = default)
        {
            SignedInClientId = clientId;
            if (FailCode is { } code) return Task.FromException<NexusKeyStatus>(new NexusApiException(code));
            SignedIn = true;
            return Task.FromResult(Status);
        }

        public Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            SignedIn = false;
            return Task.CompletedTask;
        }

        public Task<NexusModMetadata> GetModAsync(string domain, int modId, CancellationToken cancellationToken = default)
        {
            LastDomain = domain;
            return Task.FromResult(new NexusModMetadata(modId, "Example Mod", "Author", "1.2.3", "Summary", 12, true));
        }

        public string? LastDomain { get; private set; }

        public Task<IReadOnlyList<NexusFileInfo>> GetFilesAsync(string domain, int modId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NexusFileInfo>>([new NexusFileInfo(7, "Main file", "Example-1.zip", "1.2.3", "MAIN", 2048)]);

        public NexusModMetadata? CachedMod(string domain, int modId) =>
            new(modId, "Example Mod", "Author", "1.2.3", "Summary", 12, true);
        public IReadOnlyList<NexusFileInfo>? CachedFiles(string domain, int modId) => null;

        public Task<NexusDownloadResult> DownloadAsync(NxmLink link, CancellationToken cancellationToken = default) =>
            Download is { } result
                ? Task.FromResult(result)
                : Task.FromException<NexusDownloadResult>(new NexusApiException("NEXUS_DOWNLOAD_FAILED"));
    }

    private static (SettingsViewModel Vm, string Root) Create(FakeNexus fake)
    {
        string root = Directory.CreateTempSubdirectory("phaket-nexus-settings-").FullName;
        var vm = new SettingsViewModel(Path.Combine(root, "settings.json"),
            new SettingsViewModel.SecretStoreSeam(new Phaket.Core.Tests.InMemorySecretStore(), root), nexusService: fake);
        return (vm, root);
    }

    [AvaloniaFact]
    public async Task Sign_in_shows_account_and_premium_state()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var (vm, root) = Create(new FakeNexus());
        try
        {
            vm.SelectSectionCommand.Execute("nexus");
            vm.NexusClientId = "my-client";
            await vm.SignInNexusCommand.ExecuteAsync(null);

            Assert.True(vm.NexusSignedIn);
            Assert.Equal(LocalizationService.Instance["Settings.Nexus.OAuth.SignedIn"], vm.NexusOAuthStatusText);
            Assert.Contains("Tester", vm.NexusAccountText);
            Assert.Equal(LocalizationService.Instance["Settings.Nexus.Premium"], vm.NexusPremiumText);
            Assert.Contains("99", vm.NexusLimitsText);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Revoked_access_is_reported_and_leaves_the_user_signed_out()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var fake = new FakeNexus { FailCode = "NEXUS_OAUTH_REVOKED" };
        var (vm, root) = Create(fake);
        try
        {
            await vm.SignInNexusCommand.ExecuteAsync(null);
            Assert.False(vm.NexusSignedIn);
            Assert.Equal(LocalizationService.Instance["Nexus.Error.OAuthRevoked"], vm.NexusOAuthStatusText);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Sign_out_clears_the_session_and_keeps_the_api_key_fallback()
    {
        var fake = new FakeNexus { SignedIn = true };
        var (vm, root) = Create(fake);
        try
        {
            await vm.RefreshNexusSessionAsync();
            Assert.True(vm.NexusSignedIn);
            await vm.SignOutNexusCommand.ExecuteAsync(null);
            Assert.False(vm.NexusSignedIn);
            Assert.False(fake.SignedIn);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Api_key_fallback_is_validated_saved_and_cleared_from_the_field()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var fake = new FakeNexus();
        var (vm, root) = Create(fake);
        try
        {
            vm.NexusApiKey = "key-123";
            await vm.CheckNexusKeyCommand.ExecuteAsync(null);
            Assert.Equal("key-123", fake.SavedKey);
            Assert.Equal("", vm.NexusApiKey);
            Assert.Equal(LocalizationService.Instance["Settings.Nexus.Saved"], vm.NexusStatusText);
            Assert.Contains("Tester", vm.NexusAccountText);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Downloaded_nxm_file_fills_the_manual_pin_form()
    {
        string archive = Path.Combine(Path.GetTempPath(), $"phaket-nexus-{Guid.NewGuid():N}.zip");
        await File.WriteAllTextAsync(archive, "zip", TestContext.Current.CancellationToken);
        var fake = new FakeNexus { Download = new NexusDownloadResult(archive, 3, Path.GetFileName(archive)) };
        var manual = new NexusManualViewModel(nexusService: fake, nexusSource: new NexusSource("amongus", 9589));
        try
        {
            manual.Browser.NxmLinkText = "nxm://amongus/mods/1/files/2?key=k&expires=1700000000";
            await manual.Browser.DownloadCommand.ExecuteAsync(null);

            Assert.Equal(fake.Download.Path, manual.LocalArchivePath);
            Assert.Equal("Example Mod", manual.Name);
            Assert.Equal("1.2.3", manual.PinnedVersion);
            Assert.Equal("https://www.nexusmods.com/amongus/mods/1", manual.Url);
            Assert.True(NexusManualViewModel.IsValidNexusModUrl(manual.Url));
            Assert.True(NexusManualViewModel.IsValidArchive(manual.LocalArchivePath));
        }
        finally { File.Delete(archive); }
    }

    [AvaloniaFact]
    public void Bare_mod_id_is_not_accepted_as_a_pin_url()
    {
        Assert.True(NexusManualViewModel.IsValidNexusModUrl("https://www.nexusmods.com/amongus/mods/1"));
        Assert.False(NexusManualViewModel.IsValidNexusModUrl("1"));
    }

    [AvaloniaFact]
    public async Task Browse_accepts_a_bare_id_when_the_game_domain_comes_from_the_definition()
    {
        var vm = new NexusBrowserViewModel(new FakeNexus(), new NexusSource("amongus", 9589)) { Reference = "1" };
        await vm.LoadModCommand.ExecuteAsync(null);
        Assert.True(vm.HasMod);
        Assert.True(vm.HasFiles);
    }

    // PAK-129: a /games/{id}/mods/... address used to lose the game id and be read under the
    // selected game's domain. The id must match the selected game, or the load is refused.
    [AvaloniaFact]
    public async Task Numeric_game_id_in_the_url_is_checked_against_the_selected_game()
    {
        var fake = new FakeNexus();
        var matching = new NexusBrowserViewModel(fake, new NexusSource("valheim", 3667))
        {
            Reference = "https://www.nexusmods.com/games/3667/mods/123"
        };
        await matching.LoadModCommand.ExecuteAsync(null);
        Assert.True(matching.HasMod);
        Assert.Equal("valheim", fake.LastDomain);

        var foreign = new NexusBrowserViewModel(fake, new NexusSource("valheim", 3667))
        {
            Reference = "https://www.nexusmods.com/games/9589/mods/123"
        };
        await foreign.LoadModCommand.ExecuteAsync(null);
        Assert.False(foreign.HasMod);
        Assert.Equal("Nexus.Error.GameMismatch", foreign.StatusKey);

        var byDomain = new NexusBrowserViewModel(fake, new NexusSource("valheim", 3667))
        {
            Reference = "https://www.nexusmods.com/valheim/mods/123"
        };
        await byDomain.LoadModCommand.ExecuteAsync(null);
        Assert.True(byDomain.HasMod);
        Assert.Equal("valheim", fake.LastDomain);
    }

    [AvaloniaFact]
    public void Settings_nexus_section_renders_in_both_languages()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            foreach (string locale in new[] { "pl-PL", "en" })
            {
                LocalizationService.Instance.CurrentLanguage = locale;
                var (vm, root) = Create(new FakeNexus());
                var window = new MainWindow(new MainWindowViewModel(TestAccounts.Create(), vm)) { Width = 1280, Height = 800 };
                window.Show();
                try
                {
                    ((MainWindowViewModel)window.DataContext!).NavigateCommand.Execute("settings");
                    vm.SelectSectionCommand.Execute("nexus");
                    Dispatcher.UIThread.RunJobs();
                    var buttons = window.GetVisualDescendants().OfType<Button>()
                        .Select(button => button.Content as string).Where(text => !string.IsNullOrWhiteSpace(text)).ToHashSet(StringComparer.Ordinal);
                    Assert.Contains(LocalizationService.Instance["Settings.Nexus.OAuth.SignIn"], buttons);
                    Assert.Contains(LocalizationService.Instance["Settings.Nexus.Tab"], buttons);

                    Directory.CreateDirectory(RenderArtifacts.Directory);
                    using var frame = window.CaptureRenderedFrame();
                    frame!.Save(RenderArtifacts.Path($"53-settings-nexus-{locale}.png"), PngBitmapEncoderOptions.Default);
                }
                finally
                {
                    window.Close();
                    Directory.Delete(root, true);
                }
            }
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; }
    }

    [AvaloniaFact]
    public async Task Nexus_browser_renders_metadata_files_and_nxm_download()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        string archive = Path.Combine(Path.GetTempPath(), $"phaket-nexus-{Guid.NewGuid():N}.zip");
        await File.WriteAllTextAsync(archive, "zip", TestContext.Current.CancellationToken);
        var fake = new FakeNexus { Download = new NexusDownloadResult(archive, 3, Path.GetFileName(archive)) };
        try
        {
            var vm = new NexusBrowserViewModel(fake, new NexusSource("amongus", 9589))
            {
                Reference = "https://www.nexusmods.com/amongus/mods/1",
                NxmLinkText = "nxm://amongus/mods/1/files/2?key=k&expires=1700000000"
            };
            await vm.LoadModCommand.ExecuteAsync(null);
            await vm.DownloadCommand.ExecuteAsync(null);
            Assert.True(vm.HasMod);
            Assert.True(vm.HasFiles);
            Assert.True(vm.HasDownloaded);

            var window = new Window { Width = 900, Height = 760, Content = new Phaket.Views.NexusBrowserView { DataContext = vm } };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                Directory.CreateDirectory(RenderArtifacts.Directory);
                frame!.Save(RenderArtifacts.Path("54-nexus-browser.png"), PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            File.Delete(archive);
        }
    }
}