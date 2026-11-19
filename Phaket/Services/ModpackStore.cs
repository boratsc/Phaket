using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phaket.Services;

public sealed class ModpackMod
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "thunderstore";
    public bool Required { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> Dependencies { get; set; } = [];
    public string? ModId { get; set; }
    public string? Repository { get; set; }
    public string? Tag { get; set; }
    public string? AssetId { get; set; }
    public string? Sha256 { get; set; }
    public string? Link { get; set; }
    public string? ManualPath { get; set; }
}

public sealed class ModpackDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string GameId { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Slug { get; set; }
    public bool IsPublic { get; set; }
    public int? PublicVersion { get; set; }
    public List<ModpackMod> Items { get; set; } = [];
}

// Local modpacks are user data, kept outside the game directory and replaced atomically.
public sealed class ModpackStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string path;
    private readonly object gate = new();
    private List<ModpackDefinition> packs = [];

    public ModpackStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket")) { }

    internal ModpackStore(string appDataRoot)
    {
        Directory.CreateDirectory(appDataRoot);
        path = Path.Combine(appDataRoot, "modpacks.json");
        if (!File.Exists(path)) return;
        try
        {
            packs = JsonSerializer.Deserialize<List<ModpackDefinition>>(File.ReadAllText(path), JsonOptions) ?? [];
            foreach (var pack in packs)
            {
                pack.Items ??= [];
                Validate(pack);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Preserve unreadable user data; do not overwrite it with an empty catalog.
            IsReadOnly = true;
        }
    }

    public bool IsReadOnly { get; }

    public IReadOnlyList<ModpackDefinition> ForGame(string gameId)
    {
        lock (gate) return packs.Where(p => string.Equals(p.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .Select(Clone).ToArray();
    }

    public void Save(ModpackDefinition pack)
    {
        Validate(pack);
        lock (gate)
        {
            if (IsReadOnly) throw new IOException("The local modpack catalog is unreadable and was left untouched.");
            var next = packs.Select(Clone).ToList();
            var index = next.FindIndex(p => string.Equals(p.Id, pack.Id, StringComparison.Ordinal));
            if (index < 0) next.Add(Clone(pack)); else next[index] = Clone(pack);
            Write(next);
            packs = next;
        }
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            if (IsReadOnly) throw new IOException("The local modpack catalog is unreadable and was left untouched.");
            var next = packs.Where(p => !string.Equals(p.Id, id, StringComparison.Ordinal)).Select(Clone).ToList();
            Write(next);
            packs = next;
        }
    }

    private void Write(List<ModpackDefinition> value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static ModpackDefinition Clone(ModpackDefinition value) => new()
    {
        Id = value.Id, Name = value.Name, Author = value.Author, Version = value.Version,
        GameId = value.GameId, Description = value.Description, Slug = value.Slug,
        IsPublic = value.IsPublic, PublicVersion = value.PublicVersion,
        Items = value.Items.Select(m => new ModpackMod
        {
            Name = m.Name, Version = m.Version, Source = m.Source, Required = m.Required,
            Enabled = m.Enabled, Dependencies = [.. m.Dependencies], ModId = m.ModId,
            Repository = m.Repository, Tag = m.Tag, AssetId = m.AssetId, Sha256 = m.Sha256, Link = m.Link, ManualPath = m.ManualPath
        }).ToList()
    };

    private static void Validate(ModpackDefinition pack)
    {
        static bool Text(string? value, int max, bool allowEmpty = false) => value is not null && value.Length <= max
            && (allowEmpty || !string.IsNullOrWhiteSpace(value)) && !value.Any(char.IsControl);
        if (!Text(pack.Id, 100) || !Text(pack.Name, 100) || !Text(pack.Author, 100)
            || !Text(pack.Version, 100) || !Text(pack.GameId, 80) || !Text(pack.Description, 4000, true)
            || pack.Items is null || pack.Items.Count > 500
            || pack.Items.Any(m => m is null || !Text(m.Name, 160) || !Text(m.Version, 100)
                || m.Source is not ("thunderstore" or "github" or "nexus") || m.Dependencies is null || m.Dependencies.Count > 100
                || m.Sha256 is not null && (m.Sha256.Length != 64 || m.Sha256.Any(c => !Uri.IsHexDigit(c))))
            || pack.Items.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != pack.Items.Count)
            throw new ArgumentException("The modpack data is invalid.", nameof(pack));
    }
}
