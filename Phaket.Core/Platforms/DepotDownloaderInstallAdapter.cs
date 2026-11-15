using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

public sealed record DepotInstallPlan(string AppId, string BasePath, string TargetPath, string Executable, string Os, string OsArch, string? DepotId, string? ManifestId, IReadOnlyList<string> Conflicts);
public sealed record DepotRecordedInstallation(string AppId, string TargetPath, string Executable, DateTimeOffset InstalledAt);
public enum DepotPrompt { Password, SteamGuard }
public sealed record DepotSessionStores(string Root, string Status, IReadOnlyList<string> Paths);
public sealed record DepotSessionCleanupResult(string Path, string Error);

public interface IDepotProcessRunner
{
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
        Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct);
}

public sealed class DepotDownloaderInstallAdapter
{
    private readonly string? binary;
    private readonly string? host;
    private readonly IDepotProcessRunner runner;
    private readonly string sessionRoot;
    private readonly string recordsPath;

    public DepotDownloaderInstallAdapter(IDepotProcessRunner? runner = null, string? configRoot = null, string? binaryPath = null)
    {
        this.runner = runner ?? new DepotProcessRunner();
        var root = configRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket");
        sessionRoot = Path.Combine(root, "depot-session");
        recordsPath = Path.Combine(root, "steam-installs.json");
        binary = binaryPath ?? FindOnPath(OperatingSystem.IsWindows() ? ["DepotDownloader.exe", "DepotDownloader.dll"] : ["DepotDownloader", "depotdownloader", "DepotDownloader.dll"]);
        host = binary?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true ? FindOnPath(OperatingSystem.IsWindows() ? ["dotnet.exe"] : ["dotnet"]) : null;
        if (binary?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true && host is null) binary = null;
        // A crashed run would otherwise leave its session (with cached Steam tickets) behind forever.
        SessionSweep.SweepOrphans(sessionRoot, TimeSpan.FromHours(24));
    }

