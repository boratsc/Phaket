using System.Diagnostics;
using System.Security.Cryptography;
using Phaket.Core.Accounts;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Tools;

namespace Phaket.Core.Execution;

// Host runner that shells out to the OFFICIAL base-game tools, the only allowed
// vanilla sources (never a private mirror): DepotDownloader for Steam manifests,
// legendary for Epic. The engine stays declarative: it decides WHAT and WHERE,
// this class only runs the pinned tool with an argument list (no shell).
//
// Steam auth follows the library install (PAK-60/PAK-102): the account comes from the
// secret store, the password auto-answers the first prompt, and Steam Guard asks the
// host through the same shared dialog. Without a stored sign-in the provision refuses
// with an explanation instead of waiting forever on a prompt nobody can see.
public sealed class ToolBaseGameSource : IBaseGameSource
{
    private readonly string? depotDownloaderPath;
    private readonly string? legendaryPath;
    private readonly ToolProvider toolProvider;
    private readonly TimeSpan timeout;
    private readonly string? dotnetHostPath;
    private readonly IDepotProcessRunner runner;
    private readonly SteamCredentialStore? steamCredentials;
    private readonly Func<DepotPrompt, CancellationToken, Task<string>>? steamPrompt;
    private readonly Func<CancellationToken, Task<LegendaryTokens>>? epicTokens;
    private readonly string? legendaryConfigRoot;
    private readonly string? settingsPath;
    private readonly IProgress<string?>? mobileConfirmation;

    public ToolBaseGameSource(string? depotDownloaderPath = null, string? legendaryPath = null,
        TimeSpan? timeout = null, ToolProvider? toolProvider = null, string? dotnetHostPath = null,
        SteamCredentialStore? steamCredentials = null,
        Func<DepotPrompt, CancellationToken, Task<string>>? steamPrompt = null,
        Func<CancellationToken, Task<LegendaryTokens>>? epicTokens = null,
        IDepotProcessRunner? runner = null, string? legendaryConfigRoot = null, string? settingsPath = null,
        IProgress<string?>? mobileConfirmation = null)
    {
        this.mobileConfirmation = mobileConfirmation;
        this.toolProvider = toolProvider ?? new ToolProvider();
        this.dotnetHostPath = dotnetHostPath;
        this.runner = runner ?? new DepotProcessRunner();
        this.steamCredentials = steamCredentials;
        this.steamPrompt = steamPrompt;
        this.epicTokens = epicTokens;
        this.legendaryConfigRoot = legendaryConfigRoot;
        this.settingsPath = settingsPath;
        // An explicit path or environment override wins; otherwise the tool installed by
        // ToolProvider (PAK-69: ~/.config/phaket/tools/<tool>/<version>/...) is the product
        // path, so a provision does not require the user to export variables.
        this.depotDownloaderPath = depotDownloaderPath
            ?? Environment.GetEnvironmentVariable("PHAKET_DEPOTDOWNLOADER");
        this.legendaryPath = legendaryPath
            ?? Environment.GetEnvironmentVariable("PHAKET_LEGENDARY");
        this.timeout = timeout ?? TimeSpan.FromMinutes(30);
    }

    // A managed install is the fast path; a pin or a missing binary goes through
    // ToolProvider so provision can download and verify the tool itself.
    private async Task<string> EnsureToolAsync(string tool, string? configured, CancellationToken ct)
    {
        if (configured is { Length: > 0 } && File.Exists(configured)) return configured;
        var pin = ToolSettings.GetPinnedVersion(tool, settingsPath);
        if (pin is null && toolProvider.ResolveInstalled(tool) is { } installed) return installed;
        return await toolProvider.EnsureAsync(tool, pin, ct: ct);
    }

