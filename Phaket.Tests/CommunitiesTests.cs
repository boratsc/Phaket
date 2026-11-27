using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket;
using Phaket.Services;
using Phaket.ViewModels;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public class CommunitiesApiClientTests
{
    [Fact]
    public async Task Polling_after_returns_only_new_messages_in_cursor_order()
    {
        var handler = new QueueHandler(_ => Json(HttpStatusCode.OK, """{"messages":[{"id":4,"authorNickname":"A","authorKey":"key123456789","content":"new","createdAt":"2026-01-01T00:00:00Z"},{"id":3,"authorNickname":"A","authorKey":"key123456789","content":"old"},{"id":5,"authorNickname":"B","content":"next"}]}"""));
        using var client = new CommunitiesApiClient(Client(handler), new TestSigner());

        var result = await client.GetMessagesAsync("the-group", "general", after: 3, limit: 50, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([4L, 5L], result.Select(message => message.Id));
        Assert.Contains("after=3", handler.Requests.Single().RequestUri!.Query);
        Assert.Contains("limit=50", handler.Requests.Single().RequestUri!.Query);
    }

    [Theory]
    [InlineData(CommunityAccessModes.Public, null, null)]
    [InlineData(CommunityAccessModes.Password, "password8", null)]
    [InlineData(CommunityAccessModes.InviteCode, null, "invite88")]
    [InlineData(CommunityAccessModes.PasswordOrInviteCode, "password8", null)]
    [InlineData(CommunityAccessModes.PasswordOrInviteCode, null, "invite88")]
    public void Join_validation_supports_each_access_mode(string mode, string? password, string? code) =>
        CommunitiesApiClient.ValidateJoinRequest(mode, password, code);

    [Theory]
    [InlineData(CommunityAccessModes.Password, null, null)]
    [InlineData(CommunityAccessModes.InviteCode, null, null)]
    [InlineData(CommunityAccessModes.PasswordOrInviteCode, null, null)]
    public void Join_validation_rejects_missing_credentials(string mode, string? password, string? code) =>
        Assert.Throws<ArgumentException>(() => CommunitiesApiClient.ValidateJoinRequest(mode, password, code));

    [Fact]
    public async Task Invite_only_community_is_hidden_until_membership_is_known()
    {
        var handler = new QueueHandler(_ => Json(HttpStatusCode.OK, """{"communities":[{"slug":"secret-group","name":"Secret","description":"private","accessMode":"invite-code","isMember":false,"memberCount":1},{"slug":"open-group","name":"Open","description":"public","accessMode":"public","isMember":false,"memberCount":1}]}"""));
        using var client = new CommunitiesApiClient(Client(handler), new TestSigner());

        var result = await client.ListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result.Communities);
        Assert.Equal("open-group", result.Communities[0].Slug);
    }

    [Theory]
    [InlineData("", "public", null, null)]
    [InlineData("valid", "unknown", null, null)]
    [InlineData("valid", "password", "short", null)]
    [InlineData("valid", "password-or-invite-code", null, null)]
    public void Create_validation_rejects_invalid_input(string name, string mode, string? password, string? code) =>
        Assert.Throws<ArgumentException>(() => CommunitiesApiClient.ValidateCreateRequest(new(name, "", mode, password, code)));

    [Fact]
    public void Create_validation_trims_strings_and_keeps_optional_values()
    {
        var request = CommunitiesApiClient.ValidateCreateRequest(new("  Group  ", "  description  ", "public", PackageSlug: "cool-pack"));
        Assert.Equal("Group", request.Name);
        Assert.Equal("description", request.Description);
        Assert.Equal("cool-pack", request.PackageSlug);
    }

    [Fact]
    public async Task Null_signer_enables_local_demo_mutations_and_marks_demo_mode()
    {
        using var client = new CommunitiesApiClient(new HttpClient(new QueueHandler(_ => throw new HttpRequestException("offline")))
        {
            BaseAddress = new Uri("https://example.test/")
        });

        Assert.True(client.IsDemoMode);
        var created = await client.CreateAsync(new("My group", "local", CommunityAccessModes.Public), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(created.IsLocalDemo);
        Assert.True(created.IsMember);
        var joined = await client.JoinAsync("demo-wyprawa", CommunityAccessModes.Public, null, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(joined.IsMember);
        await client.SendMessageAsync(created.Slug, "general", "local message", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await client.GetMessagesAsync(created.Slug, "general", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://example.test/") };
    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class TestSigner : ISignedRequestSigner
    {
        public string? DeviceKey => "device-key";
        public Task<IReadOnlyDictionary<string, string>> SignAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private sealed class QueueHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));
            return Task.FromResult(reply(request));
        }
    }
}

public class CommunitiesHeadlessTests
{
    [AvaloniaFact]
    public async Task Communities_and_chat_views_render_in_demo_mode()
    {
        var client = new CommunitiesApiClient(new HttpClient(new OfflineHandler()) { BaseAddress = new Uri("https://example.test/") });
        var vm = new CommunitiesViewModel(null, TestAccounts.TempPath(), client, new EmptyModpackApiClient());
        var view = new Phaket.Views.CommunitiesView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsDemoMode);
        Assert.NotEmpty(vm.Communities);
        Assert.NotEmpty(window.GetVisualDescendants().OfType<TextBlock>());
        Save(window, "30-communities.png");

        vm.SelectedCommunity = vm.Communities.First(community => community.IsMember);
        await vm.OpenChatCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsChatOpen);
        Save(window, "31-chat.png");
        vm.CloseChatCommand.Execute(null);
        window.Close();
    }

    private static void Save(Window window, string filename)
    {
        var directory = RenderArtifacts.Directory;
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(RenderArtifacts.Path(filename), PngBitmapEncoderOptions.Default);
    }

    private sealed class CachedDemoApi(ICommunitiesApiClient inner) : ICommunitiesApiClient
    {
        public bool IsDemoMode => inner.IsDemoMode;
        public Task<CommunityPage> ListAsync(string? query = null, string? cursor = null, CancellationToken cancellationToken = default) => inner.ListAsync(query, cursor, cancellationToken);
        public Task<CommunityDetails?> GetAsync(string slug, CancellationToken cancellationToken = default) => inner.GetAsync(slug, cancellationToken);
        public Task<CommunitySummary> CreateAsync(CreateCommunityRequest request, CancellationToken cancellationToken = default) => inner.CreateAsync(request, cancellationToken);
        public Task<CommunitySummary> JoinAsync(string slug, string accessMode, string? password, string? inviteCode, CancellationToken cancellationToken = default) => inner.JoinAsync(slug, accessMode, password, inviteCode, cancellationToken);
        public Task LeaveAsync(string slug, CancellationToken cancellationToken = default) => inner.LeaveAsync(slug, cancellationToken);
        public Task<IReadOnlyList<CommunityMessage>> GetMessagesAsync(string slug, string channelId, long after = 0, int limit = 50, CancellationToken cancellationToken = default) => inner.GetMessagesAsync(slug, channelId, after, limit, cancellationToken);
        public Task<CommunityMessage?> SendMessageAsync(string slug, string channelId, string content, CancellationToken cancellationToken = default) => inner.SendMessageAsync(slug, channelId, content, cancellationToken);
    }

    private sealed class EmptyModpackApiClient : IModpackApiClient
    {
        public Task<ModpackSearchPage> SearchAsync(string gameId, string? query, string? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new ModpackSearchPage([], null, true));
        public Task<ModpackDefinition?> GetAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult<ModpackDefinition?>(null);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
    }
}
