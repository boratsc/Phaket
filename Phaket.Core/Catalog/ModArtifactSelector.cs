namespace Phaket.Core.Catalog;

// Picks the catalog artifact that matches the installation facts. A SusModder
// version carries one artifact per architecture/store combination; Thunderstore
// versions have no artifacts array and fall back to the legacy downloadUrl.
public sealed record SelectedModArtifact(string Url, string? Sha256, long? SizeBytes, string? Arch, string? AssetName = null,
    // Store editions this exact artifact serves (PAK-95 follow-up: the recipe target must follow the
    // artifact, not a fixed arch table). Null/empty means the artifact does not declare them.
    IReadOnlyList<string>? Stores = null);

public static class ModArtifactSelector
{
    public static SelectedModArtifact Select(ThunderstoreVersion version, string modFullName, string? arch, string store) =>
        Select(version.Artifacts, version.DownloadUrl, version.Sha256, version.FileSize, version.Arch,
            modFullName, version.VersionNumber, arch, store);

    public static SelectedModArtifact Select(IReadOnlyList<ModArtifact>? artifacts, string downloadUrl,
        string? sha256, long? fileSize, string? declaredArch, string modFullName, string versionNumber,
        string? arch, string store)
    {
        artifacts ??= [];
        if (artifacts.Count == 0)
        {
            if (declaredArch is { } declared && arch is not null && !string.Equals(declared, arch, StringComparison.Ordinal))
                throw Refuse(versionNumber, modFullName, arch, store);
            if (!IsHttps(downloadUrl))
                throw Refuse(versionNumber, modFullName, arch, store);
            return new(downloadUrl, sha256, fileSize is > 0 ? fileSize : null, declaredArch ?? arch);
        }

        var candidates = artifacts
            .Where(artifact => artifact.Stores is not { Count: > 0 }
                || artifact.Stores.Contains(store, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        // An unknown installation arch can only use an artifact that is not arch-specific.
        var chosen = arch is null
            ? candidates.FirstOrDefault(artifact => artifact.Arch is null)
            : candidates.FirstOrDefault(artifact => artifact.Arch == arch)
                ?? candidates.FirstOrDefault(artifact => artifact.Arch is null);
        if (chosen is null) throw Refuse(versionNumber, modFullName, arch, store);
        // A catalog row can point at a placeholder artifact; never hand the downloader a
        // URL it cannot fetch (empty or plain http).
        if (!IsHttps(chosen.Url)) throw Refuse(versionNumber, modFullName, arch, store);
        return new(chosen.Url, chosen.Sha256, chosen.SizeBytes, chosen.Arch ?? arch, chosen.AssetName,
            chosen.Stores is { Count: > 0 } ? chosen.Stores : null);
    }

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static Omrl.PhaketException Refuse(string versionNumber, string modFullName, string? arch, string store) =>
        new(Omrl.ErrorCodes.ModArtifactUnavailable,
            ("artifact", $"{modFullName} {versionNumber}"),
            ("arch", arch ?? "unknown"), ("store", store));
}