    private static string? FindOnPath(string[] names)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
                if (File.Exists(Path.Combine(dir, name))) return Path.GetFullPath(Path.Combine(dir, name));
        return null;
    }

    // `os` is the HOST operating system. The depot is fetched for the game's own platform
    // (Among Us ships Windows-only binaries, so under Proton on Linux it is still "windows"),
    // never for the host (PAK-65).
    public DepotInstallPlan BuildInstallPlan(OwnedSteamGame game, string targetDir, GameDefinition definition, string os, string osArch, string? depotId = null, string? manifestId = null)
    {
        RequireBinary();
        var basePath = Path.GetFullPath(targetDir);
        var targetPath = Path.Combine(basePath, game.AppId);
        var plan = new DepotInstallPlan(game.AppId, basePath, targetPath, definition.MainExecutable ?? "", definition.ArtifactOs(os), osArch, depotId, manifestId, []);
        Validate(plan);
        RejectLinks(targetPath);
        return plan with { Conflicts = Conflicts(targetPath) ? ["target exists and is not empty"] : [] };
    }

    public async Task<string> ExecuteInstallAsync(DepotInstallPlan plan, string username, Func<DepotPrompt, CancellationToken, Task<string>> credentials,
        bool consent, bool planAccepted, IProgress<string>? progress = null, CancellationToken ct = default, bool overwriteConsent = false, bool rememberSessionConsent = false)
    {
        if (!planAccepted) throw new PhaketException("PLAN_NOT_ACCEPTED");
        if (!consent) throw new PhaketException("CONSENT_REQUIRED");
        // Windows uses the user's known folder and may overwrite an existing store; explicit persistence consent is required.
        if (OperatingSystem.IsWindows() && !rememberSessionConsent) throw new PhaketException("DEPOTDOWNLOADER_SESSION_CONSENT_REQUIRED");
        Validate(plan);
        RejectLinks(plan.TargetPath);
        // Refusing an unsafe tree comes before any consent question: overwriting consent cannot
        // make a link that escapes the approved area acceptable (PAK-65).
        RejectNestedLinks(plan.TargetPath);
        if (!overwriteConsent && (plan.Conflicts.Count > 0 || Conflicts(plan.TargetPath))) throw new PhaketException("OVERWRITE_CONSENT_REQUIRED");
        RequireBinary();
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl)) throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "invalid username"));
        string session = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N"));
        SessionSecurity.EnsurePrivateRoot(sessionRoot);
        Directory.CreateDirectory(session);
        // 0700 / user-only ACL before the tool writes its cached tickets into the session.
        SessionSecurity.MakeDirectoryPrivate(session);
        try
        {
            var env = new Dictionary<string, string?>
            {
                ["HOME"] = Path.Combine(session, "home"), ["XDG_DATA_HOME"] = Path.Combine(session, "data"),
                ["LOCALAPPDATA"] = Path.Combine(session, "localappdata"), ["APPDATA"] = Path.Combine(session, "roaming")
            };
            foreach (var path in env.Values) Directory.CreateDirectory(path!);
            var args = new List<string>();
            if (host is not null) args.Add(binary!);
            args.AddRange(["-app", plan.AppId, "-username", username, "-os", plan.Os, "-osarch", plan.OsArch, "-dir", plan.TargetPath]);
            if (plan.DepotId is not null) args.AddRange(["-depot", plan.DepotId]);
            if (plan.ManifestId is not null) args.AddRange(["-manifest", plan.ManifestId]);
            var secrets = new List<string>();
            var lines = new Queue<string>();
            var buffers = new[] { new StringBuilder(), new StringBuilder() };
            var discardLine = new bool[2];
            async Task Output(bool stderr, string chunk, Func<string, Task> send)
            {
                var index = stderr ? 1 : 0;
                var b = buffers[index];
                foreach (char ch in chunk)
                {
                    if (discardLine[index]) { if (ch == '\n') discardLine[index] = false; continue; }
                    b.Append(ch);
                    string current = b.ToString();
                    DepotPrompt? prompt = !stderr && current.StartsWith("Enter account password for ", StringComparison.Ordinal) && current.EndsWith(": ", StringComparison.Ordinal) ? DepotPrompt.Password :
                        stderr && current.StartsWith("STEAM GUARD! Please enter ", StringComparison.Ordinal) && current.EndsWith(": ", StringComparison.Ordinal) ? DepotPrompt.SteamGuard : null;
                    if (prompt is not null)
                    {
                        string secret = await credentials(prompt.Value, ct);
                        if (string.IsNullOrEmpty(secret) || secret.Any(c => c is '\r' or '\n')) throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "invalid credential"));
                        secrets.Add(secret);
                        await send(secret);
                        b.Clear();
                    }
                    else if (ch == '\n')
                    {
                        if (stderr && b.ToString().Contains("STEAM GUARD! Use the Steam Mobile App to confirm your sign in...", StringComparison.Ordinal))
                            progress?.Report("STEAM GUARD! Use the Steam Mobile App to confirm your sign in...");
                        lines.Enqueue(b.ToString()); b.Clear();
                        while (lines.Count > 100) lines.Dequeue();
                    }
                    else if (b.Length >= 8192) { b.Clear(); discardLine[index] = true; }
                }
            }
            Directory.CreateDirectory(plan.BasePath);
            int exit = await runner.RunAsync(host ?? binary!, args, env, Output, ct);
            foreach (var buffer in buffers) if (buffer.Length > 0) lines.Enqueue(buffer.ToString());
            string Redact(string s) { foreach (var secret in secrets.OrderByDescending(s => s.Length)) s = s.Replace(secret, "[REDACTED]", StringComparison.Ordinal); return s; }
            var safe = Redact(string.Concat(lines.TakeLast(100)));
            progress?.Report(safe);
            if (exit != 0) throw new PhaketException("DEPOTDOWNLOADER_INSTALL_FAILED", ("tail", safe));
            string exe = Path.GetFullPath(Path.Combine(plan.TargetPath, plan.Executable));
            try
            {
                RejectLinks(plan.TargetPath);
                if (!File.Exists(exe) || !Resolve(exe).StartsWith(Resolve(plan.TargetPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison))
                    throw new PhaketException("DEPOTDOWNLOADER_INSTALL_UNVERIFIED", ("path", exe));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException || e is PhaketException { Error.Code: ErrorCodes.PathOutOfBounds })
            { throw new PhaketException("DEPOTDOWNLOADER_INSTALL_UNVERIFIED", ("path", exe)); }
            Record(new(plan.AppId, plan.TargetPath, plan.Executable, DateTimeOffset.UtcNow));
            return exe;
        }
        finally { if (OperatingSystem.IsWindows() || !rememberSessionConsent) CleanupSession(session); }
    }

    public DepotSessionStores ListSessionStores()
    {
        // .NET 10 does not write identity.dat; legacy binary evidence cannot safely establish ownership here.
        // Inspect this root manually with the tool or Explorer; never infer ownership from timestamps.
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsolatedStorage");
        return new(root, "attribution unavailable", []);
    }

    public IReadOnlyList<DepotSessionCleanupResult> CleanupSessionStores(IEnumerable<string> paths, Func<string, bool> confirmCallback)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(confirmCallback);
        // No stores can currently be attributed, so none can be offered for confirmation or deletion.
        return paths.Select(path => new DepotSessionCleanupResult(path, "attribution unavailable")).ToArray();
    }

    private void RequireBinary() { if (binary is null || !File.Exists(binary)) throw new PhaketException("DEPOTDOWNLOADER_NOT_FOUND", ("loc", "install_depotdownloader_manually")); }
    private static bool Conflicts(string path) => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool Id(string? id) => !string.IsNullOrEmpty(id) && id.All(c => c is >= '0' and <= '9') && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0;
    private static void Validate(DepotInstallPlan p)
    {
        if (!Id(p.AppId) || (p.DepotId is not null && !Id(p.DepotId)) || (p.ManifestId is not null && (!Id(p.ManifestId) || p.DepotId is null)) || p.Os is not ("windows" or "linux") || p.OsArch is not ("32" or "64"))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "invalid Steam install plan"));
        string root = Path.GetFullPath(p.BasePath), target = Path.GetFullPath(p.TargetPath);
        if (!string.Equals(target, Path.Combine(root, p.AppId), Comparison) || string.IsNullOrWhiteSpace(p.Executable) || Path.IsPathRooted(p.Executable) || p.Executable.Split(['/', '\\']).Any(x => x == "..") || !Path.GetFullPath(Path.Combine(target, p.Executable)).StartsWith(target + Path.DirectorySeparatorChar, Comparison))
            throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", p.TargetPath));
    }
    private static void RejectLinks(string path)
    {
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)) throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", current));
        }
    }
    private static void RejectNestedLinks(string path)
    {
        if (!Directory.Exists(path)) return;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", entry.FullName));
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
            }
    }
    private static string Resolve(string path)
    {
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unable to resolve link");
        }
        return current;
    }
    public IReadOnlyList<DepotRecordedInstallation> GetRecordedInstallations()
    {
        try { return JsonSerializer.Deserialize<List<DepotRecordedInstallation>>(File.ReadAllText(recordsPath)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    private void Record(DepotRecordedInstallation install)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(recordsPath)!);
        var records = GetRecordedInstallations().Where(x => x.AppId != install.AppId).Append(install).ToList();
        File.WriteAllText(recordsPath, JsonSerializer.Serialize(records));
    }
    public void CleanupSession(string path)
    {
        string root = Path.GetFullPath(sessionRoot) + Path.DirectorySeparatorChar, full = Path.GetFullPath(path);
        if (!full.StartsWith(root, Comparison) || !string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(root), Comparison)) throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", path));
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    private sealed class DepotProcessRunner : IDepotProcessRunner
    {
        public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            using var process = Process.Start(start) ?? throw new PhaketException("DEPOTDOWNLOADER_INSTALL_FAILED");
            using var gate = new SemaphoreSlim(1);
            async Task Read(StreamReader stream, bool stderr)
            {
                try
                {
                    var buffer = new char[1024];
                    while (await stream.ReadAsync(buffer.AsMemory(), ct) is var n && n > 0)
                    {
                        await gate.WaitAsync(ct);
                        try { await output(stderr, new string(buffer, 0, n), s => process.StandardInput.WriteLineAsync(s)); }
                        finally { gate.Release(); }
                    }
                }
                catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
            }
            var reads = Task.WhenAll(Read(process.StandardOutput, false), Read(process.StandardError, true));
            try
            {
                var wait = process.WaitForExitAsync(ct);
                var first = await Task.WhenAny(reads, wait);
                if (first == reads) await reads;
                await wait; await reads;
                return process.ExitCode;
            }
            catch { try { process.Kill(true); await process.WaitForExitAsync(); } catch (InvalidOperationException) { } throw; }
        }
    }
}
