namespace Phaket.Core.Accounts;

public sealed class SecretStoreException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
