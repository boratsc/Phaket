using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution.SaveBackup;

// One copied file. Path is relative to the save location it came from (posix
// separators), so the same manifest restores on Windows and Linux. LocationIndex
// points at the entry in Locations (two locations may hold the same relative path);
// a manifest written before it existed restores as location 0.
public sealed record SaveBackupFile(string RelativePath, long Size, DateTimeOffset Mtime, string Sha256, int LocationIndex = 0);

// A save location as it looked when the backup was taken: whether it existed and
// which files were inside. A location that did not exist is recorded too, so a
// restore removes what the recipe created there.
public sealed record SaveBackupLocation(string Location, bool Existed, IReadOnlyList<string> Files);

// Versioned, canonically serialized backup manifest (PAK-141, Heroic-shaped).
// StateHash is computed over the content WITHOUT SchemaVersion and StateHash so a
// manifest can be re-signed after a restore without recomputing every file.
public sealed record SaveBackupManifest(
    string SchemaVersion,
    string Id,
    DateTimeOffset CreatedAt,
    string GameId,
    string InstallationId,
    IReadOnlyList<SaveBackupLocation> Locations,
    IReadOnlyList<SaveBackupFile> Files,
    long TotalBytes,
    string? StateHash)
{
    public const string CurrentSchemaVersion = "save-backup/v1";

    public string ComputeStateHash() => ComputeStateHash(
        Id, CreatedAt, GameId, InstallationId, Locations, Files, TotalBytes);

    public static string ComputeStateHash(string id, DateTimeOffset createdAt, string gameId, string installationId,
        IReadOnlyList<SaveBackupLocation> locations, IReadOnlyList<SaveBackupFile> files, long totalBytes)
    {
        var canonicalLocations = locations.OrderBy(l => l.Location, StringComparer.Ordinal).ToArray();
        // A backup of one location hashes exactly as it did before LocationIndex existed, so an
        // older manifest still verifies; the field only appears once two locations share a path.
        if (files.All(f => f.LocationIndex == 0))
            return CanonicalJson.Hash(new
            {
                id, createdAt, gameId, installationId, locations = canonicalLocations, totalBytes,
                files = files.OrderBy(f => f.RelativePath, StringComparer.Ordinal)
                    .Select(f => new { f.RelativePath, f.Size, f.Mtime, f.Sha256 }).ToArray(),
            });
        var canonicalFiles = files.OrderBy(f => f.LocationIndex).ThenBy(f => f.RelativePath, StringComparer.Ordinal).ToArray();
        return CanonicalJson.Hash(new { id, createdAt, gameId, installationId, locations = canonicalLocations, files = canonicalFiles, totalBytes });
    }

    public static byte[] Serialize(SaveBackupManifest manifest) => CanonicalJson.Serialize(manifest);

    public static SaveBackupManifest Parse(string json)
    {
        SaveBackupManifest? manifest;
        try { manifest = System.Text.Json.JsonSerializer.Deserialize<SaveBackupManifest>(json, Options); }
        catch (System.Text.Json.JsonException) { throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid); }
        if (manifest is null || manifest.Locations is null || manifest.Files is null
            || manifest.StateHash is null || manifest.CreatedAt == default)
            throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid);
        return manifest;
    }

    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly System.Text.Json.JsonSerializerOptions ReadableOptions = new(Options) { WriteIndented = true };

    // Readable JSON for the user; the state hash is taken over the canonical form, not this text.
    internal static string Format(SaveBackupManifest manifest) =>
        System.Text.Json.JsonSerializer.Serialize(manifest, ReadableOptions);

    // Unreadable JSON or a missing file is a corrupt backup, not an empty one.
    internal static SaveBackupManifest Read(string path)
    {
        string text;
        try { text = System.IO.File.ReadAllText(path); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        { throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid); }
        return Parse(text);
    }
}