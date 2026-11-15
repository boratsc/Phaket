namespace Phaket.Core.Accounts;

public static class SecretStoreFactory
{
    private static int testHost;

    public static void DisableNativeAccessForTests() => Interlocked.Exchange(ref testHost, 1);

    internal static bool NativeAccessBlocked => Volatile.Read(ref testHost) != 0 &&
        Environment.GetEnvironmentVariable("PHAKET_KEYRING_PROBE") != "1";

    internal static void CheckNativeAccess()
    {
        if (NativeAccessBlocked)
            throw new SecretStoreException("SECRET_STORE_TEST_GUARD", "Natywny magazyn zablokowany w testach / Native secret store blocked in tests.");
    }

    public static ISecretStore Create(bool useFileStore = false, string? password = null, string? directory = null)
    {
        if (useFileStore)
        {
            if (string.IsNullOrEmpty(password))
                throw new SecretStoreException("SECRET_STORE_CONSENT_REQUIRED", "Wymagana zgoda i hasło / Consent and password are required.");
            return new ConsentFileStore(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket"), true, password);
        }
        if (password is not null || directory is not null)
            throw new SecretStoreException("SECRET_STORE_CONSENT_REQUIRED", "Wymagana zgoda i hasło / Consent and password are required.");
        if (NativeAccessBlocked) return new TestMemorySecretStore();
        if (OperatingSystem.IsWindows())
            return new WindowsDpapiStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket"));
        if (OperatingSystem.IsLinux()) return new LinuxLibsecretStore();
        throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "Brak magazynu dla tego systemu / No secret store for this platform.");
    }
}
