using System.Diagnostics;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Services;

// One-click launch (simplicity): "Graj" opens the game through the Steam
// protocol. Mod launch options are applied to a SINGLE Steam account's
// localconfig.vdf only after the user accepts a shown plan, with a backup
// and rollback — never a manual copy box, never a blind multi-account write.
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

public sealed record SteamAccount(string Id, string ConfigPath);

public sealed record LaunchOptionsPlan(
    SteamAccount Account, string AppId, string? Current, string Proposed);

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

    public IReadOnlyList<SteamAccount> Accounts()
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return [];
        return Directory.GetDirectories(userdata)
            .Select(dir => new SteamAccount(Path.GetFileName(dir), Path.Combine(dir, "config", "localconfig.vdf")))
            .Where(a => File.Exists(a.ConfigPath))
            .ToArray();
    }

    private static VdfNode? AppNode(VdfNode root, string appId) =>
        root.Child("UserLocalConfigStore")?.Child("Software")?.Child("Valve")?
            .Child("Steam")?.Child("Apps")?.Child(appId);

    public string? GetCurrent(SteamAccount account, string appId)
    {
        var node = VdfParser.Parse(File.ReadAllText(account.ConfigPath));
        return AppNode(node, appId)?.Get("LaunchOptions");
    }

    // Builds a plan for one account; nothing is written here.
    public LaunchOptionsPlan? BuildPlan(string appId, string proposed, SteamAccount? account = null)
    {
        var accounts = Accounts();
        var chosen = account ?? accounts.FirstOrDefault();
        if (chosen is null) return null;
        return new LaunchOptionsPlan(chosen, appId, GetCurrent(chosen, appId), proposed);
    }

    // Applies a previously accepted plan to its single account; backs up first.
    public string ApplyPlan(LaunchOptionsPlan plan)
    {
        var path = plan.Account.ConfigPath;
        var node = VdfParser.Parse(File.ReadAllText(path));
        var apps = EnsurePath(node, "UserLocalConfigStore", "Software", "Valve", "Steam", "Apps");
        var app = apps.Child(plan.AppId) ?? new VdfNode();
        app.Values["LaunchOptions"] = plan.Proposed;
        apps.Children[plan.AppId] = app;

        // Unique backup name: two applies in the same second must not overwrite
        // the earlier recovery point.
        var backup = path + $".phaket-bak-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid():N}";
        File.Copy(path, backup, overwrite: true);
        // Atomic replace: a failed write must never truncate the live config.
        var temp = path + ".phaket-tmp";
        File.WriteAllText(temp, VdfWriter.Write(node));
        File.Move(temp, path, overwrite: true);
        return backup;
    }

    public void Rollback(string backupPath)
    {
        var idx = backupPath.IndexOf(".phaket-bak-", StringComparison.Ordinal);
        if (idx < 0 || !File.Exists(backupPath)) return;
        File.Copy(backupPath, backupPath[..idx], overwrite: true);
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
        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in root.Values) WritePair(sb, key, value, 0);
        foreach (var (key, child) in root.Children) WriteObject(sb, key, child, 0);
        return sb.ToString();
    }

    private static void WriteObject(System.Text.StringBuilder sb, string key, VdfNode node, int depth)
    {
        var pad = new string('\t', depth);
        sb.Append(pad).Append('"').Append(Escape(key)).Append("\"\n");
        sb.Append(pad).Append("{\n");
        foreach (var (k, v) in node.Values) WritePair(sb, k, v, depth + 1);
        foreach (var (k, c) in node.Children) WriteObject(sb, k, c, depth + 1);
        sb.Append(pad).Append("}\n");
    }

    private static void WritePair(System.Text.StringBuilder sb, string key, string value, int depth)
    {
        sb.Append(new string('\t', depth)).Append('"').Append(Escape(key)).Append("\"\t\t\"")
            .Append(Escape(value)).Append("\"\n");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
