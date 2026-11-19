using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Accounts;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// Client half of the community contract. Payload shapes are copied from
// PhaketServer/Phaket.Api/Communities.cs so both the current server replies ({id, slug},
// {joined: true}, nick + fingerprint) and the full records keep working.
public sealed class CommunitiesContractTests
{
    private const string RecordId = "8f14e45f-ceea-467a-a3bb-3f4b0a1d2c34";
    private const string ChannelId = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    // GET /communities/{slug} -> CommunityApi.Detail
    private static string FullRecord(bool isMember = true) => $$"""
        {"id":"{{RecordId}}","slug":"grupa","name":"Grupa Granica","description":"Wspólne wypady",
         "accessMode":"public","packageSlug":"cool-pack","memberCount":3,
         "isMember":{{(isMember ? "true" : "false")}},
         "channels":[{"id":"{{ChannelId}}","name":"ogólny"}],
         "createdAt":"2026-11-19T10:00:00Z"}
        """;

    // POST /communities -> Results.Created($"/communities/{slug}", new { id, slug })
    private const string CreateReply = $$"""{"id":"{{RecordId}}","slug":"grupa"}""";

    // POST /communities/{slug}/join -> Results.Ok(new { joined = true })
    private const string JoinReply = """{"joined":true}""";

    // GET .../messages -> { id, channelId, nick, fingerprint, body, createdAt }
    private const string ServerMessages = $$"""
        {"messages":[{"id":7,"channelId":"{{ChannelId}}","nick":"Anka","fingerprint":"a1b2c3d4",
        "body":"Cześć!","createdAt":"2026-11-19T10:00:00Z"}],"nextCursor":7}
        """;

    // Shape an older server used to send; the client must still read it.
    private const string LegacyMessages =
        """{"messages":[{"id":7,"channelId":"general","authorNickname":"Anka","authorKey":"a1b2c3d4ef","content":"Cześć!","createdAt":"2026-11-19T10:00:00Z"}]}""";

    // POST .../messages -> Results.Created(..., new { id, channelId, body, createdAt })
    private const string SendMessageReply = $$"""{"id":8,"channelId":"{{ChannelId}}","body":"Nowa","createdAt":"2026-11-19T10:05:00Z"}""";

    private static string ErrorPayload(string code) =>
        "{\"error\":{\"code\":\"" + code + "\",\"message\":{\"pl-PL\":\"Błąd.\",\"en\":\"Failure.\"}}}";

    [Fact]
    public async Task Create_reply_of_id_and_slug_is_upgraded_to_the_full_record()
    {
        var routes = Routes(
            (HttpMethod.Post, "/communities", () => Json(HttpStatusCode.Created, CreateReply)),
            (HttpMethod.Get, "/communities/grupa", () => Json(HttpStatusCode.OK, FullRecord(isMember: false))));
        using var api = Client(routes);

        var created = await api.CreateAsync(new("Grupa Granica", "Wspólne wypady", CommunityAccessModes.Public));

        Assert.Equal("grupa", created.Slug);
        Assert.Equal("Grupa Granica", created.Name);
        Assert.Equal("Wspólne wypady", created.Description);
        Assert.Equal("cool-pack", created.PackageSlug);
        Assert.Equal(3, created.MemberCount);
        Assert.Equal(["POST", "GET"], routes.Requests.Select(request => request.Method).ToArray());
    }

    [Fact]
    public async Task Create_reply_with_a_full_record_needs_no_second_request()
    {
        var routes = Routes((HttpMethod.Post, "/communities", () => Json(HttpStatusCode.Created, FullRecord())));
        using var api = Client(routes);

        var created = await api.CreateAsync(new("Grupa Granica", "", CommunityAccessModes.Public));

        Assert.Equal("Grupa Granica", created.Name);
        Assert.True(created.IsMember);
        Assert.Equal(["POST"], routes.Requests.Select(request => request.Method).ToArray());
    }

    [Fact]
    public async Task Create_without_any_usable_record_is_reported()
    {
        var routes = Routes((HttpMethod.Post, "/communities", () => Json(HttpStatusCode.Created, $$"""{"id":"{{RecordId}}"}""")));
        using var api = Client(routes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            api.CreateAsync(new("Grupa", "", CommunityAccessModes.Public)));
    }

