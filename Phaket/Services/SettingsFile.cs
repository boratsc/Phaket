using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phaket.Services;

// settings.json has several independent writers (theme, device nick, language, catalog
// endpoint, tool pins). Each one is a read-modify-write, so they all go through this single
// gate: the file is re-read under the lock, merged in memory and swapped atomically through a
// temporary file. A malformed document is rejected instead of being replaced by a partial one.
internal static class SettingsFile
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // Strict variant: cancellation, I/O and corrupt-document failures reach the caller.
    internal static async Task UpdateAsync(string path, Action<JsonObject> mutate, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temp = null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            JsonObject node;
            if (File.Exists(fullPath))
            {
                try
                {
                    node = JsonNode.Parse(await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false))?.AsObject()
                        ?? throw new InvalidDataException("Settings must contain a JSON object.");
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    throw new InvalidDataException("Settings could not be updated safely.", ex);
                }
            }
            else node = [];

            mutate(node);
            temp = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temp, node.ToJsonString(Indented), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (temp is not null)
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Gate.Release();
        }
    }

    // Best-effort variant for UI saves that only report success or failure.
    internal static bool TryUpdate(string path, Action<JsonObject> mutate)
    {
        try
        {
            UpdateAsync(path, mutate).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or ArgumentException or InvalidDataException or OperationCanceledException)
        {
            return false;
        }
    }
}