    public async Task<BaseGamePrepareResult> TryPrepareAsync(string store, string version, string? manifestId,
        string targetDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDirectory);
        try
        {
            if (store == "steam") return await PrepareSteamAsync(version, manifestId, targetDirectory, ct);
            if (store == "epic") return await PrepareEpicAsync(version, manifestId, targetDirectory, ct);
            return BaseGamePrepareResult.Fail(ErrorCodes.ValidationFailed, ("detail", $"unsupported store: {store}"));
        }
        catch (PhaketException ex)
        {
            return BaseGamePrepareResult.Fail(ex.Error.Code, ex.Error.Parameters.Select(p => (p.Key, p.Value)).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameToolFailed, ("detail", ex.Message));
        }
    }

    private async Task<BaseGamePrepareResult> PrepareSteamAsync(string version, string? manifestId, string targetDirectory, CancellationToken ct)
    {
        // Steam can only pin a version through a manifest id (observed by running
        // DepotDownloader once); without it the tool would fetch the CURRENT depot,
        // silently breaking the mod's version pin.
        if (string.IsNullOrWhiteSpace(manifestId))
            return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameManifestMissing, ("version", version));
        var account = await ReadAccountAsync(ct);
        // Without a stored sign-in the tool would block on an invisible prompt. Refusing
        // with the one action that fixes it beats a silent hang (PAK-102).
        if (account is null || steamPrompt is null)
            return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameSignInRequired);
        string depotDownloader;
        try { depotDownloader = await EnsureToolAsync("depotdownloader", depotDownloaderPath, ct); }
        catch (PhaketException ex) { return BaseGamePrepareResult.Fail(ex.Error.Code, ex.Error.Parameters.Select(p => (p.Key, p.Value)).ToArray()); }
        return await RunSteamAsync(depotDownloader, targetDirectory, manifestId, account, ct);
    }

    private async Task<SteamInstallCredentials?> ReadAccountAsync(CancellationToken ct)
    {
        if (steamCredentials is null) return null;
        try { return await steamCredentials.ReadAsync(ct); }
        catch (Exception ex) when (ex is SecretStoreException or CryptographicException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private async Task<BaseGamePrepareResult> RunSteamAsync(string executable, string targetDirectory, string manifestId,
        SteamInstallCredentials account, CancellationToken ct)
    {
        var fileName = executable;
        var args = new List<string>(BaseGameToolArguments.DepotDownloader(targetDirectory, manifestId, account.Username));
        // The managed DepotDownloader is a framework-dependent .dll; it needs the dotnet host.
        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            var host = dotnetHostPath is { } injected
                ? (File.Exists(injected) ? injected : null)
                : FindDotnetHost(name, Environment.GetEnvironmentVariable("PATH"),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (host is null) return BaseGamePrepareResult.Fail(ErrorCodes.DepotDownloaderDotnetMissing);
            fileName = host;
            args.Insert(0, executable);
        }
        var secrets = new List<string>();
        var prompts = new DepotPromptReader();
        // Auto-fill answers the first password prompt only. A second one means the tool
        // rejected the stored value, so the user is asked instead of looping on it.
        var passwordAutoFilled = false;
        async Task Output(bool stderr, string chunk, Func<string, Task> send)
        {
            foreach (var ch in chunk)
            {
                var prompt = prompts.Append(stderr, ch, out var line);
                // The mobile confirmation prints a notice instead of a prompt; reporting it
                // lets the host show the PAK-105 dialog while the tool waits for the phone.
                if (prompt is null)
                {
                    if (stderr && line?.Contains(DepotPromptReader.MobileConfirmation, StringComparison.Ordinal) == true)
                        mobileConfirmation?.Report(DepotPromptReader.MobileConfirmation);
                    continue;
                }
                string answer;
                if (prompt == DepotPrompt.Password && !passwordAutoFilled)
                {
                    passwordAutoFilled = true;
                    answer = account.Password;
                }
                else
                {
                    try { answer = await steamPrompt!(prompt.Value, ct); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PhaketException(ErrorCodes.BaseGameAuthCancelled); }
                    catch (TimeoutException) { throw new PhaketException(ErrorCodes.BaseGameAuthCancelled); }
                }
                if (string.IsNullOrEmpty(answer) || answer.Any(c => c is '\r' or '\n'))
                    throw new PhaketException(ErrorCodes.BaseGameAuthCancelled);
                secrets.Add(answer);
                await send(answer);
            }
        }
        int exit;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            try { exit = await runner.RunAsync(fileName, args, new Dictionary<string, string?>(), Output, linked.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameTimeout); }
        }
        // The notice lives while the tool waits: success, a failed run and a cancel all end it.
        finally { mobileConfirmation?.Report(null); }
        prompts.Flush();
        string Redact(string s)
        {
            foreach (var secret in secrets.OrderByDescending(s => s.Length)) s = s.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            return s;
        }
        var tail = SanitizeToolOutput(Redact(string.Concat(prompts.Lines)));
        if (exit != 0)
            return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameToolFailed, ("detail", tail.Length > 0 ? tail : $"exit code {exit}"));
        return BaseGamePrepareResult.Ok;
    }

    // The tail is untrusted external output: control characters are stripped (line breaks
    // become spaces so it stays one readable line) and the length is capped for the UI.
    internal const int MaxToolOutput = 2000;
    internal static string SanitizeToolOutput(string value)
    {
        var builder = new System.Text.StringBuilder(Math.Min(value.Length, MaxToolOutput));
        foreach (var ch in value)
        {
            if (builder.Length >= MaxToolOutput) break;
            if (ch is '\r' or '\n' or '\t') builder.Append(' ');
            else if (!char.IsControl(ch)) builder.Append(ch);
        }
        return builder.ToString().Trim();
    }

    private async Task<BaseGamePrepareResult> PrepareEpicAsync(string version, string? manifestId, string targetDirectory, CancellationToken ct)
    {
        string legendary;
        try { legendary = await EnsureToolAsync("legendary", legendaryPath, ct); }
        catch (PhaketException ex) { return BaseGamePrepareResult.Fail(ex.Error.Code, ex.Error.Parameters.Select(p => (p.Key, p.Value)).ToArray()); }
        if (string.IsNullOrWhiteSpace(manifestId))
            return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameManifestMissing, ("version", version));
        var manifestFile = manifestId.EndsWith(".manifest", StringComparison.Ordinal) ? manifestId : manifestId + ".manifest";
        // Manifests live beside the per-version caches, never inside them
        // (CopyVanilla would otherwise ship them into the game tree).
        var manifestDir = Path.Combine(targetDirectory, "..", "..", "manifests");
        Directory.CreateDirectory(manifestDir);
        var manifestPath = Path.Combine(manifestDir, manifestFile);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(BaseGameToolArguments.EpicManifestMirror + Uri.EscapeDataString(manifestFile), ct);
            if (!response.IsSuccessStatusCode)
                return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameToolFailed, ("detail", $"epic manifest download failed: {(int)response.StatusCode}"));
            File.WriteAllBytes(manifestPath, await response.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameToolFailed, ("detail", $"epic manifest download failed: {ex.Message}")); }
        var epicAppId = Environment.GetEnvironmentVariable("PHAKET_EPIC_APP_ID") ?? "963137e4c29d4c79a81323b8fab03a40";
        // legendary signs in through the token file PAK-58 already writes for library
        // installs; the isolated folder is private and removed after the run.
        string? config = null;
        try
        {
            var environment = new Dictionary<string, string?>();
            if (epicTokens is not null)
            {
                LegendaryTokens account;
                try { account = await epicTokens(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { return BaseGamePrepareResult.Fail(ErrorCodes.EpicSignInRequired, ("detail", ex.Message)); }
                config = CreateLegendaryConfig(account);
                environment["LEGENDARY_CONFIG_PATH"] = config;
            }
            int exit;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                linked.CancelAfter(timeout);
                try { exit = await runner.RunAsync(legendary, BaseGameToolArguments.Legendary(epicAppId, manifestPath, targetDirectory), environment, (_, _, _) => Task.CompletedTask, linked.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameTimeout); }
            }
            if (exit != 0)
                return BaseGamePrepareResult.Fail(ErrorCodes.BaseGameToolFailed, ("detail", $"legendary exit code {exit}"));
            // legendary installs into <base>/<AppDir>; flatten a single child
            // directory so the cache holds the game tree directly.
            FlattenSingleChild(targetDirectory);
            return BaseGamePrepareResult.Ok;
        }
        finally
        {
            if (config is not null && Directory.Exists(config)) Directory.Delete(config, recursive: true);
        }
    }

    private string CreateLegendaryConfig(LegendaryTokens account)
    {
        var root = legendaryConfigRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "legendary-config");
        SessionSecurity.EnsurePrivateRoot(root);
        var config = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        SessionSecurity.MakeDirectoryPrivate(config);
        var user = new Dictionary<string, object?>
        {
            ["access_token"] = account.AccessToken, ["refresh_token"] = account.RefreshToken,
            ["expires_at"] = account.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["account_id"] = account.AccountId, ["displayName"] = account.DisplayName, ["token_type"] = "bearer"
        };
        using var stream = SessionSecurity.CreatePrivateFile(Path.Combine(config, "user.json"));
        System.Text.Json.JsonSerializer.Serialize(stream, user);
        return config;
    }

    // The managed DepotDownloader is a framework-dependent .dll, so the dotnet host
    // is required. GUI-launched apps often do not inherit a PATH that has it;
    // installer locations are checked before refusing.
    internal static string? FindDotnetHost(string name, string? path, string home) =>
        DotnetHostCandidates(name, path, home).FirstOrDefault(File.Exists) is { } found ? Path.GetFullPath(found) : null;

    internal static IEnumerable<string> DotnetHostCandidates(string name, string? path, string home)
    {
        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, name);
        if (OperatingSystem.IsWindows()) yield break;
        yield return "/usr/share/dotnet/" + name;
        yield return "/usr/lib/dotnet/" + name;
        yield return Path.Combine(home, ".dotnet", name);
    }

    private static void FlattenSingleChild(string targetDirectory)
    {
        var children = Directory.GetDirectories(targetDirectory);
        if (children.Length != 1 || Directory.GetFiles(targetDirectory).Length > 0) return;
        var child = children[0];
        foreach (var entry in Directory.GetFileSystemEntries(child))
        {
            var destination = Path.Combine(targetDirectory, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination, overwrite: true);
        }
        Directory.Delete(child, recursive: true);
    }
}