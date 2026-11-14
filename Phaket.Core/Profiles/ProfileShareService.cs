using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phaket.Core.Profiles;

// Local profile sharing (spec §8.5): export a profile to a portable package
// (manifest + lockfile-style pinned versions), import with a diff preview.
// Sharing is metadata only — no mod files, no internal mod settings. Import
// never installs anything automatically; it creates a draft profile.
public sealed record SharedMod(string Name, string Version, string? Sha256 = null);

public sealed class ProfileSharePackage
{
    public string SchemaVersion { get; set; } = "profile-share/v1";
    public string ProfileName { get; set; } = "";
    public string GameId { get; set; } = "";
    public List<SharedMod> Mods { get; set; } = [];
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record ProfileShareDiff(
    IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed);

public static class ProfileShareService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Export(string gameId, string profileName, IEnumerable<(string Name, string Version)> mods)
    {
        var package = new ProfileSharePackage
        {
            GameId = gameId,
            ProfileName = profileName,
            Mods = mods.Select(m => new SharedMod(m.Name, m.Version)).ToList(),
        };
        return JsonSerializer.Serialize(package, Options);
    }

    public static ProfileSharePackage Parse(string json)
    {
        var package = JsonSerializer.Deserialize<ProfileSharePackage>(json, Options)
            ?? throw new InvalidDataException("empty profile package");
        if (package.SchemaVersion != "profile-share/v1")
            throw new InvalidDataException($"unsupported schema: {package.SchemaVersion}");
        return package;
    }

    public static ProfileShareDiff Diff(IEnumerable<(string Name, string Version)>? current, ProfileSharePackage incoming)
    {
        var cur = (current ?? []).ToDictionary(m => m.Name, m => m.Version, StringComparer.Ordinal);
        var inc = incoming.Mods.ToDictionary(m => m.Name, m => m.Version, StringComparer.Ordinal);
        var added = inc.Keys.Except(cur.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var removed = cur.Keys.Except(inc.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var changed = cur.Keys.Intersect(inc.Keys, StringComparer.Ordinal)
            .Where(k => cur[k] != inc[k]).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return new ProfileShareDiff(added, removed, changed);
    }
}
