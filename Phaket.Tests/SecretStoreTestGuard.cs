using System.Runtime.CompilerServices;
using Phaket.Core.Accounts;

internal static class SecretStoreTestGuard
{
    [ModuleInitializer]
    internal static void Initialize() => SecretStoreFactory.DisableNativeAccessForTests();
}
