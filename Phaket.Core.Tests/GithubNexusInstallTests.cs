using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public sealed class GithubNexusInstallTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];
        public bool SawAuthorizationHeader { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            SawAuthorizationHeader |= request.Headers.Authorization is not null;
            return send(request, cancellationToken);
        }
    }

    private static byte[] Zip(string entry, string content)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = zip.CreateEntry(entry).Open())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            stream.Write(bytes);
        }
        return output.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class ManualTimeProvider : TimeProvider
    {
        private ManualTimer? timer;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer = new ManualTimer(callback, state);
            return timer;
        }

        public void ExpireTimer() => timer?.Fire();

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public void Fire() { if (!disposed) callback(state); }
        }
    }

    private static ModpackSourceInstallContext Context(ExecutorFixture fixture) => new(
        "install-1", "profile-1", fixture.GameRoot, fixture.ProfileRoot, fixture.StagingRoot,
        fixture.PackageRoot, Path.Combine(fixture.Root, "installation-state"), TestFixtures.AmongUs,
        new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false, Arch: "x86"));

    private static ModpackSourceItem GitHub(byte[] bytes, string? hash = null, long? size = null, string? url = null) => new(
        "github", "Example Mod", "v1.2.3", Repo: "example/mod", Tag: "v1.2.3", AssetId: 42,
        AssetName: "example-mod.zip", DownloadUrl: url ?? "https://github.com/example/mod/releases/download/v1.2.3/example-mod.zip",
        Size: size ?? bytes.LongLength, Sha256: hash);

    private static ModpackSourceItem Nexus(string path, string name = "Manual Mod", string version = "1.0.0") => new(
        "nexus", name, version, Url: "https://www.nexusmods.com/amongus/mods/123", PinnedVersion: version,
        LocalArchivePath: path);

    [Fact]
    public async Task Github_downloads_selected_asset_through_allowed_redirect_and_executes_the_plan()
    {
        using var fixture = new ExecutorFixture();
        var bytes = Zip("plugins/Example.dll", "pinned github bytes");
        var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.Host == "github.com"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://objects.githubusercontent.com/github-production-release-asset/example") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var installer = new ModpackSourceInstaller(new HttpPackageTransport(http));

        var plan = await installer.PrepareAsync([GitHub(bytes, Hash(bytes))], Context(fixture), TestContext.Current.CancellationToken);
        Assert.True(plan.CanInstall);
        Assert.Equal("v1.2.3", Assert.Single(plan.Items).Version);
        Assert.DoesNotContain("GITHUB_HASH_UNVERIFIED", plan.Warnings);
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(handler.SawAuthorizationHeader);
        Assert.True(installer.Execute(plan, true, cancellationToken: TestContext.Current.CancellationToken).Success);
        Assert.Equal("pinned github bytes", File.ReadAllText(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Example_Mod", "Example.dll")));
    }

    [Fact]
    public async Task Github_rejects_incorrect_size_and_hash_without_creating_the_package()
    {
        using var fixture = new ExecutorFixture();
        var bytes = Zip("plugins/Example.dll", "archive bytes");
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var installer = new ModpackSourceInstaller(new HttpPackageTransport(http));

        var sizeError = await Assert.ThrowsAsync<PhaketException>(() =>
            installer.PrepareAsync([GitHub(bytes, Hash(bytes), bytes.LongLength + 1)], Context(fixture), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ArtifactSizeMismatch, sizeError.Error.Code);
        Assert.False(File.Exists(Path.Combine(fixture.PackageRoot, "0000", "package.zip")));

        var hashError = await Assert.ThrowsAsync<PhaketException>(() =>
            installer.PrepareAsync([GitHub(bytes, Hash("wrong"u8.ToArray()))], Context(fixture), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ArtifactHashMismatch, hashError.Error.Code);
        Assert.False(File.Exists(Path.Combine(fixture.PackageRoot, "0000", "package.zip")));
    }

    [Fact]
    public async Task Github_timeout_is_stable_and_respects_the_caller_token()
    {
        using var fixture = new ExecutorFixture();
        var timeoutRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutHandler = new FakeHandler(async (_, cancellationToken) =>
        {
            timeoutRequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var timeoutClock = new ManualTimeProvider();
        using var timeoutHttp = new HttpClient(timeoutHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var timeoutInstaller = new ModpackSourceInstaller(new HttpPackageTransport(timeoutHttp), TimeSpan.FromSeconds(1), timeoutClock);
        var timeoutTask = timeoutInstaller.PrepareAsync([GitHub([], size: 1)], Context(fixture), cancellationToken: TestContext.Current.CancellationToken);

        await timeoutRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        timeoutClock.ExpireTimer();
        var error = await Assert.ThrowsAsync<PhaketException>(() => timeoutTask);
        Assert.Equal(ErrorCodes.ArtifactDownloadTimeout, error.Error.Code);

        var callerRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerHandler = new FakeHandler(async (_, cancellationToken) =>
        {
            callerRequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var callerHttp = new HttpClient(callerHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var callerInstaller = new ModpackSourceInstaller(new HttpPackageTransport(callerHttp), TimeSpan.FromMinutes(1));
        using var callerCancellation = new CancellationTokenSource();
        var callerTask = callerInstaller.PrepareAsync([GitHub([], size: 1)], Context(fixture), callerCancellation.Token);

        await callerRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerTask);
    }

    [Fact]
    public async Task Github_rejects_a_hosted_asset_whose_asset_name_differs_from_the_pinned_pick()
    {
        using var fixture = new ExecutorFixture();
        var bytes = Zip("plugins/Example.dll", "x");
        var item = GitHub(bytes, Hash(bytes), url: "https://github.com/example/mod/releases/download/v1.2.3/other.zip");
        var installer = new ModpackSourceInstaller(new FakePackageTransport(bytes));

        var error = await Assert.ThrowsAsync<PhaketException>(() =>
            installer.PrepareAsync([item], Context(fixture), TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ModSourceHostNotAllowed, error.Error.Code);
    }

    [Fact]
    public async Task Github_rejects_disallowed_initial_and_redirect_hosts()
    {
        using var fixture = new ExecutorFixture();
        var bytes = Zip("plugins/Example.dll", "x");
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://attacker.example/payload.zip") }
        }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var installer = new ModpackSourceInstaller(new HttpPackageTransport(http));

        var initial = await Assert.ThrowsAsync<PhaketException>(() =>
            installer.PrepareAsync([GitHub(bytes, Hash(bytes), url: "https://attacker.example/mod.zip")], Context(fixture), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ModSourceHostNotAllowed, initial.Error.Code);
        Assert.Empty(handler.Requests);

        var redirect = await Assert.ThrowsAsync<PhaketException>(() =>
            installer.PrepareAsync([GitHub(bytes, Hash(bytes))], Context(fixture), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ModSourceHostNotAllowed, redirect.Error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Nexus_zip_slip_is_rejected_by_the_existing_archive_guard()
    {
        using var fixture = new ExecutorFixture();
        var archive = Path.Combine(fixture.Root, "nexus.zip");
        await File.WriteAllBytesAsync(archive, Zip("../escape.txt", "must not escape"), TestContext.Current.CancellationToken);
        var installer = new ModpackSourceInstaller(new FakePackageTransport([]));

        var error = await Assert.ThrowsAsync<PhaketException>(() =>
            installer.PrepareAsync([Nexus(archive)], Context(fixture), TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ArchiveUnsafe, error.Error.Code);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.txt")));
    }

    [Fact]
    public async Task Failure_in_second_package_rolls_back_first_package()
    {
        using var fixture = new ExecutorFixture();
        var first = Path.Combine(fixture.Root, "first.zip");
        var second = Path.Combine(fixture.Root, "second.zip");
        await File.WriteAllBytesAsync(first, Zip("plugins/Shared.dll", "first"), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(second, Zip("plugins/Shared.dll", "different second"), TestContext.Current.CancellationToken);
        var installer = new ModpackSourceInstaller(new FakePackageTransport([]));
        var plan = await installer.PrepareAsync(
            [Nexus(first, "Shared Mod", "1.0"), Nexus(second, "Shared Mod", "2.0")], Context(fixture), TestContext.Current.CancellationToken);

        var result = installer.Execute(plan, true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Shared_Mod", "Shared.dll")));
    }

    [Fact]
    public async Task Missing_github_hash_is_a_localized_plan_warning_and_asset_stays_pinned()
    {
        using var fixture = new ExecutorFixture();
        var bytes = Zip("plugins/Example.dll", "unverified but pinned");
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var installer = new ModpackSourceInstaller(new HttpPackageTransport(http));

        var plan = await installer.PrepareAsync([GitHub(bytes)], Context(fixture), TestContext.Current.CancellationToken);

        Assert.Contains("GITHUB_HASH_UNVERIFIED", plan.Warnings);
        Assert.Equal(Hash(bytes), Assert.Single(plan.Items).Plan.Operations.First().Sha256);
        Assert.Equal("v1.2.3", plan.Items[0].Version);
        Assert.Single(handler.Requests); // no latest-release lookup or auto-update
        Assert.Contains("SHA-256", ErrorMessages.Format("GITHUB_HASH_UNVERIFIED", "en"));
        Assert.Contains("SHA-256", ErrorMessages.Format("GITHUB_HASH_UNVERIFIED", "pl-PL"));
    }

    [Fact]
    public async Task Nexus_7z_and_rar_report_an_explicit_unsupported_format()
    {
        using var fixture = new ExecutorFixture();
        var installer = new ModpackSourceInstaller(new FakePackageTransport([]));
        foreach (var extension in new[] { ".7z", ".rar" })
        {
            var archive = Path.Combine(fixture.Root, "manual" + extension);
            await File.WriteAllTextAsync(archive, "not an archive", TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<PhaketException>(() =>
                installer.PrepareAsync([Nexus(archive)], Context(fixture), TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCodes.ArchiveFormatUnsupported, error.Error.Code);
        }
    }

    [Fact]
    public async Task Plan_must_be_accepted_before_any_install_write()
    {
        using var fixture = new ExecutorFixture();
        var archive = Path.Combine(fixture.Root, "nexus.zip");
        await File.WriteAllBytesAsync(archive, Zip("plugins/Mod.dll", "bytes"), TestContext.Current.CancellationToken);
        var installer = new ModpackSourceInstaller(new FakePackageTransport([]));
        var plan = await installer.PrepareAsync([Nexus(archive)], Context(fixture), TestContext.Current.CancellationToken);

        var error = Assert.Throws<PhaketException>(() => installer.Execute(plan, false, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.PlanNotAccepted, error.Error.Code);
        Assert.False(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Manual_Mod", "Mod.dll")));
    }

    private sealed class FakePackageTransport(byte[] bytes) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
