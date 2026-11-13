using System.Diagnostics;
using System.Text;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Services;

// One-click launch (simplicity): "Graj" opens the game through the Steam
// protocol. Mod launch options are applied to Steam's localconfig.vdf only
// after a single consent, with a backup and rollback — never a manual copy box.
public sealed class LaunchService
{
    public bool LaunchGame(string steamAppId)
    {
        if (string.IsNullOrWhiteSpace(steamAppId)) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{steamAppId}",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

// Reads/writes the Steam per-app LaunchOptions string with backup + rollback.
// This is the "safe adapter" path (ADR-045): consented, reversible, scoped to
// one app key — not a blind patch of the whole file.
public sealed class SteamLaunchOptionsService
{
    private readonly string steamRoot;

    public SteamLaunchOptionsService(string steamRoot) => this.steamRoot = steamRoot;

    public static string? FindSteamRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in SteamAdapter.DefaultSteamRoots(
            OperatingSystem.IsWindows() ? "windows" : "linux", home))
            if (Directory.Exists(root)) return root;
        return null;
    }

    private IEnumerable<string> LocalConfigPaths()
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) yield break;
        foreach (var dir in Directory.GetDirectories(userdata))
        {
            var path = Path.Combine(dir, "config", "localconfig.vdf");
            if (File.Exists(path)) yield return path;
        }
    }

    public string? GetCurrent(string appId)
    {
        foreach (var path in LocalConfigPaths())
        {
            var node = VdfParser.Parse(File.ReadAllText(path));
            var value = node.Child("UserLocalConfigStore")?.Child("Software")?.Child("Valve")?
                .Child("Steam")?.Child("Apps")?.Child(appId)?.Get("LaunchOptions");
            if (value is not null) return value;
        }
        return null;
    }

    // Applies options to every local user config; backs up each file first.
    // Returns the backup paths so the caller can roll back.
    public IReadOnlyList<string> Apply(string appId, string options)
    {
        var backups = new List<string>();
        foreach (var path in LocalConfigPaths())
        {
            var text = File.ReadAllText(path);
            var node = VdfParser.Parse(text);
            var apps = EnsurePath(node, "UserLocalConfigStore", "Software", "Valve", "Steam", "Apps");
            var app = apps.Child(appId) ?? new VdfNode();
            app.Values["LaunchOptions"] = options;
            apps.Children[appId] = app;

            var backup = path + $".phaket-bak-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.Copy(path, backup, overwrite: true);
            backups.Add(backup);
            File.WriteAllText(path, VdfWriter.Write(node));
        }
        return backups;
    }

    public void Rollback(IReadOnlyList<string> backups)
    {
        foreach (var backup in backups)
        {
            var original = backup[..backup.IndexOf(".phaket-bak-", StringComparison.Ordinal)];
            if (File.Exists(backup)) File.Copy(backup, original, overwrite: true);
        }
    }

    private static VdfNode EnsurePath(VdfNode root, params string[] keys)
    {
        var current = root;
        foreach (var key in keys)
        {
            if (!current.Children.TryGetValue(key, out var child))
            {
                child = new VdfNode();
                current.Children[key] = child;
            }
            current = child;
        }
        return current;
    }
}

// Minimal VDF text writer (quoted keys/values, tab indentation).
public static class VdfWriter
{
    public static string Write(VdfNode root)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in root.Values) WritePair(sb, key, value, 0);
        foreach (var (key, child) in root.Children) WriteObject(sb, key, child, 0);
        return sb.ToString();
    }

    private static void WriteObject(StringBuilder sb, string key, VdfNode node, int depth)
    {
        var pad = new string('\t', depth);
        sb.Append(pad).Append('"').Append(Escape(key)).Append("\"\n");
        sb.Append(pad).Append("{\n");
        foreach (var (k, v) in node.Values) WritePair(sb, k, v, depth + 1);
        foreach (var (k, c) in node.Children) WriteObject(sb, k, c, depth + 1);
        sb.Append(pad).Append("}\n");
    }

    private static void WritePair(StringBuilder sb, string key, string value, int depth)
    {
        sb.Append(new string('\t', depth)).Append('"').Append(Escape(key)).Append("\"\t\t\"")
            .Append(Escape(value)).Append("\"\n");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
