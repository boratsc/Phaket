using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Launch;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-146 C: the first instance starts; only a second instance of the same game can be
// blocked. Every process here is a fixture owned by the test, never a real game.
public sealed class Pak146ParallelLaunchTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-parallel-").FullName;
    private string Store => Path.Combine(root, "processes.json");

    private static (string Executable, string Arguments) Helper() =>
        OperatingSystem.IsWindows() ? ("ping", "-n 300 127.0.0.1") : ("sleep", "300");

    private static GameDefinition Definition(bool isolated = false, string slug = "among-us", string appId = "945360")
    {
        var mode = isolated
            ? new SavesIsolationMode(SavesIsolationMode.EnvRedirect, ["HOME", "XDG_CONFIG_HOME"])
            : new SavesIsolationMode(SavesIsolationMode.Shared, Reason: "fixture-shared-saves");
        return new GameDefinition(slug, ["BepInEx/**"],
            StoreIds: new Dictionary<string, string> { ["steam"] = appId },
            SavesIsolation: new SavesIsolation(new Dictionary<string, SavesIsolationMode>
            {
                ["windows"] = mode,
                ["linux"] = mode,
            }));
    }

    private static ParallelLaunchContext Context(GameDefinition definition) =>
        new(definition, "steam", OperatingSystem.IsWindows() ? "windows" : "linux");

    private static LaunchOutcome Launch(LaunchService service, ParallelLaunchContext context, bool savesShared)
    {
        var (executable, arguments) = Helper();
        return service.LaunchExecutable(executable, null, context.Definition.StoreIds!["steam"], "fixture",
            arguments, environment: null, savesShared, context);
    }

    private static ProcessRecord WaitForTracked(LaunchService service, string appId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (service.Tracked(appId) is { } record) return record;
            Assert.True(DateTime.UtcNow < deadline, $"no tracked fixture for {appId}");
            Thread.Sleep(10);
        }
    }

    private static void AssertGone(ProcessRecord record)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var (found, identity) = ProcessIdentity.TryGetProcessIdentity(record.Pid);
            if (!found || identity?.StartValue != record.StartTime) return;
            Assert.True(DateTime.UtcNow < deadline, $"fixture {record.Pid} is still alive");
            Thread.Sleep(10);
        }
    }

    private void StopFixtures()
    {
        var tracker = new ProcessTracker(Store);
        foreach (var record in tracker.Load())
        {
            var result = tracker.TryStop(record, force: true);
            Assert.True(result.Status is ProcessStopStatus.Stopped or ProcessStopStatus.NotRunning,
                $"fixture {record.Pid} was not stopped: {result}");
            AssertGone(record);
        }
    }

    [Fact]
    public void SecondSharedSavesInstanceIsBlockedUntilTheFirstOneCloses()
    {
        var service = new LaunchService(Store);
        var context = Context(Definition());
        try
        {
            var first = Launch(service, context, savesShared: true);
            Assert.True(first.Started, first.Failure);
            Assert.True(first.Tracked, first.Failure);
            var firstRecord = WaitForTracked(service, "945360");

            var second = Launch(service, context, savesShared: true);
            Assert.True(second.BlockedByParallelPolicy);
            Assert.False(second.Started);
            Assert.False(second.Tracked);
            Assert.Equal("shared-saves", second.ParallelLaunchReasonCode);
            Assert.Equal("Nie można uruchomić dwóch wersji tej gry naraz: dzielą zapisy i jedna zepsuje drugą. Zamknij drugą wersję i spróbuj ponownie.",
                second.ParallelLaunchReason!.MessagePl);
            Assert.Equal("You cannot run two versions of this game at the same time: they share saves and one will break the other. Close the other version and try again.",
                second.ParallelLaunchReason!.MessageEn);
            Assert.Single(new ProcessTracker(Store).Load());

            Assert.Equal(ProcessStopStatus.Stopped, service.Stop(firstRecord).Status);
            AssertGone(firstRecord);

            var third = Launch(service, context, savesShared: true);
            Assert.True(third.Started, third.Failure);
            Assert.True(third.Tracked, third.Failure);
        }
        finally { StopFixtures(); }
    }

    [Fact]
    public void SecondSteamProtocolInstanceIsBlockedBeforeSteamIsAsked()
    {
        var service = new LaunchService(Store);
        var context = Context(Definition());
        try
        {
            Assert.True(Launch(service, context, savesShared: true).Started);
            WaitForTracked(service, "945360");
            var callsBefore = SteamLaunchGuard.RequestedUris.Count;

            var second = service.LaunchShortcut("12345", "945360", "fixture", context);

            Assert.True(second.BlockedByParallelPolicy);
            Assert.Equal("steam-single-instance", second.ParallelLaunchReasonCode);
            Assert.Equal(callsBefore, SteamLaunchGuard.RequestedUris.Count);
            Assert.Single(new ProcessTracker(Store).Load());
        }
        finally { StopFixtures(); }
    }

    [Fact]
    public void UnknownDoesNotBecomeEitherSupportOrAHardBlock()
    {
        var service = new LaunchService(Store);
        var context = Context(Definition(isolated: true));
        try
        {
            var first = Launch(service, context, savesShared: false);
            var second = Launch(service, context, savesShared: false);

            Assert.True(first.Started, first.Failure);
            Assert.True(second.Started, second.Failure);
            Assert.False(first.BlockedByParallelPolicy);
            Assert.False(second.BlockedByParallelPolicy);
            Assert.Equal(2, new ProcessTracker(Store).Load().Count);
        }
        finally { StopFixtures(); }
    }

    [Fact]
    public void AnActiveDifferentGameDoesNotBlockTheLaunch()
    {
        var service = new LaunchService(Store);
        var amongUs = Context(Definition());
        var valheim = Context(Definition(slug: "valheim", appId: "892970"));
        try
        {
            Assert.True(Launch(service, amongUs, savesShared: true).Started);
            WaitForTracked(service, "945360");

            var otherGame = Launch(service, valheim, savesShared: true);

            Assert.True(otherGame.Started, otherGame.Failure);
            Assert.False(otherGame.BlockedByParallelPolicy);
        }
        finally { StopFixtures(); }
    }

    [AvaloniaFact]
    public void ViewModelShowsTheBlockedReasonInsteadOfGenericFailure()
    {
        var service = new LaunchService(Store);
        var definition = Definition();
        var context = Context(definition);
        var gameRoot = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        var item = new GameCatalogItem(definition, "", "");
        item.Detected = new SteamInstallation(
            new Installation("steam-945360", "among-us", gameRoot),
            new SteamAppManifest("945360", "Among Us", "1", "public", "1", "Among Us", 1),
            new SteamLibrary(Path.Combine(root, "steam"), false), GameAvailability.Moddable);
        var vm = new GameViewModel(item, () => { }, appDataRoot: Path.Combine(root, "app"))
        {
            Launch = service,
            HostOs = OperatingSystem.IsWindows() ? "windows" : "linux",
            SteamHome = Path.Combine(root, "home"),
            SteamRoots = [],
        };
        try
        {
            Assert.True(Launch(service, context, savesShared: true).Started);
            WaitForTracked(service, "945360");
            var callsBefore = SteamLaunchGuard.RequestedUris.Count;

            var play = vm.PlayCommand.ExecuteAsync(null);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!play.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.True(play.IsCompleted, "play did not finish");
            play.GetAwaiter().GetResult();

            Assert.True(vm.LastLaunch!.BlockedByParallelPolicy);
            Assert.Equal("steam-single-instance", vm.LastLaunch.ParallelLaunchReasonCode);
            Assert.Equal(vm.LastLaunch.ParallelLaunchReason!.Message(LocalizationService.Instance.CurrentLanguage), vm.StatusMessage);
            Assert.Equal(callsBefore, SteamLaunchGuard.RequestedUris.Count);
        }
        finally { StopFixtures(); }
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
