using System.Security.Cryptography;
using Phaket.Core.Accounts;

namespace Phaket.Core.Tests;

public sealed class WindowsDpapiStoreTests
{
    public static bool IsNotWindows => !OperatingSystem.IsWindows();

    [Fact(SkipWhen = nameof(IsNotWindows), Skip = "DPAPI wymaga Windows / DPAPI requires Windows")]
    public async Task RoundTripAndDelete()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable("PHAKET_KEYRING_PROBE") != "1") Assert.Skip("Set PHAKET_KEYRING_PROBE=1 to run the native keyring probe.");
        string directory = Path.Combine(Path.GetTempPath(), "phaket-dpapi-test-" + Guid.NewGuid().ToString("N"));
        string key = "probe-" + Guid.NewGuid().ToString("N");
        try
        {
            var store = new WindowsDpapiStore(directory);
            byte[] data = [0, 1, 2, 255];
            await store.SetAsync(key, data, TestContext.Current.CancellationToken);
            Assert.Equal(data, await store.GetAsync(key, TestContext.Current.CancellationToken));
            await store.DeleteAsync(key, TestContext.Current.CancellationToken);
            Assert.Null(await store.GetAsync(key, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact(SkipWhen = nameof(IsNotWindows), Skip = "DPAPI wymaga Windows / DPAPI requires Windows")]
    public async Task DoesNotReadOrOverwriteLegacyBlob()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable("PHAKET_KEYRING_PROBE") != "1") Assert.Skip("Set PHAKET_KEYRING_PROBE=1 to run the native keyring probe.");
        string directory = Path.Combine(Path.GetTempPath(), "phaket-dpapi-test-" + Guid.NewGuid().ToString("N"));
        string key = "probe-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(directory);
            byte[] data = [0, 10, 255];
            await File.WriteAllTextAsync(Path.Combine(directory, key + ".bin"), Convert.ToBase64String(ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)), TestContext.Current.CancellationToken);
            var store = new WindowsDpapiStore(directory);
            Assert.Null(await store.GetAsync(key, TestContext.Current.CancellationToken));
            await store.SetAsync(key, data, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(Path.Combine(directory, key + ".bin")));
            Assert.True(File.Exists(Path.Combine(directory, "secrets", key + ".dpapi")));
            Assert.Equal(data, await store.GetAsync(key, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
