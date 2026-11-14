using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phaket.Services;

// Local PoC profile/mod/backup store (JSON in the user data dir). Sample profiles
// are seeded so the PoC demonstrates the flow; they are clearly PoC data, not
// real mod installations. No game files are touched here.
public sealed class ModEntry
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Compatible { get; set; } = true;
}

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<ModEntry> Mods { get; set; } = [];
}

public sealed class BackupEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public long SizeBytes { get; set; }
    public string Kind { get; set; } = "manual";
    public string Snapshot { get; set; } = "";
}

public sealed class GameProfileData
{
    public List<GameProfile> Profiles { get; set; } = [];
    public string? ActiveProfileId { get; set; }
}

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string root;
    private readonly string profilesPath;
    private readonly string backupsDir;
    private Dictionary<string, GameProfileData> data = new(StringComparer.Ordinal);

    public ProfileStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket"))
    {
    }

    internal ProfileStore(string root)
    {
        this.root = root;
        profilesPath = Path.Combine(root, "profiles.json");
        backupsDir = Path.Combine(root, "backups");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(backupsDir);
        Load();
    }

    private void Load()
    {
        if (File.Exists(profilesPath))
        {
            try { data = JsonSerializer.Deserialize<Dictionary<string, GameProfileData>>(File.ReadAllText(profilesPath)) ?? []; }
            catch
            {
                // Unreadable user file: keep it on disk untouched and never seed
                // sample data over it. The user can recover manually.
                data = new Dictionary<string, GameProfileData>(StringComparer.Ordinal);
                return;
            }
        }
        Seed("among-us", "Szyfrowy Mod – Stable",
            ("Szyfrowy Mod", "2.6.0"), ("BepInEx IL2CPP", "6.0.4"), ("Reactor", "2.5.1"),
            ("Custom Roles", "1.8.3"), ("LobbyControl", "1.4.0"));
        Seed("valheim", "Valheim – QoL Pack",
            ("BepInEx", "5.4.23"), ("PlantEverything", "1.18.0"), ("CraftFromContainers", "1.5.0"));
    }

    private void Seed(string gameId, string profileName, params (string Name, string Version)[] mods)
    {
        if (data.ContainsKey(gameId)) return;
        var profile = new GameProfile { Name = profileName };
        foreach (var (name, version) in mods)
            profile.Mods.Add(new ModEntry { Name = name, Version = version });
        data[gameId] = new GameProfileData { Profiles = [profile], ActiveProfileId = profile.Id };
        Save();
    }

    public GameProfileData For(string gameId) =>
        data.TryGetValue(gameId, out var d) ? d : new GameProfileData();

    public GameProfile? Active(string gameId)
    {
        var d = For(gameId);
        return d.Profiles.FirstOrDefault(p => p.Id == d.ActiveProfileId) ?? d.Profiles.FirstOrDefault();
    }

    public void SetActive(string gameId, string profileId)
    {
        var d = For(gameId);
        d.ActiveProfileId = profileId;
        data[gameId] = d;
        Save();
    }

    public GameProfile AddProfile(string gameId, string name)
    {
        var d = For(gameId);
        var profile = new GameProfile { Name = name };
        d.Profiles.Add(profile);
        d.ActiveProfileId ??= profile.Id;
        data[gameId] = d;
        Save();
        return profile;
    }

    // Adds a profile with its mods in a single atomic save (import path), so a
    // failure cannot leave an empty draft.
    public GameProfile AddProfile(string gameId, string name, IEnumerable<ModEntry> mods)
    {
        var d = For(gameId);
        var profile = new GameProfile { Name = name, Mods = mods.ToList() };
        d.Profiles.Add(profile);
        d.ActiveProfileId ??= profile.Id;
        data[gameId] = d;
        Save();
        return profile;
    }

    public void Save()
    {
        var temp = profilesPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
        File.Move(temp, profilesPath, overwrite: true);
    }

    public IReadOnlyList<BackupEntry> Backups(string gameId)
    {
        var dir = Path.Combine(backupsDir, gameId);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.json").OrderDescending(StringComparer.Ordinal)
            .Select(path =>
            {
                try { return JsonSerializer.Deserialize<BackupEntry>(File.ReadAllText(path)); }
                catch { return null; }
            })
            .Where(b => b is not null)
            .Cast<BackupEntry>()
            .ToArray();
    }

    public BackupEntry CreateBackup(string gameId, string name, string kind)
    {
        var d = For(gameId);
        var snapshot = JsonSerializer.Serialize(d, Options);
        var entry = new BackupEntry
        {
            Name = name,
            Kind = kind,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(snapshot),
            Snapshot = snapshot,
        };
        var dir = Path.Combine(backupsDir, gameId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, entry.Id + ".json"), JsonSerializer.Serialize(entry, Options));
        return entry;
    }

    public void Restore(string gameId, BackupEntry backup)
    {
        var restored = JsonSerializer.Deserialize<GameProfileData>(backup.Snapshot);
        if (restored is not null)
        {
            data[gameId] = restored;
            Save();
        }
    }
}
