using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Phaket.Services;

namespace Phaket.Tests;

// No test may hand a steam:// URI to the user's desktop shell. xUnit disables parallel
// collections here, so spies can replace and restore this default without a registry race.
internal static class SteamLaunchGuard
{
    internal static readonly ConcurrentQueue<string> RequestedUris = new();

    [ModuleInitializer]
    internal static void BlockSystemSteamLaunches() =>
        LaunchService.SteamLauncher = uri => { RequestedUris.Enqueue(uri); return true; };
}
