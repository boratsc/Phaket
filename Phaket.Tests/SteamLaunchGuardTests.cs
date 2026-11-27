using System.Diagnostics;
using Phaket.Services;

namespace Phaket.Tests;

public sealed class SteamLaunchGuardTests
{
    [Fact]
    public void LaunchGameWithoutALauncherIsBlockedBeforeProcessStart()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("PHAKET_TEST_MODE"));
        Assert.NotNull(LaunchService.SteamLauncher);
        var previousLauncher = LaunchService.SteamLauncher;
        var previousStarter = LaunchService.SteamProcessStarter;
        var processStarts = 0;
        try
        {
            LaunchService.SteamLauncher = null;
            LaunchService.SteamProcessStarter = _ => { processStarts++; return null; };

            var error = Assert.Throws<InvalidOperationException>(() => new LaunchService().LaunchGame("945360"));

            Assert.Equal("steam:// blocked in tests", error.Message);
            Assert.Equal(0, processStarts);
        }
        finally
        {
            LaunchService.SteamLauncher = previousLauncher;
            LaunchService.SteamProcessStarter = previousStarter;
        }
    }
}
