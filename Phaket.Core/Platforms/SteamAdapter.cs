using System.Security.Cryptography;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

// Separated game states (spec §7.1): in library != installed != moddable != supported.
public enum GameAvailability { InLibrary, Installed, Moddable, VersionSupported }

public sealed record SteamAppManifest(
    string AppId, string Name, string BuildId, string Branch,
    string ManifestId, string InstallDir, long SizeOnDisk,
    // W1/PAK-14 probe: depot id -> manifest id actually installed. Lets a build
    // without a known build id still resolve to a game version label.
    IReadOnlyDictionary<string, string>? DepotManifests = null,
    // PAK-144: unix seconds from the appmanifest; tells which copy Steam last touched.
    long LastUpdated = 0);

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

    // Deterministic across processes/runs (string.GetHashCode is not stable).
    private static string StablePathId(string path) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..12];

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
        VdfNode root;
        // A corrupt library list must not abort detection: the root library stays.
        try { root = VdfParser.Parse(File.ReadAllText(vdfPath)); }
        catch (Exception) { return libraries; }
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
        var depots = new Dictionary<string, string>(StringComparer.Ordinal);
        if (state.Child("InstalledDepots") is { } installed)
            foreach (var (depotId, entry) in installed.Children)
                if (entry.Get("manifest") is { Length: > 0 } manifest)
                    depots[depotId] = manifest;
        return new SteamAppManifest(
            appId,
            state.Get("name") ?? "",
            state.Get("buildid") ?? "",
            state.Get("branch") ?? "public",
            state.Get("manifest") ?? "",
            state.Get("installdir") ?? "",
            long.TryParse(state.Get("SizeOnDisk"), out var size) ? size : 0,
            depots,
            long.TryParse(state.Get("LastUpdated"), out var updated) ? updated : 0);
    }

    public IReadOnlyList<SteamInstallation> DetectInstallations(string os, string home)
    {
        var results = new List<SteamInstallation>();
        foreach (var root in DefaultSteamRoots(os, home))
        {
            if (!Directory.Exists(root)) continue;
            var isFlatpak = root.Replace('\\', '/').Contains(".var/app/com.valvesoftware.Steam", StringComparison.Ordinal);
            foreach (var library in ParseLibraries(root, isFlatpak))
            {
                var steamapps = Path.Combine(library.Path, "steamapps");
                if (!Directory.Exists(steamapps)) continue;
                foreach (var acf in Directory.GetFiles(steamapps, "appmanifest_*.acf").Order(StringComparer.Ordinal))
                {
                    SteamAppManifest? manifest;
                    // One corrupt/unreadable acf must not abort the whole scan.
                    try { manifest = ParseAppManifest(acf); }
                    catch (Exception) { continue; }
                    if (manifest is null) continue;
                    var installDir = Path.Combine(steamapps, "common", manifest.InstallDir);
                    var definition = definitions.FirstOrDefault(d =>
                        d.StoreIds?.TryGetValue("steam", out var appId) == true && appId == manifest.AppId);
                    var editionId = $"{definition?.Slug ?? manifest.AppId}-steam";
                    var installation = new Installation(
                        $"steam-{manifest.AppId}-{StablePathId(library.Path)}",
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

    // Whether a detected installation and a recorded path are the same folder
    // (symlinks and trailing separators must not look like two copies).
    public static bool SameInstallation(SteamInstallation installation, string path)
    {
        try
        {
            var left = Path.GetFullPath(installation.Installation.RootPath).TrimEnd(Path.DirectorySeparatorChar);
            var right = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(left, right, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    // PAK-144: read-only duplicate report. The same Steam app installed in two
    // libraries is legitimately possible; this only lists the copies so the user
    // can spot the one the launcher actually runs. Nothing is ever deleted.
    public static IReadOnlyList<SteamDuplicateGroup> FindDuplicateInstalls(string os, string home) =>
        GroupDuplicates(DetectLibraries(os, home));

    public static IReadOnlyList<SteamDuplicateGroup> GroupDuplicates(IEnumerable<SteamInstallation> installations)
    {
        var groups = new List<SteamDuplicateGroup>();
        foreach (var byApp in installations.GroupBy(i => i.Manifest.AppId, StringComparer.Ordinal))
        {
            // One folder can hold several appmanifests; the folder is the duplicate,
            // not every manifest that happens to sit in it.
            var copies = byApp.GroupBy(i => i.Library.Path, StringComparer.Ordinal).Select(g =>
            {
                var live = g.First();
                foreach (var copy in g) if (copy.Manifest.LastUpdated > live.Manifest.LastUpdated) live = copy;
                return live;
            }).ToArray();
            if (copies.Length < 2) continue;
            var newest = copies[0];
            foreach (var copy in copies) if (copy.Manifest.LastUpdated > newest.Manifest.LastUpdated) newest = copy;
            groups.Add(new SteamDuplicateGroup(byApp.Key, copies[0].Manifest.Name, newest, copies));
        }
        return groups;
    }

    // All installations from every default Steam root (native, Flatpak, extra
    // libraries), deduplicated by folder.
    public static IReadOnlyList<SteamInstallation> DetectLibraries(string os, string home)
    {
        var results = new List<SteamInstallation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in DefaultSteamRoots(os, home))
        {
            if (!Directory.Exists(root)) continue;
            var isFlatpak = root.Replace('\\', '/').Contains(".var/app/com.valvesoftware.Steam", StringComparison.Ordinal);
            foreach (var library in ParseLibraries(root, isFlatpak))
            {
                var steamapps = Path.Combine(library.Path, "steamapps");
                if (!Directory.Exists(steamapps)) continue;
                foreach (var acf in Directory.GetFiles(steamapps, "appmanifest_*.acf").Order(StringComparer.Ordinal))
                {
                    SteamAppManifest? manifest;
                    // One corrupt/unreadable acf must not abort the whole scan.
                    try { manifest = ParseAppManifest(acf); }
                    catch (Exception) { continue; }
                    if (manifest is null) continue;
                    var installDir = Path.Combine(steamapps, "common", manifest.InstallDir);
                    var installation = new Installation($"steam-{manifest.AppId}-{StablePathId(library.Path)}",
                        $"{manifest.AppId}-steam", installDir);
                    // Unknown games still count as installations for the duplicate report.
                    if (seen.Add(installation.InstallationId))
                        results.Add(new SteamInstallation(installation, manifest, library, GameAvailability.Installed));
                }
            }
        }
        return results;
    }
}

// One app installed more than once; Live is the copy Steam updated last.
public sealed record SteamDuplicateGroup(
    string AppId, string Name, SteamInstallation Live, IReadOnlyList<SteamInstallation> Copies);
