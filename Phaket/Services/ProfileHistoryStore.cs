using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phaket.Services;

public sealed class ProfileHistoryStore
{
    internal const int MaxEntries = 100;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string path;
    private readonly Dictionary<string, List<ProfileHistoryEntry>> entries;
    private readonly bool loadFailed;

    public ProfileHistoryStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket")) { }

    internal ProfileHistoryStore(string root)
    {
        Directory.CreateDirectory(root);
        path = Path.Combine(root, "profile-history.json");
        try
        {
            entries = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<ProfileHistoryEntry>>>(File.ReadAllText(path), Options) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            entries = new(StringComparer.Ordinal);
            loadFailed = true;
        }
    }

    public IReadOnlyList<ProfileHistoryEntry> For(string gameId, string profileId)
    {
        ValidateKey(gameId, profileId);
        return entries.TryGetValue(Key(gameId, profileId), out var history) ? history.ToArray() : [];
    }

    public ProfileHistoryEntry Record(string gameId, GameProfile profile)
    {
        if (loadFailed) throw new InvalidDataException("Profile history could not be read and was left untouched.");
        ArgumentNullException.ThrowIfNull(profile);
        ValidateKey(gameId, profile.Id);

        if (profile.Name.Length > 80 || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Any(char.IsControl) ||
            profile.Mods.Count > 500 || profile.Mods.Any(mod => string.IsNullOrWhiteSpace(mod.Name) || mod.Name.Length > 160 ||
                string.IsNullOrWhiteSpace(mod.Version) || mod.Version.Length > 100 || mod.Name.Any(char.IsControl) || mod.Version.Any(char.IsControl)))
            throw new InvalidDataException("Invalid profile snapshot.");

        var key = Key(gameId, profile.Id);
        if (!entries.TryGetValue(key, out var history))
            entries[key] = history = [];
        if (history.Count > 0 && SameSnapshot(history[^1].Snapshot, profile)) return history[^1];
        var description = history.Count == 0 ? "Profiles.History.Created"
            : history[^1].Snapshot.Name != profile.Name ? "Profiles.History.Renamed" : "Profiles.History.Changed";

        var entry = new ProfileHistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            Description = description,
            Snapshot = Clone(profile),
        };
        history.Add(entry);
        // A profile that is opened on every start would otherwise grow this file forever:
        // the oldest snapshots fall off once the profile keeps MaxEntries of them.
        List<ProfileHistoryEntry>? dropped = null;
        if (history.Count > MaxEntries)
        {
            dropped = history.GetRange(0, history.Count - MaxEntries);
            history.RemoveRange(0, dropped.Count);
        }
        try { Save(); }
        catch
        {
            if (dropped is not null) history.InsertRange(0, dropped);
            history.RemoveAt(history.Count - 1);
            if (history.Count == 0) entries.Remove(key);
            throw;
        }
        return entry;
    }

    private void Save()
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string Key(string gameId, string profileId) => gameId + "\n" + profileId;

    private static void ValidateKey(string gameId, string profileId)
    {
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 80 || gameId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
            string.IsNullOrWhiteSpace(profileId) || profileId.Length > 128 || profileId.Any(char.IsControl))
            throw new ArgumentException("Invalid profile history key.");
    }

    private static GameProfile Clone(GameProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Mods = profile.Mods.Select(m => new ModEntry
        {
            Name = m.Name,
            Version = m.Version,
            Enabled = m.Enabled,
            Compatible = m.Compatible,
            Required = m.Required,
        }).ToList(),
    };

    private static bool SameSnapshot(GameProfile first, GameProfile second) =>
        first.Name == second.Name && first.Mods.Count == second.Mods.Count && first.Mods.Zip(second.Mods).All(pair =>
            pair.First.Name == pair.Second.Name && pair.First.Version == pair.Second.Version &&
            pair.First.Enabled == pair.Second.Enabled && pair.First.Compatible == pair.Second.Compatible && pair.First.Required == pair.Second.Required);
}

public sealed class ProfileHistoryEntry
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Description { get; set; } = "";
    public GameProfile Snapshot { get; set; } = new();
}