    [Fact]
    public async Task Join_reply_of_joined_flag_is_upgraded_to_the_full_record()
    {
        var routes = Routes(
            (HttpMethod.Post, "/communities/grupa/join", () => Json(HttpStatusCode.OK, JoinReply)),
            (HttpMethod.Get, "/communities/grupa", () => Json(HttpStatusCode.OK, FullRecord())));
        using var api = Client(routes);

        var joined = await api.JoinAsync("grupa", CommunityAccessModes.Public, null, null);

        Assert.Equal("Grupa Granica", joined.Name);
        Assert.Equal(3, joined.MemberCount);
        Assert.True(joined.IsMember);
    }

    [Fact]
    public async Task Join_without_a_readable_record_keeps_membership_and_slug()
    {
        var routes = Routes(
            (HttpMethod.Post, "/communities/grupa/join", () => Json(HttpStatusCode.OK, JoinReply)),
            (HttpMethod.Get, "/communities/grupa", () => Json(HttpStatusCode.NotFound, "{}")));
        using var api = Client(routes);

        var joined = await api.JoinAsync("grupa", CommunityAccessModes.Password, "password8", null);

        Assert.Equal("grupa", joined.Slug);
        Assert.True(joined.IsMember);
        Assert.Equal(CommunityAccessModes.Password, joined.AccessMode);
    }

    [Theory]
    [InlineData(ServerMessages, "Anka", "a1b2c3d4", "Cześć!")]
    [InlineData(LegacyMessages, "Anka", "a1b2c3d4ef", "Cześć!")]
    public async Task Messages_of_both_server_shapes_are_parsed(string payload, string nick, string key, string body)
    {
        var routes = Routes((HttpMethod.Get, "/communities/grupa/channels", () => Json(HttpStatusCode.OK, payload)));
        using var api = Client(routes);

        var messages = await api.GetMessagesAsync("grupa", ChannelId, after: 6);

        var message = Assert.Single(messages);
        Assert.Equal(7, message.Id);
        Assert.Equal(nick, message.AuthorNickname);
        Assert.Equal(key, message.AuthorKey);
        Assert.Equal(body, message.Content);
    }

