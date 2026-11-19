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
    public bool Required { get; set; }
}

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<ModEntry> Mods { get; set; } = [];
    // Server slug of the first publication; older profiles.json files simply have no key here (null).
    public string? Slug { get; set; }
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
    private bool loadFailed;
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
                loadFailed = true;
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

    internal bool IsLoadFailed => loadFailed;

    public GameProfileData For(string gameId) =>
        data.TryGetValue(gameId, out var d) ? d : new GameProfileData();

    public GameProfile? Active(string gameId)
    {
        var d = For(gameId);
        return d.Profiles.FirstOrDefault(p => p.Id == d.ActiveProfileId) ?? d.Profiles.FirstOrDefault();
    }

    public void SetActive(string gameId, string profileId)
    {
        if (loadFailed) throw new InvalidDataException("The profile file is unreadable and was left untouched.");
        var d = For(gameId);
        if (!d.Profiles.Any(profile => profile.Id == profileId)) throw new InvalidOperationException("Profile not found.");
        d.ActiveProfileId = profileId;
        data[gameId] = d;
        Save();
    }

    public GameProfile AddCleanProfile(string gameId, string name)
    {
        if (loadFailed) throw new InvalidDataException("The profile file is unreadable and was left untouched.");
        var d = For(gameId);
        if (d.Profiles.Any(p => p.Id == "clean-game")) throw new InvalidOperationException("The clean profile already exists.");
        var profile = new GameProfile { Id = "clean-game", Name = name, Mods = [] };
        d.Profiles.Add(profile);
        d.ActiveProfileId ??= profile.Id;
        data[gameId] = d;
        Save();
        return profile;
    }

    public GameProfile AddProfile(string gameId, string name)
    {
        if (loadFailed) throw new InvalidDataException("The profile file is unreadable and was left untouched.");
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
        if (loadFailed) throw new InvalidDataException("The profile file is unreadable and was left untouched.");
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
        if (loadFailed) throw new InvalidDataException("The profile file is unreadable and was left untouched.");
        foreach (var profile in data.Values.SelectMany(game => game.Profiles)
                     .Where(profile => profile.Id == "clean-game" || profile.Name is "Czysta gra" or "Clean game"))
            profile.Mods.Clear();
        var temp = profilesPath + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
            File.Move(temp, profilesPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        var history = new ProfileHistoryStore(root);
        foreach (var (gameId, gameData) in data)
            foreach (var profile in gameData.Profiles)
                try { history.Record(gameId, profile); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("Profile history could not be persisted: {0}", ex); }
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
