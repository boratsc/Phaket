using System.Text.Json;

namespace Phaket.Core.Nexus;

// Small on-disk TTL cache for Nexus metadata. A free API key allows ~100 requests
// per hour, so a mod that was just looked at is never fetched again inside the TTL
// window; the UI falls back to the last (expired) copy when the hourly limit is hit.
internal sealed record NexusCacheEntry(long ExpiresAt, string Json);

public sealed class NexusCache(string? path = null, TimeProvider? timeProvider = null, TimeSpan? ttl = null)
{
    private readonly Dictionary<string, NexusCacheEntry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private bool loaded;

    public TimeSpan Ttl { get; set; } = ttl ?? TimeSpan.FromMinutes(10);

    public bool TryGet<T>(string key, out T? value, bool allowExpired = false) where T : class
    {
        value = null;
        EnsureLoaded();
        if (!entries.TryGetValue(key, out var entry)) return false;
        if (!allowExpired && entry.ExpiresAt <= Now()) return false;
        try { value = JsonSerializer.Deserialize<T>(entry.Json); }
        catch (JsonException) { return false; }
        return value is not null;
    }

    public void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class
    {
        EnsureLoaded();
        entries[key] = new NexusCacheEntry(Now() + (long)(ttl ?? Ttl).TotalMilliseconds, JsonSerializer.Serialize(value));
        Save();
    }

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, NexusCacheEntry>>(File.ReadAllText(path));
            if (stored is null) return;
            foreach ((string key, NexusCacheEntry entry) in stored) entries[key] = entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* a broken cache is just an empty cache */ }
    }

    private void Save()
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cache writes never block the feature */ }
    }
}