    [Fact]
    public async Task Send_reply_without_an_author_is_read_back_from_the_server()
    {
        var stored = $$"""{"messages":[{"id":8,"channelId":"{{ChannelId}}","nick":"Anka","fingerprint":"a1b2c3d4","body":"Nowa","createdAt":"2026-11-19T10:05:00Z"}]}""";
        var routes = Routes(
            (HttpMethod.Post, "/communities/grupa/channels", () => Json(HttpStatusCode.Created, SendMessageReply)),
            (HttpMethod.Get, "/communities/grupa/channels", () => Json(HttpStatusCode.OK, stored)));
        using var api = Client(routes);

        var sent = await api.SendMessageAsync("grupa", ChannelId, "Nowa");

        Assert.NotNull(sent);
        Assert.Equal(8, sent!.Id);
        Assert.Equal("Anka", sent.AuthorNickname);
        Assert.Equal("a1b2c3d4", sent.AuthorKey);
        Assert.Contains("after=7", routes.Requests[^1].Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DEVICE_REPLAY", 409)]
    [InlineData("COMMUNITY_SLUG_TAKEN", 409)]
    [InlineData("JOIN_DENIED", 403)]
    public async Task Error_payload_code_is_carried_by_the_exception(string code, int status)
    {
        var routes = Routes((HttpMethod.Post, "/communities",
            () => Json((HttpStatusCode)status, ErrorPayload(code))));
        using var api = Client(routes);

        var error = await Assert.ThrowsAsync<CommunityApiException>(() =>
            api.CreateAsync(new("Grupa", "", CommunityAccessModes.Public)));

        Assert.Equal(code, error.ErrorCode);
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
    }

    [Fact]
    public async Task Message_reads_are_signed_over_an_empty_body_and_public_reads_are_not()
    {
        var secretStore = new MemorySecretStore();
        await secretStore.SetAsync(DeviceKeyStore.SecretKey, new byte[32]);
        string root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        var keys = new DeviceKeyStore(secretStore, Path.Combine(root, "settings.json"));
        var signer = new DeviceKeySigner(keys, registerBeforeSigning: false);
        var routes = Routes(
            (HttpMethod.Get, "/communities/grupa/channels", () => Json(HttpStatusCode.OK, ServerMessages)),
            (HttpMethod.Get, "/communities?", () => Json(HttpStatusCode.OK, """{"communities":[],"nextCursor":null}""")));
        using var http = new HttpClient(new SignedHttpHandler(routes, keys, signer, new Uri("https://api.example.test/")))
        { BaseAddress = new Uri("https://api.example.test/") };
        using var api = new CommunitiesApiClient(http, signer);

        await api.GetMessagesAsync("grupa", ChannelId);
        await api.ListAsync();

        var messages = routes.Requests.Single(request => request.Path.Contains("/messages", StringComparison.Ordinal));
        var publicRead = routes.Requests.Single(request => request.Path.StartsWith("/communities?", StringComparison.Ordinal));
        Assert.True(messages.Headers.ContainsKey("X-Signature"));
        Assert.True(messages.Headers.ContainsKey("X-Device-Key"));
        Assert.False(publicRead.Headers.ContainsKey("X-Signature"));

        byte[] publicKey = Convert.FromBase64String(messages.Headers["X-Device-Key"]);
        string canonical = DeviceKeySigner.CreateCanonicalRepresentation("GET", messages.Path,
            messages.Headers["X-Timestamp"], messages.Headers["X-Nonce"], Array.Empty<byte>());
        Assert.True(DeviceKeySigner.VerifyForTest(publicKey, Convert.FromBase64String(messages.Headers["X-Signature"]), canonical));
    }

    [Fact]
    public async Task Placeholder_join_summary_never_overwrites_a_known_community()
    {
        var api = new ScriptedApi(
            list: [new("grupa", "Grupa Granica", "Wspólne wypady", CommunityAccessModes.Public, "cool-pack", 3, false)],
            joinResult: new("grupa", "grupa", "", CommunityAccessModes.Public, null, 0, true));
        var viewModel = new CommunitiesViewModel(null, TestAccounts.TempPath(), api, new NoPackages());

        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.JoinTarget = viewModel.Communities.Single();
        await viewModel.JoinSelectedCommand.ExecuteAsync(null);

        var item = viewModel.Communities.Single();
        Assert.Equal("Grupa Granica", item.Name);
        Assert.Equal("Wspólne wypady", item.Description);
        Assert.Equal("cool-pack", item.Model.PackageSlug);
        Assert.True(item.IsMember);
    }

    [Fact]
    public async Task Readable_join_record_still_replaces_the_known_summary()
    {
        var api = new ScriptedApi(
            list: [new("grupa", "Stara nazwa", "", CommunityAccessModes.Public, null, 1, false)],
            joinResult: new("grupa", "Grupa Granica", "Nowy opis", CommunityAccessModes.Public, "cool-pack", 4, true));
        var viewModel = new CommunitiesViewModel(null, TestAccounts.TempPath(), api, new NoPackages());

        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.JoinTarget = viewModel.Communities.Single();
        await viewModel.JoinSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Grupa Granica", viewModel.Communities.Single().Name);
        Assert.Equal(4, viewModel.Communities.Single().Model.MemberCount);
    }

    [Fact]
    public void Create_request_with_empty_optional_fields_is_accepted()
    {
        var request = CommunitiesApiClient.ValidateCreateRequest(new("Grupa", "", CommunityAccessModes.Public, "", "", ""));

        Assert.Null(request.Password);
        Assert.Null(request.InviteCode);
        Assert.Null(request.PackageSlug);
    }

    [Theory]
    [InlineData("DEVICE_REPLAY", "Communities.DeviceReplay")]
    [InlineData("COMMUNITY_SLUG_TAKEN", "Communities.SlugTaken")]
    [InlineData("JOIN_DENIED", "Communities.JoinDenied")]
    [InlineData(null, "Communities.AlreadyMember")]
    public async Task Server_error_codes_get_their_own_messages(string? code, string expectedKey)
    {
        var api = new ScriptedApi(list: [], createError: new CommunityApiException(HttpStatusCode.Conflict, code));
        var viewModel = new CommunitiesViewModel(null, TestAccounts.TempPath(), api, new NoPackages());
        viewModel.NewCommunityName = "Grupa";

        await viewModel.CreateCommunityCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationService.Instance[expectedKey], viewModel.CommunityError);
        Assert.NotEqual(LocalizationService.Instance["Communities.CreateFailed"], viewModel.CommunityError);
    }

    [AvaloniaFact]
    public async Task Chat_polling_gives_up_after_five_failures_and_restarts_on_request()
    {
        var api = new ScriptedApi(
            list: [new("grupa", "Grupa Granica", "", CommunityAccessModes.Public, null, 3, true)],
            channels: [new(ChannelId, "ogólny")],
            messagesAlwaysFail: true);
        var viewModel = new CommunitiesViewModel(null, TestAccounts.TempPath(), api, new NoPackages(),
            TimeSpan.FromMilliseconds(5));
        try
        {
            await viewModel.RefreshCommand.ExecuteAsync(null);
            await viewModel.OpenChatCommand.ExecuteAsync(null);
            await WaitFor(() => viewModel.ChatStatus == LocalizationService.Instance["Chat.ConnectionLost"]);

            Assert.Equal(CommunitiesViewModel.MaxConsecutivePollFailures, api.MessageRequests);
            int stoppedAt = api.MessageRequests;
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(stoppedAt, api.MessageRequests);

            viewModel.RetryChatPollingCommand.Execute(null);
            await WaitFor(() => api.MessageRequests > stoppedAt);
        }
        finally
        {
            viewModel.CloseChatCommand.Execute(null);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("The chat polling loop never reached the expected state.");
    }

    private static CommunitiesApiClient Client(RouteHandler routes) =>
        new(new HttpClient(routes) { BaseAddress = new Uri("https://api.example.test/") }, new StubSigner());

    private static RouteHandler Routes(params (HttpMethod Method, string Path, Func<HttpResponseMessage> Reply)[] routes) =>
        new(routes);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed record CapturedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

    // Routes by method and path prefix, so the ?after=... query of the message reads stays irrelevant.
    private sealed class RouteHandler((HttpMethod Method, string Path, Func<HttpResponseMessage> Reply)[] routes) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.PathAndQuery;
            Requests.Add(new CapturedRequest(request.Method.Method, path, request.Headers.ToDictionary(
                header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase)));
            var match = Array.Find(routes, route => route.Method == request.Method &&
                path.StartsWith(route.Path, StringComparison.Ordinal));
            return Task.FromResult(match.Reply is null
                ? Json(HttpStatusCode.NotFound, "{}")
                : match.Reply());
        }
    }

