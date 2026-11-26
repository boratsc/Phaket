using System.Diagnostics;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms.SteamShortcuts;

public sealed record SteamShortcut(
    string AppName, string Exe, string StartDir, string LaunchOptions,
    byte[]? CoverJpg = null, byte[]? HeroJpg = null, byte[]? LogoPng = null);

public sealed record SteamShortcutResult(int AppId, string RunGameUri, bool SteamRunning, bool BackupCreated, IReadOnlyList<string> ArtworkPaths);

// Adds/removes a non-Steam shortcut for a Phaket-managed (modded) game, so Steam runs it with its
// own overlay, Input and playtime. Writes only into one found Steam user's config, only with
// explicit consent, backs the file up first and touches only entries tagged "phaket".
// Layout and ids follow HeroicGamesLauncher (GPL-3.0, read only for the format; nothing copied).
public sealed class SteamShortcutService(Func<bool>? isSteamRunning = null)
{
    private readonly Func<bool> isSteamRunning = isSteamRunning ?? DefaultIsSteamRunning;

    // Steam reads shortcuts.vdf at start and rewrites it on exit, so running Steam would silently
    // lose the change; the caller shows "restart Steam" (i18n SteamShortcut.RestartSteam).
    public bool IsSteamRunning => isSteamRunning();

    // Steam never touches the backup; restore it by copying over shortcuts.vdf with Steam closed.
    public const string BackupSuffix = ".phaket.bak";

    public static string ShortcutsPath(string steamRoot, string userId) =>
        Path.Combine(steamRoot, "userdata", userId, "config", "shortcuts.vdf");

    // Userdata folders are numeric Steam ids ("ac" is Steam's account cache, not a user).
    public static IReadOnlyList<string> SteamUsers(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return [];
        return Directory.EnumerateDirectories(userdata)
            .Select(Path.GetFileName)
            .Where(name => name is { Length: > 0 } && name.All(char.IsAsciiDigit))
            .Order(StringComparer.Ordinal)
            .ToArray()!;
    }

    // Every known Steam root that has a user in userdata.
    public static IReadOnlyList<string> FindSteamRoots(string os, string home) =>
        SteamAdapter.DefaultSteamRoots(os, home).Where(root => SteamUsers(root).Count > 0).ToArray();

    // The user the profile was chosen for: userId if it is a user of this root, else the root's
    // first user (Steam writes one shortcuts.vdf per user).
    // An unknown chosen user has no write target at all (null), never a substitution.
    public static string? DefaultUser(string steamRoot, string? userId = null)
    {
        var users = SteamUsers(steamRoot);
        if (userId is not null) return users.Contains(userId) ? userId : null;
        return users.FirstOrDefault();
    }

    public async Task<SteamShortcutResult> AddShortcutAsync(string steamRoot, string userId, SteamShortcut shortcut, bool consent,
        CancellationToken ct = default)
    {
        if (!consent) throw new PhaketException(ErrorCodes.SteamShortcutConsentRequired);
        var (path, root) = Locate(steamRoot, userId);
        Validate(shortcut);
        var entry = SteamShortcutsFile.CreateEntry(shortcut.AppName, shortcut.Exe, shortcut.StartDir, shortcut.LaunchOptions);
        SteamShortcutsFile.TagPhaket(entry);
        var latest = ReadOrMissing(path);
        // Refused before any write: a broken file would lose the user's other shortcuts.
        SteamShortcutsFile.Upsert(latest, entry);
        await WriteAsync(path, latest, ct);
        var appId = entry.GetInt32("appid");
        var grid = ArtworkDir(root, userId);
        Directory.CreateDirectory(grid);
        var artwork = new List<string>();
        foreach (var (name, bytes) in new[]
                 {
                     (GridName(appId, "p.jpg"), shortcut.CoverJpg),
                     (GridName(appId, "_hero.jpg"), shortcut.HeroJpg),
                     // Without a logo Steam draws the game title; the built-in empty image hides it.
                     (GridName(appId, "_logo.png"), shortcut.LogoPng ?? SteamShortcutsFile.TransparentLogoPng),
                 })
        {
            if (bytes is null) continue; // no cover/hero from the caller: Steam's own image shows
            await File.WriteAllBytesAsync(Path.Combine(grid, name), bytes, ct);
            artwork.Add(Path.Combine(grid, name));
        }
        return new SteamShortcutResult(appId, RunGameUri(appId), isSteamRunning(), File.Exists(path + BackupSuffix), artwork);
    }

