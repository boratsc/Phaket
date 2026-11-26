using System.Text;
using System.Text.Json;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Platforms.Aurelia;

namespace Phaket.Core.Tests;

// PAK-137: the Aurelia driver is exercised only through a fake process runner. No test starts
// the real binary, touches the network or signs in to Steam.
public sealed class AureliaAdapterTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-aurelia-").FullName;
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private string Binary(string name = "aurelia")
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, "fake");
        return path;
    }

    private AureliaRunner Runner(FakeRunner fake, string? binary = null, TimeSpan? timeout = null) =>
        new(binary ?? Binary(), Path.Combine(root, "aurelia-config"), timeout, fake);

    private static string Capabilities(params string[] features) => JsonSerializer.Serialize(new
    {
        name = "aurelia", version = "0.3.1", schema = 1,
        commands = new Dictionary<string, object> { ["list"] = new { }, ["manifests"] = new { }, ["branches"] = new { }, ["capabilities"] = new { }, ["install"] = new { } },
        features,
        errors = new[] { new { type = "offline", exit_code = 86, hint = "no network connection; reconnect and retry" } }
    });

    // A list row exactly as `aurelia list --json` serialises LibraryGame, plus the extra fields.
    private static string ListRow(string id, string name, bool owned = true, bool shared = false, int? minutes = 5) =>
        $$"""{"app_id":{{id}},"name":"{{name}}","playtime_forever_minutes":{{(minutes is null ? "null" : minutes.ToString())}},"is_installed":false,"install_path":null,"local_manifest_ids":{},"update_available":false,"update_queued":false,"active_branch":"public","is_owned":{{(owned ? "true" : "false")}},"is_family_shared":{{(shared ? "true" : "false")}},"online_required":null,"platform":null,"from_windows_steam":false,"size_on_disk":null,"assets":{"header":"https://cdn.test/header.jpg"},"store_url":"https://store.test/app/{{id}}","collections":[]}""";

    private static string List(params string[] rows) => "[\n  " + string.Join(",\n  ", rows) + "\n]\n";

    private AureliaLibraryService Service(FakeRunner fake, Func<CancellationToken, Task<IReadOnlyList<OwnedSteamGame>>>? local = null,
        IReadOnlyList<GameDefinition>? definitions = null) =>
        new(Runner(fake), local ?? (_ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>([])), definitions ?? []);

    // --- read-only allowlist ---------------------------------------------------------------

    // There is no generic builder: the contract exposes one argv method per read-only command, so
    // install/login/play cannot be expressed at all. These cases pin that surface.
    [Fact]
    public void ContractExposesOnlyReadOnlyCommands()
    {
        Assert.Equal(["Branches", "Capabilities", "List", "Manifests", "Version"], CommandMethods());
    }

    [Fact]
    public void ContractBuildsReadOnlyArgvWithJsonLast()
    {
        Assert.Equal(["capabilities", "--json"], AureliaContract.Capabilities());
        Assert.Equal(["list", "--json"], AureliaContract.List());
        Assert.Equal(["manifests", "945360", "--json"], AureliaContract.Manifests("945360"));
        Assert.Equal(["manifests", "945360", "--depot", "945361", "--json"], AureliaContract.Manifests("945360", "945361"));
        Assert.Equal(["branches", "945360", "--json"], AureliaContract.Branches("945360"));
        Assert.Equal(["--version", "--json"], AureliaContract.Version());
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc")]
    [InlineData("1; rm -rf /")]
    [InlineData("1234\nlogout")]
    [InlineData("-1")]
    [InlineData("10 --depot 1")]
    [InlineData("18446744073709551616")]
    public void ContractRefusesAnythingButADecimalId(string id)
    {
        Assert.Throws<ArgumentException>(() => AureliaContract.Manifests(id));
        Assert.Throws<ArgumentException>(() => AureliaContract.Manifests("945360", id));
        Assert.Throws<ArgumentException>(() => AureliaContract.Branches(id));
    }

    [Fact]
    public async Task RefusedIdNeverReachesTheProcess()
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities());
        var service = Service(fake);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetManifestsAsync("945360", "10 --manifest /etc/passwd", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(fake.Runs, run => run.Arguments.Contains("manifests"));
    }

    private static string[] CommandMethods() => typeof(AureliaContract)
        .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(method => method.ReturnType == typeof(string[])).Select(method => method.Name).Order().ToArray();


    // --- runner ---------------------------------------------------------------------------

    [Fact]
    public async Task RunnerIsolatesConfigDirAndForbidsDaemonSpawn()
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities());
        await Runner(fake).RunAsync(["capabilities", "--json"], TestContext.Current.CancellationToken);

        var run = Assert.Single(fake.Runs);
        Assert.Equal(Path.Combine(root, "aurelia-config"), run.Environment!["AURELIA_CONFIG_DIR"]);
        Assert.Equal("1", run.Environment["AURELIA_NO_SPAWN"]);
        Assert.Equal(["capabilities", "--json"], run.Arguments);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("timeout")]
    public async Task RunnerMapsAMissingBinaryOrADeadlineToOneCode(string mode)
    {
        var fake = new FakeRunner();
        if (mode == "timeout") fake.Hang();
        var runner = mode == "missing" ? new AureliaRunner(null, Path.Combine(root, "cfg"), process: fake)
            : Runner(fake, timeout: TimeSpan.FromMilliseconds(200));

        var error = await Assert.ThrowsAsync<PhaketException>(() => runner.RunAsync(["capabilities", "--json"], TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.AureliaUnavailable, error.Error.Code);
        Assert.Equal(mode == "missing" ? "missing" : "timeout", error.Error.Parameters["reason"]);
        if (mode == "missing") Assert.Empty(fake.Runs);
    }

    [Fact]
    public async Task NonZeroExitKeepsTheStreamsSoTheTypedErrorSurvives()
    {
        var fake = new FakeRunner().Reply("capabilities", "", stderr: "{\"error\":\"boom\",\"type\":\"unknown\"}\n", exitCode: 1);

        var run = await Runner(fake).RunAsync(["capabilities", "--json"], TestContext.Current.CancellationToken);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("boom", AureliaOutput.Failure(run)!.Message);
    }

    [Fact]
    public async Task StallIsACancellableTimeoutThatEndsTheWait()
    {
        var fake = new FakeRunner().Stall();
        var runner = Runner(fake, timeout: TimeSpan.FromMilliseconds(150));
        var started = DateTime.UtcNow;

        // The fake ignores the runner's token, so the deadline is what must end the wait.
        var error = await Assert.ThrowsAsync<PhaketException>(() => runner.RunAsync(["list", "--json"], TestContext.Current.CancellationToken));

        Assert.Equal("timeout", error.Error.Parameters["reason"]);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CallerCancellationIsNotTurnedIntoAToolError()
    {
        var fake = new FakeRunner().Stall();
        using var cts = new CancellationTokenSource();
        var task = Runner(fake).RunAsync(["list", "--json"], cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void StampFollowsTheBinaryOnDisk()
    {
        string path = Binary();
        var runner = Runner(new FakeRunner(), path);
        string before = runner.Stamp;
        File.WriteAllText(path, "another build");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.NotEqual(before, runner.Stamp);
        Assert.StartsWith(Path.GetFullPath(path) + ":", runner.Stamp, StringComparison.Ordinal);
        Assert.Equal("none", new AureliaRunner(null, Path.Combine(root, "cfg")).Stamp);
    }

    // --- NDJSON parsing -------------------------------------------------------------------

    [Fact]
    public void ResultIsTheLastLineWithAnsiStripped()
    {
        const string stdout = "\u001b[32m{\"event\":\"progress\",\"percent\":10}\u001b[0m\r\n{\"event\":\"result\",\"schema\":1,\"logged_in\":true,\"steam_id\":76561198000000000}\u001b[0m";
        var run = new AureliaRunResult(0, stdout, "\u001b[2mdebug\u001b[0m\n");

        var result = AureliaOutput.Result(run)!.Value;

        Assert.Equal("result", AureliaOutput.String(result, "event"));
        Assert.Equal("76561198000000000", AureliaOutput.Id(result, "steam_id"));
        Assert.Null(AureliaOutput.Failure(run));
    }

    [Fact]
    public void ResultFallsBackToAQuietStderrWhenStdoutIsEmpty()
    {
        var run = new AureliaRunResult(0, "\u001b[33mwarning\u001b[0m\n", "{\"app_id\":945360,\"available\":true}\n");

        var result = AureliaOutput.Result(run)!.Value;

        Assert.Equal("945360", AureliaOutput.Id(result, "app_id"));
        Assert.Null(AureliaOutput.Failure(run));
    }

    [Fact]
    public void TypedErrorKeepsTypeHintAndExitCode()
    {
        var run = new AureliaRunResult(86, "", "{\"error\":\"no network route\",\"type\":\"offline\",\"hint\":\"no network connection; reconnect and retry\"}\n");

        var failure = AureliaOutput.Failure(run);

        Assert.NotNull(failure);
        Assert.Equal("offline", failure.Type);
        Assert.Equal("no network route", failure.Message);
        Assert.Contains("reconnect", failure.Hint);
        Assert.Equal(86, failure.ExitCode);
        Assert.Equal(ErrorCodes.AureliaOffline, AureliaOutput.Code(failure.Type));
        Assert.Equal(ErrorCodes.AureliaAuthRequired, AureliaOutput.Code("login_denied"));
        Assert.Equal(ErrorCodes.AureliaAuthRequired, AureliaOutput.Code("login_expired"));
        Assert.Equal(ErrorCodes.AureliaCancelled, AureliaOutput.Code("cancelled"));
        Assert.Equal(ErrorCodes.AureliaFailed, AureliaOutput.Code("disk_full"));
        Assert.Equal(ErrorCodes.AureliaFailed, AureliaOutput.Code(null));
    }

    [Fact]
    public void FailedResultAndStreamedErrorAreFailures()
    {
        Assert.Equal("login_denied", AureliaOutput.Failure(new AureliaRunResult(80,
            "{\"account\":\"me\",\"logged_in\":true}\n{\"error\":\"QR sign-in was declined\",\"event\":\"result\",\"ok\":false,\"schema\":1,\"type\":\"login_denied\"}\n", ""))?.Type);
        Assert.Equal("timeout", AureliaOutput.Failure(new AureliaRunResult(82, "", "{\"event\":\"error\",\"type\":\"timeout\",\"message\":\"no approval\"}\n"))?.Type);
        Assert.Null(AureliaOutput.Failure(new AureliaRunResult(0, "{\"event\":\"result\",\"ok\":true,\"schema\":1}\n", "")));
    }

    [Fact]
    public void ParserIgnoresBracesInsideStrings()
    {
        var objectValue = Assert.Single(AureliaJson.Values("{\"a\":\"}\"}"));
        Assert.Equal("}", objectValue!.Value.GetProperty("a").GetString());

        var array = Assert.Single(AureliaJson.Values("[{\"a\":1},{\"a\":2}]"));
        Assert.Equal(2, array!.Value.EnumerateArray().Count());

        var escaped = Assert.Single(AureliaJson.Values("prefix {\"a\":\"say \\\"hi\\\"\"} suffix"));
        Assert.Equal("say \"hi\"", escaped!.Value.GetProperty("a").GetString());
    }

    [Fact]
    public void TruncatedValueIsNotAValue()
    {
        Assert.Empty(AureliaJson.Values("{\"event\":\"result\",\"schema\":1,"));
        Assert.Empty(AureliaJson.Values("{\"name\":\"unterminated"));
        Assert.Equal(2, AureliaJson.Values("{\"a\":1}\n{\"b\":2}").Count);
    }

    // --- capabilities gate ----------------------------------------------------------------

    [Fact]
    public void GateAcceptsThePinnedContract()
    {
        var capabilities = AureliaCapabilities.Parse(Capabilities("login.qr.result_line", "result.schema"));

        Assert.NotNull(capabilities);
        Assert.Equal("0.3.1", capabilities.Version);
        Assert.Equal(1, capabilities.Schema);
        Assert.True(capabilities.IsSupported);
        Assert.Contains("install", capabilities.Commands);
    }

    [Fact]
    public void GateDisablesAMissingCommandNewerSchemaOrNoVersion()
    {
        string withoutList = JsonSerializer.Serialize(new
        {
            version = "0.3.1", schema = 1,
            commands = new Dictionary<string, object> { ["manifests"] = new { }, ["branches"] = new { } }
        });
        Assert.False(AureliaCapabilities.Parse(withoutList)!.IsSupported);
        Assert.False(AureliaCapabilities.Parse(Capabilities().Replace("\"schema\":1", "\"schema\":2"))!.IsSupported);
        Assert.False(AureliaCapabilities.Parse(Capabilities().Replace("\"version\":\"0.3.1\",", ""))!.IsSupported);
        Assert.Null(AureliaCapabilities.Parse("not json at all"));
        Assert.Null(AureliaCapabilities.Parse("{\"error\":\"offline\",\"type\":\"offline\"}"));
        // Colourised capabilities output still parses.
        Assert.True(AureliaCapabilities.Parse("\u001b[1mAurelia\u001b[0m\n" + Capabilities())!.IsSupported);
    }

    [Fact]
    public async Task MissingListCommandTurnsTheIntegrationOffAndUsesTheLocalDetection()
    {
        var fake = new FakeRunner().Reply("capabilities", "{\"version\":\"0.1.0\",\"schema\":1,\"commands\":{\"manifests\":{}}}");
        var local = new List<string> { "local" };
        var service = Service(fake, _ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>(local.Select(id => new OwnedSteamGame(id, id, null, null, "local", false)).ToArray()));

        var games = await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);

        var game = Assert.Single(games);
        Assert.Equal("local", game.Source);
        Assert.False(await service.IsSupportedAsync(TestContext.Current.CancellationToken));
        Assert.Equal([["capabilities", "--json"]], fake.Runs.Select(r => r.Arguments).ToArray());
    }

    [Fact]
    public async Task GateIsCachedPerBinaryStamp()
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities()).Reply("list", List(ListRow("945360", "Among Us")));
        var service = Service(fake);

        await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);
        await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.Count("capabilities"));
        Assert.Equal(2, fake.Count("list"));
    }

    [Fact]
    public async Task ReplacedBinaryInvalidatesTheGate()
    {
        string binary = Binary();
        var fake = new FakeRunner().Reply("capabilities", Capabilities()).Reply("list", List(ListRow("945360", "Among Us")));
        var service = new AureliaLibraryService(new AureliaRunner(binary, Path.Combine(root, "cfg"), process: fake), (Func<CancellationToken, Task<IReadOnlyList<OwnedSteamGame>>>?)null, []);

        await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(binary, "a newer build");
        File.SetLastWriteTimeUtc(binary, DateTime.UtcNow.AddMinutes(1));
        await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, fake.Count("capabilities"));
    }

    // --- library mapping and fallback ------------------------------------------------------

    [Fact]
    public async Task ListMapsLicencesFamilySharingAndDefinitions()
    {
        var definitions = new[] { new GameDefinition("among-us", [], new Dictionary<string, string> { ["steam"] = "945360" }) };
        var fake = new FakeRunner().Reply("capabilities", Capabilities())
            .Reply("list", List(ListRow("945360", "Among Us"), ListRow("620", "Portal 2", owned: false, shared: true, minutes: null)));
        var service = Service(fake, definitions: definitions);

        var games = await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, games.Count);
        Assert.Equal("aurelia", games[0].Source);
        Assert.Equal("among-us", games[0].Definition!.Slug);
        Assert.Equal(5, games[0].PlaytimeMinutes);
        Assert.Equal("aurelia-family", games[1].Source);
        Assert.True(games[1].LowerConfidence);
        Assert.Null(games[1].PlaytimeMinutes);
    }

    [Fact]
    public async Task StringAndNumberIdsBothMap()
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities())
            .Reply("list", "[{\"app_id\":\"945360\",\"name\":\"Among Us\",\"is_installed\":true,\"update_available\":false,\"is_owned\":true,\"is_family_shared\":false,\"active_branch\":\"public\"}]");
        var service = Service(fake);

        var game = Assert.Single(await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("945360", game.AppId);
        Assert.Null(game.PlaytimeMinutes);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("throw")]
    [InlineData("offline")]
    [InlineData("shape")]
    [InlineData("row-drift")]
    public async Task EveryToolProblemFallsBackToTheLocalList(string mode)
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities());
        if (mode == "exit") fake.Reply("list", "{\"error\":\"boom\",\"type\":\"unknown\"}", exitCode: 1);
        if (mode == "throw") fake.Throw("list", new IOException("pipe closed"));
        if (mode == "offline") fake.Reply("list", "", stderr: "{\"error\":\"no route\",\"type\":\"offline\"}\n");
        if (mode == "shape") fake.Reply("list", "{\"games\":[]}");
        if (mode == "row-drift") fake.Reply("list", "[{\"app_id\":945360,\"name\":\"Among Us\"}]");
        var service = Service(fake, _ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>([new OwnedSteamGame("945360", "Among Us", null, null, "local", false)]));

        var games = await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("local", Assert.Single(games).Source);
    }

    [Fact]
    public async Task TimeoutFallsBackToTheLocalList()
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities()).Stall();
        var service = new AureliaLibraryService(new AureliaRunner(Binary(), Path.Combine(root, "cfg"), TimeSpan.FromMilliseconds(200), fake),
            _ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>([new OwnedSteamGame("945360", "Among Us", null, null, "local", false)]), []);

        var games = await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("local", Assert.Single(games).Source);
    }

    [Fact]
    public async Task MissingBinaryNeverStartsAProcessAndFallsBack()
    {
        var fake = new FakeRunner();
        var service = new AureliaLibraryService(new AureliaRunner(null, Path.Combine(root, "cfg"), process: fake),
            _ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>([new OwnedSteamGame("1", "Local", null, null, "local", false)]), []);

        Assert.Equal("local", Assert.Single(await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken)).Source);
        Assert.Empty(fake.Runs);
        var error = await Assert.ThrowsAsync<PhaketException>(() => service.GetManifestsAsync("945360", ct: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.AureliaUnavailable, error.Error.Code);
    }

    [Fact]
    public async Task LocalServiceIsTheRealSteamFallback()
    {
        // The same composition the UI will use: SteamLibraryService answers when Aurelia is absent.
        var home = Directory.CreateTempSubdirectory("phaket-aurelia-home-").FullName;
        try
        {
            var steamapps = Path.Combine(home, ".local", "share", "Steam", "steamapps");
            Directory.CreateDirectory(Path.Combine(steamapps, "common", "Among Us"));
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_945360.acf"), "\"AppState\" { \"appid\" \"945360\" \"name\" \"Among Us\" \"buildid\" \"123\" \"installdir\" \"Among Us\" }");
            var service = new AureliaLibraryService(new AureliaRunner(null, Path.Combine(root, "cfg")), "76561198000000000", [], Path.Combine(home, "data"), home);

            var games = await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);

            if (!OperatingSystem.IsLinux()) return;
            Assert.Equal("local", Assert.Single(games).Source);
        }
        finally { Directory.Delete(home, true); }
    }

    // --- manifests and branches -----------------------------------------------------------

    [Fact]
    public async Task ManifestsMapDepotBranchAndPin()
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities()).Reply("manifests",
            "[\n  {\n    \"depot_id\": 945361,\n    \"depot_name\": \"Among Us Content\",\n    \"branch\": \"public\",\n    \"manifest_id\": 18446744073709551615,\n    \"size\": 0\n  },\n  {\"depot_id\":945362,\"depot_name\":null,\"branch\":\"beta\",\"manifest_id\":1234567890123456789,\"size\":734003200}\n]\n");
        var service = Service(fake);

        var manifests = await service.GetManifestsAsync("945360", ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, manifests.Count);
        Assert.Equal("945361", manifests[0].DepotId);
        Assert.Equal("public", manifests[0].Branch);
        Assert.Equal("18446744073709551615", manifests[0].ManifestId);
        Assert.Equal("945361:18446744073709551615", manifests[0].Pin);
        Assert.Equal(734003200, manifests[1].Size);
        Assert.Equal(["manifests", "945360", "--json"], fake.Runs[^1].Arguments);
    }

    [Fact]
    public async Task ManifestErrorIsTypedForTheCaller()
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities()).Reply("manifests 945360 --json", "", stderr: "{\"error\":\"login first\",\"type\":\"auth_required\",\"hint\":\"run `aurelia login`\"}\n", exitCode: 77);
        var service = Service(fake);

        var error = await Assert.ThrowsAsync<PhaketException>(() => service.GetManifestsAsync("945360", ct: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.AureliaAuthRequired, error.Error.Code);
        Assert.Equal("login first", error.Error.Parameters["detail"]);
    }

    [Fact]
    public async Task BranchesReadTheWrappedArray()
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities()).Reply("branches", "{\n  \"app_id\": 945360,\n  \"branches\": [\n    \"public\",\n    \"beta\"\n  ]\n}\n");
        var service = Service(fake);

        Assert.Equal(["public", "beta"], await service.GetBranchesAsync("945360", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManifestsRefuseAWriteCommandBeforeTheProcess()
    {
        var fake = new FakeRunner().Reply("capabilities", Capabilities(), exitCode: 0);
        var service = Service(fake);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetManifestsAsync("945360", "1 --manifest /etc/passwd", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fake.Runs, run => run.Arguments.Contains("manifests"));
    }

    // --- fakes ------------------------------------------------------------------------------

    private sealed record Run(string[] Arguments, IDictionary<string, string?>? Environment);

    private sealed class FakeRunner : IAureliaProcessRunner
    {
        private readonly List<Run> runs = [];
        private readonly Dictionary<string, Func<CancellationToken, Task<AureliaRunResult>>> replies = new(StringComparer.Ordinal);
        private bool hanging;
        private bool stalling;

        public IReadOnlyList<Run> Runs { get { lock (runs) return [.. runs]; } }

        public int Count(string command) => Runs.Count(r => r.Arguments.Length > 0 && r.Arguments[0] == command);

        public FakeRunner Reply(string command, string stdout, string stderr = "", int exitCode = 0)
        {
            replies[command] = _ => Task.FromResult(new AureliaRunResult(exitCode, stdout, stderr));
            return this;
        }

        public FakeRunner Throw(string command, Exception error)
        {
            replies[command] = _ => Task.FromException<AureliaRunResult>(error);
            return this;
        }

        // Waits until the runner's deadline cancels it, like a process still starting up.
        public FakeRunner Hang()
        {
            hanging = true;
            return this;
        }

        public FakeRunner Stall()
        {
            stalling = true;
            return this;
        }

        // Suspends first, like a real process call: a runner that starts synchronously would run its
        // deadline inside the caller's call and never observe a timeout.
        public async Task<AureliaRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, CancellationToken ct)
        {
            await Task.Yield();
            string joined = string.Join(" ", arguments);
            lock (runs) runs.Add(new Run([.. arguments], new Dictionary<string, string?>(environment)));
            if (hanging) { long until = Environment.TickCount64 + 20_000; while (Environment.TickCount64 < until) ct.ThrowIfCancellationRequested(); return new AureliaRunResult(0, "", ""); }
            // Ignores the token on purpose: the runner's own deadline has to end the wait.
            if (stalling) { long until = Environment.TickCount64 + 3_000; while (Environment.TickCount64 < until) System.Threading.Thread.Sleep(10); return new AureliaRunResult(0, "", ""); }
            string command = arguments.Count == 0 || arguments[0].StartsWith("--") ? "" : arguments[0];
            if (replies.TryGetValue(joined, out var exact)) return await exact(ct);
            return replies.TryGetValue(command, out var reply) ? await reply(ct) : throw new InvalidOperationException($"unexpected aurelia run: {joined}");
        }
    }
}