    private sealed class StubSigner : ISignedRequestSigner
    {
        public string? DeviceKey => "ZGV2aWNlLWtleQ==";
        public Task<IReadOnlyDictionary<string, string>> SignAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.TryGetValue(key, out var value) ? value.ToArray() : null);
        public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
        {
            values[key] = value.ToArray();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class NoPackages : IModpackApiClient
    {
        public Task<ModpackSearchPage> SearchAsync(string gameId, string? query, string? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModpackSearchPage([], null, true));
        public Task<ModpackDefinition?> GetAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult<ModpackDefinition?>(null);
    }

    // Stands in for the HTTP client in the view-model tests; every message read fails so the polling loop can be observed.
    private sealed class ScriptedApi(
        IReadOnlyList<CommunitySummary> list,
        CommunitySummary? joinResult = null,
        CommunityApiException? createError = null,
        IReadOnlyList<CommunityChannel>? channels = null,
        bool messagesAlwaysFail = false) : ICommunitiesApiClient
    {
        public int MessageRequests { get; private set; }
        public bool IsDemoMode => false;

        public Task<CommunityPage> ListAsync(string? query = null, string? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommunityPage(list, null));

        public Task<CommunityDetails?> GetAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult<CommunityDetails?>(new(list.First(community => community.Slug == slug), channels ?? []));

        public Task<CommunitySummary> CreateAsync(CreateCommunityRequest request, CancellationToken cancellationToken = default) =>
            createError is null
                ? Task.FromResult(new CommunitySummary("grupa", request.Name, request.Description, request.AccessMode, null, 1, true))
                : Task.FromException<CommunitySummary>(createError);

        public Task<CommunitySummary> JoinAsync(string slug, string accessMode, string? password, string? inviteCode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(joinResult ?? throw new InvalidOperationException("No join result scripted."));

        public Task LeaveAsync(string slug, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CommunityMessage>> GetMessagesAsync(string slug, string channelId, long after = 0,
            int limit = 50, CancellationToken cancellationToken = default)
        {
            MessageRequests++;
            return messagesAlwaysFail
                ? Task.FromException<IReadOnlyList<CommunityMessage>>(new HttpRequestException("offline"))
                : Task.FromResult<IReadOnlyList<CommunityMessage>>([]);
        }

        public Task<CommunityMessage?> SendMessageAsync(string slug, string channelId, string content,
            CancellationToken cancellationToken = default) => Task.FromResult<CommunityMessage?>(null);
    }
}
