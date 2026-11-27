using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Models;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public sealed class GitHubPickerTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private const string ReleaseJson = """
    [
      {"id":3,"tag_name":"v3.0.0-beta.1","name":"Beta build","published_at":"2026-01-02T00:00:00Z","prerelease":true,"draft":false,"assets":[{"id":31,"name":"mod-beta.zip","size":1024,"browser_download_url":"https://github.com/owner/repo/releases/download/v3.0.0/mod-beta.zip"}]},
      {"id":2,"tag_name":"v2.1.0","name":"Stable","published_at":"2025-12-01T00:00:00Z","prerelease":false,"draft":false,"assets":[{"id":21,"name":"mod.zip","size":2048,"browser_download_url":"https://github.com/owner/repo/releases/download/v2.1.0/mod.zip"}]},
      {"id":1,"tag_name":"draft","name":"Draft","draft":true,"assets":[]}
    ]
    """;

    [Theory]
    [InlineData("owner/repo", "owner/repo")]
    [InlineData(" https://github.com/owner/repo/ ", "owner/repo")]
    [InlineData("https://github.com/owner/repo.git", "owner/repo")]
    [InlineData("https://github.com/owner/repo/releases", "owner/repo")]
    [InlineData("Owner/repo.name_with-dots", "Owner/repo.name_with-dots")]
    [InlineData("http://github.com/owner/repo", null)]
    [InlineData("https://github.com.evil.test/owner/repo", null)]
    [InlineData("owner/../repo", null)]
    [InlineData("owner/repo?query", null)]
    public void Repository_input_is_validated(string value, string? expected) =>
        Assert.Equal(expected, GitHubReleaseService.ParseRepository(value));

    [Fact]
    public async Task Parses_public_releases_assets_and_caches_the_response()
    {
        GitHubReleaseService.ClearCache();
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson, Encoding.UTF8, "application/json"),
            Headers = { { "Link", "<https://api.github.com/repos/owner/repo/releases?page=2>; rel=\"next\"" } }
        });
        using var service = new GitHubReleaseService(new HttpClient(handler));

        var first = await service.GetReleasesAsync("owner/repo", cancellationToken: TestContext.Current.CancellationToken);
        var cached = await service.GetReleasesAsync("https://github.com/owner/repo", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
        Assert.True(first.HasMore);
        Assert.Equal(2, first.Releases.Count);
        Assert.True(first.Releases[0].Prerelease);
        Assert.Equal("v2.1.0", first.Releases[1].Tag);
        Assert.Equal(21, first.Releases[1].Assets[0].Id);
        Assert.Equal(2048, first.Releases[1].Assets[0].Size);
        Assert.Same(first, cached);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    public async Task Rate_limit_has_a_clear_localized_error(HttpStatusCode status)
    {
        GitHubReleaseService.ClearCache();
        using var service = new GitHubReleaseService(new HttpClient(new Handler(_ => new HttpResponseMessage(status))));
        var ex = await Assert.ThrowsAsync<GitHubReleaseException>(() => service.GetReleasesAsync(
            "owner/repo", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Sources.GitHub.RateLimit", ex.LocalizationKey);
    }

    [AvaloniaFact]
    public async Task Offline_picker_falls_back_to_deterministic_demo_releases()
    {
        GitHubReleaseService.ClearCache();
        using var service = new GitHubReleaseService(new HttpClient(new OfflineHandler()));
        using var vm = new GitHubReleasePickerViewModel(service);
        vm.RepositoryInput = "owner/repo";

        await vm.SearchCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsDemoMode);
        Assert.Equal("Sources.GitHub.Offline", vm.StatusMessage);
        Assert.Contains(LocalizationService.Instance["Sources.GitHub.DemoMode"], vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("owner/repo", vm.RepositoryInput);
        Assert.Equal(GitHubReleaseService.DemoReleases.Select(x => x.Tag), vm.Releases.Select(x => x.Tag));
    }

    [AvaloniaFact]
    public async Task Pin_is_stable_and_newer_release_is_only_informational()
    {
        GitHubReleaseService.ClearCache();
        string current = """[{"id":1,"tag_name":"v1.0.0","name":"Stable","prerelease":false,"draft":false,"assets":[{"id":11,"name":"mod.zip","size":50,"browser_download_url":"https://github.com/owner/repo/releases/download/v1.0.0/mod.zip"}]}]""";
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(current, Encoding.UTF8, "application/json")
        });
        using var service = new GitHubReleaseService(new HttpClient(handler));
        using var vm = new GitHubReleasePickerViewModel(service);
        vm.RepositoryInput = "owner/repo";
        await vm.SearchCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedRelease = vm.Releases[0];
        vm.SelectedAsset = vm.Assets[0];
        var selected = vm.Result!;

        current = """[{"id":2,"tag_name":"v2.0.0","name":"Newer","prerelease":false,"draft":false,"assets":[{"id":22,"name":"mod-v2.zip","size":60,"browser_download_url":"https://github.com/owner/repo/releases/download/v2.0.0/mod-v2.zip"}]}]""";
        GitHubReleaseService.ClearCache();
        vm.PinSelected();
        for (int attempt = 0; attempt < 20 && !vm.HasNewerRelease; attempt++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("v1.0.0", vm.PinnedSelection!.Tag);
        Assert.Equal(selected.AssetId, vm.PinnedSelection.AssetId);
        Assert.True(vm.HasNewerRelease);
        Assert.Contains("v1.0.0", vm.PinnedStatus, StringComparison.Ordinal);
        Assert.Contains(LocalizationService.Instance["Sources.GitHub.PinnedNewer"].Replace("{0}", "v1.0.0", StringComparison.Ordinal), vm.PinnedStatus, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Picker_renders_headless_at_target_size()
    {
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var vm = new GitHubReleasePickerViewModel();
        vm.ShowDemoCommand.Execute(null);
        vm.SelectedRelease = vm.Releases[0];
        vm.SelectedAsset = vm.Assets[0];
        vm.PinSelected();
        var window = new Window { Width = 1280, Height = 800, Content = new GitHubReleasePickerView { DataContext = vm } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            string dir = RenderArtifacts.Directory;
            Directory.CreateDirectory(dir);
            frame!.Save(RenderArtifacts.Path("24-github-picker.png"), PngBitmapEncoderOptions.Default);
            Assert.True(frame.PixelSize.Width > 0);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
    }
}