    // Removes the caller's shortcut: matched by ShortcutPath or Exe when given, otherwise every
    // Phaket-tagged entry of that app name. Their grid images go too.
    public async Task<SteamShortcutResult?> RemoveShortcutAsync(string steamRoot, string userId, string? shortcutPath, string? exe,
        bool consent, CancellationToken ct = default)
    {
        if (!consent) throw new PhaketException(ErrorCodes.SteamShortcutConsentRequired);
        var (path, root) = Locate(steamRoot, userId);
        if (!File.Exists(path)) return null;
        var latest = ReadOrMissing(path);
        var removed = SteamShortcutsFile.Entries(latest).Where(entry =>
                SteamShortcutsFile.IsPhaket(entry) &&
                (shortcutPath is not null ? entry.GetString("ShortcutPath") == shortcutPath
                    : exe is not null ? entry.GetString("Exe") == exe
                    : true))
            .Select(entry => entry.GetInt32("appid")).ToArray();
        if (removed.Length == 0) return null;
        SteamShortcutsFile.RemovePhaket(latest, removed);
        await WriteAsync(path, latest, ct);
        var grid = ArtworkDir(root, userId);
        foreach (var name in removed.SelectMany(ArtworkNames))
            if (File.Exists(Path.Combine(grid, name))) File.Delete(Path.Combine(grid, name));
        return new SteamShortcutResult(removed[0], "", isSteamRunning(), File.Exists(path + BackupSuffix), []);
    }

    // Our shortcuts only, read from the file: a corrupt file is refused loudly, never "none".
    public IReadOnlyList<SteamShortcutResult> ListPhaketShortcuts(string steamRoot, string userId)
    {
        var path = ShortcutsPath(Path.GetFullPath(steamRoot), userId);
        if (!File.Exists(path)) return [];
        return SteamShortcutsFile.Entries(ReadOrMissing(path)).Where(SteamShortcutsFile.IsPhaket)
            .Select(entry => new SteamShortcutResult(entry.GetInt32("appid"), RunGameUri(entry.GetInt32("appid")), false, false, []))
            .ToArray();
    }

    private static string RunGameUri(int appId) => $"steam://rungameid/{(uint)appId}";

    // A root outside the known Steam layout or a user that does not exist is refused, so a typo
    // never creates a stray shortcuts.vdf in the user's home.
    private static (string Path, string Root) Locate(string steamRoot, string userId)
    {
        var root = Path.GetFullPath(steamRoot);
        if (!IsUserOf(root, userId))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "unknown Steam user"));
        return (ShortcutsPath(root, userId), root);
    }

    // The user must be one of this root's numeric userdata folders.
    private static bool IsUserOf(string root, string userId) =>
        userId.Length > 0 && userId.All(char.IsAsciiDigit) && SteamUsers(root).Contains(userId);

    private static void Validate(SteamShortcut shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut.AppName) || shortcut.AppName.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(shortcut.Exe) || shortcut.Exe.Any(c => c is '\0' or '\r' or '\n') ||
            string.IsNullOrWhiteSpace(shortcut.StartDir) || shortcut.StartDir.Any(c => c is '\0' or '\r' or '\n'))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "invalid Steam shortcut entry"));
    }

    // A file we cannot parse is left alone: refusing beats destroying the user's other shortcuts.
    private static BinaryVdfNode ReadOrMissing(string path)
    {
        try { return File.Exists(path) ? SteamShortcutsFile.Parse(File.ReadAllBytes(path)) : SteamShortcutsFile.Parse([]); }
        catch (Exception e) when (e is FormatException or ArgumentException) { throw new PhaketException(ErrorCodes.SteamShortcutsCorrupt, ("path", path)); }
    }

    // Backup of the previous file, temp file next to it, then move: a crash leaves either the old
    // or the new file. The backup keeps the last good copy even after a second change.
    private static async Task WriteAsync(string path, BinaryVdfNode content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(path)) File.Copy(path, path + BackupSuffix, overwrite: true);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, BinaryVdf.Write(content), ct);
        File.Move(temp, path, overwrite: true);
    }

    // Grid names follow Steam's convention for our app id.
    private static string ArtworkDir(string root, string userId) => Path.Combine(root, "userdata", userId, "config", "grid");

    private static string GridName(int appId, string suffix) => (uint)appId + suffix;

    private static string[] ArtworkNames(int appId) =>
        [GridName(appId, "p.jpg"), GridName(appId, ".jpg"), GridName(appId, "_hero.jpg"), GridName(appId, "_logo.png"), GridName(appId, "_logo.jpg")];

    // A pid file is the cheapest signal (Heroic's steamProcess.ts); pgrep catches a Steam started
    // without one (Flatpak). steamwebhelper is not checked: it can outlive the client briefly.
    private static bool DefaultIsSteamRunning()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in SteamAdapter.DefaultSteamRoots(OperatingSystem.IsWindows() ? "windows" : "linux", home))
            foreach (var pidFile in new[] { Path.Combine(root, "steam.pid"), Path.Combine(root, "..", "steam.pid") })
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && IsAlive(pid)) return true;
        if (OperatingSystem.IsWindows())
            return Process.GetProcessesByName("steam").Length > 0 || Process.GetProcessesByName("steamwebhelper").Length > 0;
        try
        {
            using var pgrep = Process.Start(new ProcessStartInfo("pgrep", ["-x", "steam"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (pgrep is null) return false;
            pgrep.StandardOutput.ReadToEnd();
            pgrep.WaitForExit(2000);
            return pgrep.HasExited && pgrep.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }
}