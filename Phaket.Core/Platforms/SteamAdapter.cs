using System.Security.Cryptography;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

// Separated game states (spec §7.1): in library != installed != moddable != supported.
public enum GameAvailability { InLibrary, Installed, Moddable, VersionSupported }

public sealed record SteamAppManifest(
    string AppId, string Name, string BuildId, string Branch,
    string ManifestId, string InstallDir, long SizeOnDisk);

public sealed record SteamLibrary(string Path, bool IsFlatpak);

public sealed record SteamInstallation(
    Installation Installation, SteamAppManifest Manifest, SteamLibrary Library, GameAvailability Availability);

// Local Steam detection: native + Flatpak + extra libraries, read-only, no
// store credentials. Real installations are only read with user consent;
// CI uses fixture trees exclusively.
public sealed class SteamAdapter
{
    private readonly IReadOnlyList<GameDefinition> definitions;

    public SteamAdapter(IReadOnlyList<GameDefinition> definitions) => this.definitions = definitions;

    public static IReadOnlyList<string> DefaultSteamRoots(string os, string home)
    {
        if (os == "windows")
            return [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")];
        return
        [
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
        ];
    }

    public static IReadOnlyList<SteamLibrary> ParseLibraries(string steamRoot, bool isFlatpak)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var libraries = new List<SteamLibrary>();
        void Add(string path, bool flatpak)
        {
            var full = Path.GetFullPath(path);
            if (seen.Add(full)) libraries.Add(new SteamLibrary(full, flatpak));
        }
        Add(steamRoot, isFlatpak);
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) return libraries;
        var root = VdfParser.Parse(File.ReadAllText(vdfPath));
        var folders = root.Child("libraryfolders") ?? root;
        foreach (var (_, entry) in folders.Children)
        {
            var path = entry.Get("path");
            if (path is not null && Directory.Exists(path))
                Add(path, isFlatpak);
        }
        return libraries;
    }

    public static SteamAppManifest? ParseAppManifest(string acfPath)
    {
        var root = VdfParser.Parse(File.ReadAllText(acfPath));
        var state = root.Child("AppState");
        if (state is null) return null;
        var appId = state.Get("appid");
        if (appId is null) return null;
        return new SteamAppManifest(
            appId,
            state.Get("name") ?? "",
            state.Get("buildid") ?? "",
            state.Get("branch") ?? "public",
            state.Get("manifest") ?? "",
            state.Get("installdir") ?? "",
            long.TryParse(state.Get("SizeOnDisk"), out var size) ? size : 0);
    }

    public IReadOnlyList<SteamInstallation> DetectInstallations(string os, string home)
    {
        var results = new List<SteamInstallation>();
        foreach (var root in DefaultSteamRoots(os, home))
        {
            if (!Directory.Exists(root)) continue;
            var isFlatpak = root.Contains(".var/app/com.valvesoftware.Steam", StringComparison.Ordinal);
            foreach (var library in ParseLibraries(root, isFlatpak))
            {
                var steamapps = Path.Combine(library.Path, "steamapps");
                if (!Directory.Exists(steamapps)) continue;
                foreach (var acf in Directory.GetFiles(steamapps, "appmanifest_*.acf").Order(StringComparer.Ordinal))
                {
                    var manifest = ParseAppManifest(acf);
                    if (manifest is null) continue;
                    var installDir = Path.Combine(steamapps, "common", manifest.InstallDir);
                    var definition = definitions.FirstOrDefault(d =>
                        d.StoreIds?.TryGetValue("steam", out var appId) == true && appId == manifest.AppId);
                    var editionId = $"{definition?.Slug ?? manifest.AppId}-steam";
                    var installation = new Installation(
                        $"steam-{manifest.AppId}-{library.Path.GetHashCode():x}",
                        editionId, installDir);
                    var availability = Directory.Exists(installDir)
                        ? (definition is not null ? GameAvailability.Moddable : GameAvailability.Installed)
                        : GameAvailability.InLibrary;
                    results.Add(new SteamInstallation(installation, manifest, library, availability));
                }
            }
        }
        return results;
    }

    // Manual folder fallback (spec §7.1): no store account, unknown version.
    public SteamInstallation? FromManualFolder(string folder, string appId)
    {
        if (!Directory.Exists(folder)) return null;
        var definition = definitions.FirstOrDefault(d =>
            d.StoreIds?.TryGetValue("steam", out var id) == true && id == appId);
        var manifest = new SteamAppManifest(appId, definition?.Title?.GetValueOrDefault("en") ?? appId,
            "", "unknown", "", Path.GetFileName(folder), 0);
        var installation = new Installation($"manual-{appId}", $"{definition?.Slug ?? appId}-steam", folder);
        return new SteamInstallation(installation, manifest, new SteamLibrary(folder, false), GameAvailability.Moddable);
    }

    // probeVersion: facts from the manifest, never guessed; optional file fingerprint.
    public IReadOnlyList<string> ProbeVersion(SteamInstallation installation, bool fingerprint = false)
    {
        var versions = new List<string>();
        if (installation.Manifest.BuildId.Length > 0)
            versions.Add(installation.Manifest.BuildId);
        if (fingerprint && Directory.Exists(installation.Installation.RootPath))
        {
            var exe = Directory.GetFiles(installation.Installation.RootPath, "*.exe").Order(StringComparer.Ordinal).FirstOrDefault();
            if (exe is not null)
                versions.Add("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(exe))));
        }
        return versions;
    }
}
