using System.Diagnostics;
using System.Text.Json;
using Phaket.Core.Accounts;
using Phaket.Core.Omrl;
using Phaket.Core.Tools;

namespace Phaket.Core.Platforms;

public sealed record LegendaryInstallPlan(string AppName, string BasePath, string TargetPath, long? SizeBytes, string Executable, IReadOnlyList<string> Conflicts);
public sealed record LegendaryRecordedInstallation(string AppName, string TargetPath, string Executable, DateTimeOffset InstalledAt);
public sealed record LegendaryTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string AccountId, string DisplayName);

public static class LegendaryTokenProvider
{
    public static Func<CancellationToken, Task<LegendaryTokens>> From(EpicAccountService account) => account.GetLegendaryTokensAsync;
}

public interface IProcessRunner
{
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
        Action<string> output, CancellationToken cancellationToken);
}

public sealed class LegendaryInstallAdapter
{
    private readonly string? binary;
    private readonly IProcessRunner runner;
    private readonly Func<CancellationToken, Task<LegendaryTokens>> tokens;
    private readonly string configRoot;
    private readonly ToolProvider toolProvider;
    private readonly string? settingsPath;

    public LegendaryInstallAdapter(Func<CancellationToken, Task<LegendaryTokens>> tokens, IProcessRunner? runner = null, string? configRoot = null, string? binaryPath = null, ToolProvider? toolProvider = null, string? settingsPath = null)
    {
        this.tokens = tokens;
        this.runner = runner ?? new ProcessRunner();
        this.configRoot = configRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "legendary-config");
        binary = binaryPath ?? FindLegendary(); this.toolProvider = toolProvider ?? new ToolProvider();
        this.settingsPath = settingsPath;
        // A crashed run would otherwise leave its token-bearing config folder behind forever.
        SessionSweep.SweepOrphans(this.configRoot, TimeSpan.FromHours(24));
    }

    public static string? FindLegendary()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "legendary.exe", "legendary" } : new[] { "legendary" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    public LegendaryInstallPlan BuildInstallPlan(OwnedEpicGame game, string targetDir, GameDefinition definition, long? sizeBytes = null)
    {
        if (!game.Installable || string.IsNullOrWhiteSpace(game.AppName)) throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "Epic game is not installable"));
        string basePath = Path.GetFullPath(targetDir);
        string targetPath = Path.Combine(basePath, game.AppName);
        string executable = definition.MainExecutable ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "game executable is not defined"));
        ValidatePaths(basePath, targetPath, game.AppName, executable);
        var conflicts = Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any()
            ? new[] { $"target exists with {Directory.EnumerateFileSystemEntries(targetPath).Count()} entries" }
            : Array.Empty<string>();
        return new(game.AppName, basePath, targetPath, sizeBytes, executable, conflicts);
    }

    public async Task<string> ExecuteInstallAsync(LegendaryInstallPlan plan, bool consent, bool planAccepted, IProgress<string>? progress = null, CancellationToken ct = default, bool overwriteConsent = false)
    {
        if (!planAccepted) throw new PhaketException("PLAN_NOT_ACCEPTED");
        if (!consent) throw new PhaketException("CONSENT_REQUIRED");
        ValidatePaths(plan.BasePath, plan.TargetPath, plan.AppName, plan.Executable);
        RejectLinks(plan.BasePath);
        RejectLinks(plan.TargetPath);
        // Nothing inside the tree may be a link: legendary would follow it and write outside the
        // approved area. Refused before the process starts; a link the tool itself creates is
        // still caught by the post-run check below.
        RejectNestedLinks(plan.TargetPath);
        if (!overwriteConsent && (plan.Conflicts.Count != 0 ||
            Directory.Exists(plan.TargetPath) && Directory.EnumerateFileSystemEntries(plan.TargetPath).Any()))
            throw new PhaketException("OVERWRITE_CONSENT_REQUIRED");
        var pin = ToolSettings.GetPinnedVersion("legendary", settingsPath);
        string exePath = pin is null && binary is not null && File.Exists(binary) ? binary : await toolProvider.EnsureAsync("legendary", pin, ct: ct);
        Directory.CreateDirectory(plan.BasePath);
        string config = Path.Combine(configRoot, Guid.NewGuid().ToString("N"));
        SessionSecurity.EnsurePrivateRoot(configRoot);
        Directory.CreateDirectory(config);
        // Private (0700 / user-only ACL) before the token file is written into it.
        SessionSecurity.MakeDirectoryPrivate(config);
        try
        {
            var account = await tokens(ct);
            var user = new Dictionary<string, object?>
            {
                ["access_token"] = account.AccessToken, ["refresh_token"] = account.RefreshToken,
                ["expires_at"] = account.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["account_id"] = account.AccountId, ["displayName"] = account.DisplayName, ["token_type"] = "bearer"
            };
            var userPath = Path.Combine(config, "user.json");
            // Created empty with 0600 (Windows: user-only ACL) BEFORE the token bytes are written.
            await using (var stream = SessionSecurity.CreatePrivateFile(userPath))
                await JsonSerializer.SerializeAsync(stream, user, cancellationToken: ct);
            var tail = new Queue<string>();
            void Log(string line)
            {
                string safe = line.Replace(account.AccessToken, "[REDACTED]", StringComparison.Ordinal).Replace(account.RefreshToken, "[REDACTED]", StringComparison.Ordinal);
                tail.Enqueue(safe);
                while (tail.Count > 100) tail.Dequeue();
                progress?.Report(safe);
            }
            int exit = await runner.RunAsync(exePath, ["install", plan.AppName, "--base-path", plan.BasePath, "--yes"],
                new Dictionary<string, string?> { ["LEGENDARY_CONFIG_PATH"] = config }, Log, ct);
            if (exit != 0) throw new PhaketException("LEGENDARY_INSTALL_FAILED", ("tail", string.Join(Environment.NewLine, tail)));
            string exe = Path.GetFullPath(Path.Combine(plan.TargetPath, plan.Executable));
            try
            {
                RejectLinks(plan.TargetPath);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!File.Exists(exe) || !ResolvePath(exe).StartsWith(ResolvePath(plan.TargetPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                    throw new PhaketException("LEGENDARY_INSTALL_UNVERIFIED", ("path", exe));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException || e is PhaketException { Error.Code: ErrorCodes.PathOutOfBounds })
            {
                throw new PhaketException("LEGENDARY_INSTALL_UNVERIFIED", ("path", exe));
            }
            RecordInstallation(new(plan.AppName, plan.TargetPath, plan.Executable, DateTimeOffset.UtcNow));
            return exe;
        }
        finally { CleanupConfig(config); }
    }

    private static void RejectLinks(string path) => LinkGuard.RejectLinks(path);

    private static void RejectNestedLinks(string path) => LinkGuard.RejectNestedLinks(path);

    private static string ResolvePath(string path) => LinkGuard.Resolve(path);

    private static void ValidatePaths(string basePath, string targetPath, string appName, string executable)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string fullBase = Path.GetFullPath(basePath);
        string fullTarget = Path.GetFullPath(targetPath);
        if (string.IsNullOrWhiteSpace(appName) || appName.IndexOfAny(['/', '\\']) >= 0 || appName is "." or ".." ||
            !string.Equals(fullTarget, Path.GetFullPath(Path.Combine(fullBase, appName)), comparison))
            throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", targetPath));
        if (Path.IsPathRooted(executable) || executable.Split(['/', '\\']).Any(p => p == "..") ||
            !Path.GetFullPath(Path.Combine(fullTarget, executable)).StartsWith(fullTarget + Path.DirectorySeparatorChar, comparison))
            throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", executable));
    }

    public IReadOnlyList<LegendaryRecordedInstallation> GetRecordedInstallations()
    {
        try
        {
            string path = Path.Combine(configRoot, "..", "epic-installs.json");
            return JsonSerializer.Deserialize<List<LegendaryRecordedInstallation>>(File.ReadAllText(Path.GetFullPath(path))) ?? [];
        }
        catch { return []; }
    }

    private void RecordInstallation(LegendaryRecordedInstallation installation)
    {
        string path = Path.GetFullPath(Path.Combine(configRoot, "..", "epic-installs.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var installations = GetRecordedInstallations().Where(x => x.AppName != installation.AppName).Append(installation).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(installations));
    }

    public void CleanupConfig(string path)
    {
        string root = Path.GetFullPath(configRoot) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", path));
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private sealed class ProcessRunner : IProcessRunner
    {
        public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, Action<string> output, CancellationToken ct)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            using var process = Process.Start(start) ?? throw new PhaketException("LEGENDARY_INSTALL_FAILED", ("tail", "failed to start legendary"));
            var readOut = Read(process.StandardOutput, output, ct);
            var readErr = Read(process.StandardError, output, ct);
            try { await process.WaitForExitAsync(ct); }
            catch { try { process.Kill(true); } catch { } throw; }
            await Task.WhenAll(readOut, readErr);
            return process.ExitCode;
        }
        private static async Task Read(StreamReader stream, Action<string> output, CancellationToken ct)
        {
            while (await stream.ReadLineAsync(ct) is { } line) output(line);
        }
    }
}
