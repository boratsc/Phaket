using System.Diagnostics;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Platforms.SteamShortcuts;

namespace Phaket.Services;

public sealed record LaunchOutcome(bool Started, bool Tracked, bool SavesShared = false)
{
    public static LaunchOutcome Failed { get; } = new(false, false);

    // Diagnostic detail for a refused start; null for a successful start. Never shown to users.
    internal string? Failure { get; init; }
}

// One-click launch (simplicity): "Graj" opens the game through the Steam
// protocol. Mod launch options are applied to a SINGLE Steam account's
// localconfig.vdf only after the user accepts a shown plan, with a backup
// and rollback — never a manual copy box, never a blind multi-account write.
public sealed class LaunchService
{
    private readonly ProcessTracker trackers;

    // The one place a steam:// request leaves the app. Tests replace it to see the exact URL
    // instead of asking a real Steam; the app never does.
    internal static Func<string, bool>? SteamLauncher;

    // processStorePath is a seam for tests; app state lives in %AppData%/phaket/processes.json,
    // the same file SessionSweep refreshes.
    public LaunchService(string? processStorePath = null) => trackers = new ProcessTracker(processStorePath);

    public bool LaunchGame(string steamAppId)
    {
        if (string.IsNullOrWhiteSpace(steamAppId)) return false;
        var uri = $"steam://rungameid/{steamAppId}";
        if (SteamLauncher is { } launcher) return launcher(uri);
        try
        {
            // steam://rungameid only asks the Steam client to start the game: no process of
            // ours, so no PID. A record here would point at Steam, never at the game.
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Direct launch of a game executable, with no launcher in between: the only path
    // where Phaket owns a process and can record PID + start time for "stop".
    // A store launcher (Steam -applaunch) hands over to a child and exits; its record is
    // dropped again so "stop" never targets the launcher. Nothing waits here (the UI must
    // not freeze) and Stop re-verifies identity anyway.
    // directArguments is a test seam that lets a fixture pass `cmd /c ...` on Windows without
    // giving up the real OpenProcess/GetProcessTimes tracking; the app never sets it.
    public LaunchOutcome LaunchExecutable(string? executable, string? workingDirectory, string? appId, string? sessionLabel, string? directArguments = null)
        => LaunchExecutable(executable, workingDirectory, appId, sessionLabel, directArguments, environment: null, savesShared: false);

    // PAK-146 (model B): a package launch passes the redirected environment (saves/config
    // inside the package) and savesShared from PackageSavesIsolation; the overload above keeps
    // launching exactly as before (no overrides, shared = false). A package launch that could
    // not redirect is flagged by the caller as savesShared, because it then writes into the
    // user's real profile.
    public LaunchOutcome LaunchExecutable(string? executable, string? workingDirectory, string? appId,
        string? sessionLabel, string? directArguments, IReadOnlyDictionary<string, string>? environment, bool savesShared)
    {
        // A launcher-installed game runs by name (Steam hands over the exe path with the game's
        // working directory); only a rooted executable can be checked for existence here.
        if (string.IsNullOrWhiteSpace(executable) || (Path.IsPathRooted(executable) && !File.Exists(executable)))
            return LaunchOutcome.Failed with { Failure = $"missing executable '{executable}'" };
        int pid;
        Process? process = null;
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = directArguments ?? "",
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
            };
            foreach (var (key, value) in environment ?? new Dictionary<string, string>()) info.Environment[key] = value;
            process = Process.Start(info);
            pid = process?.Id ?? 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return LaunchOutcome.Failed with { Failure = $"{e.GetType().Name}: {e.Message}" };
        }
        if (pid <= 0 || process is null) return LaunchOutcome.Failed;
        using var child = process;

        var (found, identity) = ProcessIdentity.TryGetProcessIdentity(pid);
        if (!found || identity is null) return new LaunchOutcome(true, false, savesShared);
        var record = ProcessRecord.FromIdentity(identity, appId, sessionLabel);
        trackers.Save(record);
        // Still alive right after the start means the game is ours to track. On Windows the
        // image starts suspended and TerminateProcess queues: the exit code stays STILL_ACTIVE
        // while it lives, so liveness is read from the child itself, not only from the store.
        // A false here means a launcher handed over and exited.
        if (trackers.IsAlive(record) || StillActive(process))
            return new LaunchOutcome(true, true, savesShared);
        trackers.Remove(record);
        return new LaunchOutcome(true, false, savesShared);
    }

    private static bool StillActive(Process process)
    {
        try
        {
            return !process.HasExited || process.ExitCode == StillActiveExitCode;
        }
        catch (InvalidOperationException) { return false; }
    }

    // Win32 STILL_ACTIVE: also what a suspended process reports before it is resumed.
    private const int StillActiveExitCode = 259;

    // PAK-146 B: "Graj" through Steam. The shortcut's own id runs the modded package (Steam
    // knows its Exe); the store id runs the vanilla game. Steam passes its own environment, so
    // a launch that goes through it ALWAYS shares saves with the base game — never claim the
    // package's redirect here. A steam:// launch leaves no PID of ours: nothing to track or stop.
    // The same diagnostic Failure travels back when Steam refuses the request.
    public LaunchOutcome LaunchShortcut(string? shortcutAppId, string? storeAppId, string? sessionLabel) =>
        LaunchGame(shortcutAppId ?? storeAppId ?? "")
            ? new LaunchOutcome(true, false, SavesShared: true)
            : LaunchOutcome.Failed with
            {
                Failure = $"steam:// launch refused for '{shortcutAppId ?? storeAppId}'",
                SavesShared = true,
            };

    // The newest record of this app, or null when Phaket never launched the game directly.
    public ProcessRecord? Tracked(string? appId) =>
        trackers.Load()
            .Where(r => appId is null || r.AppId == appId)
            .OrderByDescending(r => r.LaunchedAt)
            .FirstOrDefault();

    public ProcessStopResult Stop(ProcessRecord record, bool force = false) => trackers.TryStop(record, force);
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
