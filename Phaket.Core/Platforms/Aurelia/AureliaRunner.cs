using System.Diagnostics;
using Phaket.Core.Omrl;
using Phaket.Core.Tools;

namespace Phaket.Core.Platforms.Aurelia;

// One finished Aurelia run: both streams as they came (ANSI may still be inside) and the exit code.
public sealed record AureliaRunResult(int ExitCode, string Stdout, string Stderr);

public interface IAureliaProcessRunner
{
    Task<AureliaRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, CancellationToken ct);
}

// Real Aurelia process. Read-only commands never prompt, but stdin is still closed at once:
// Aurelia treats an open stdin as a driver's control channel.
public sealed class AureliaProcessRunner : IAureliaProcessRunner
{
    public async Task<AureliaRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new PhaketException(ErrorCodes.AureliaUnavailable, ("reason", "start"));
        try
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new AureliaRunResult(process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            try { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            throw;
        }
    }
}

// Foreground CLI without the daemon: a bare `aurelia list` would start `aurelia daemon` in the
// background and hold a Steam connection after Phaket stops asking, so `AURELIA_NO_SPAWN=1`
// keeps every command one-shot. A missing binary, a start failure or a deadline is one code
// (the callers answer them with the local fallback); a non-zero exit is returned with its
// streams, because a typed error line says more than the code.
public sealed class AureliaRunner
{
    public const string ConfigDirVariable = "AURELIA_CONFIG_DIR";
    public const string NoSpawnVariable = "AURELIA_NO_SPAWN";

    private readonly string? executable;
    private readonly TimeSpan timeout;
    private readonly IAureliaProcessRunner process;

    public AureliaRunner(string? executable, string configDir, TimeSpan? timeout = null, IAureliaProcessRunner? process = null)
    {
        this.executable = string.IsNullOrWhiteSpace(executable) ? null : executable;
        ConfigDir = Path.GetFullPath(configDir);
        this.timeout = timeout ?? TimeSpan.FromSeconds(60);
        this.process = process ?? new AureliaProcessRunner();
    }

    public string ConfigDir { get; }

    // No executable = the tool is not installed: every call reports AURELIA_UNAVAILABLE (reason
    // "missing") and the callers fall back. A non-empty version is the pin to install through
    // ToolProvider; without one the download is skipped, so a test never reaches the network.
    public async Task<AureliaRunner> WithToolAsync(string? pinnedVersion, ToolProvider provider, CancellationToken ct = default)
    {
        if (executable is not null || string.IsNullOrWhiteSpace(pinnedVersion)) return this;
        string path = await provider.EnsureAsync("aurelia", pinnedVersion, ct: ct);
        return new AureliaRunner(path, ConfigDir, timeout, process);
    }

    // Identifies the binary for caches: path, size and mtime (Heroic's daemon stamp, minus the
    // daemon). A missing binary keeps the stamp stable so a repeated call does not look changed.
    public string Stamp
    {
        get
        {
            if (executable is null) return "none";
            try
            {
                var info = new FileInfo(executable);
                return info.Exists ? $"{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}" : info.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return executable; }
        }
    }

    public async Task<AureliaRunResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (executable is null) throw new PhaketException(ErrorCodes.AureliaUnavailable, ("reason", "missing"));
        var env = new Dictionary<string, string?> { [ConfigDirVariable] = ConfigDir, [NoSpawnVariable] = "1" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            // A non-zero exit is not thrown here: a typed error line is worth more than the code,
            // so the callers read the streams first (AureliaOutput.Failure) and decide.
            // A process that ignores its token must not hold the caller past the deadline.
            var run = process.RunAsync(executable, arguments, env, deadline.Token);
            return await run.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PhaketException(ErrorCodes.AureliaUnavailable, ("reason", "timeout")); }
        catch (Exception ex) when (ex is not PhaketException and not OperationCanceledException)
        { throw new PhaketException(ErrorCodes.AureliaUnavailable, ("reason", ex.GetType().Name)); }
    }
}