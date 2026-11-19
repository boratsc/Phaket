namespace Phaket.Services;

public interface ISignedRequestSigner
{
    string? DeviceKey { get; }

    Task<IReadOnlyDictionary<string, string>> SignAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default);
}
