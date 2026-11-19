using System.Net.Http.Json;
using System.Runtime.InteropServices;

namespace Phaket.Core.Mods;

public sealed record ModReceipt(string GameId, string ModFullName, string ModVersion, string Os,
    string? Store, string Result, string? ErrorCode, string AppVersion, DateTimeOffset OccurredAt);

public interface IReceiptTransport
{
    Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken);
}

public sealed class HttpReceiptTransport(HttpClient http) : IReceiptTransport
{
    public async Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken) =>
        _ = await http.PostAsJsonAsync(path, receipt, cancellationToken);

    public static HttpReceiptTransport FromConfiguration(IReadOnlyDictionary<string, string?> configuration) =>
        new(new HttpClient { BaseAddress = new Uri(configuration["Phaket:ApiBaseUrl"]!) });
}

public sealed class ReceiptSender(IReceiptTransport transport)
{
    public async Task SendAsync(string gameId, string modFullName, string modVersion, string? store,
        string result, string? errorCode = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var receipt = new ModReceipt(gameId, modFullName, modVersion,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux",
                store is "steam" or "epic" ? store : null, result, result == "failed" ? errorCode : null,
                "0.1.0", DateTimeOffset.UtcNow);
            await transport.PostAsync("/receipts", receipt, cancellationToken);
        }
        catch (Exception) { }
    }
}
