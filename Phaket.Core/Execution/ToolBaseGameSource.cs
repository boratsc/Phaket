using System.Diagnostics;

namespace Phaket.Core.Execution;

// Host runner that shells out to the OFFICIAL base-game tools, the only allowed
// vanilla sources (never a private mirror): DepotDownloader for Steam manifests,
// legendary for Epic. The engine stays declarative: it decides WHAT and WHERE,
// this class only runs the pinned tool with an argument list (no shell).
public sealed class ToolBaseGameSource : IBaseGameSource
{
    private readonly string? depotDownloaderPath;
    private readonly string? legendaryPath;
    private readonly TimeSpan timeout;

    public ToolBaseGameSource(string? depotDownloaderPath = null, string? legendaryPath = null,
        TimeSpan? timeout = null)
    {
        this.depotDownloaderPath = depotDownloaderPath
            ?? Environment.GetEnvironmentVariable("PHAKET_DEPOTDOWNLOADER");
        this.legendaryPath = legendaryPath
            ?? Environment.GetEnvironmentVariable("PHAKET_LEGENDARY");
        this.timeout = timeout ?? TimeSpan.FromMinutes(30);
    }

    public bool TryPrepare(string store, string version, string? manifestId, string targetDirectory, out string? error)
    {
        error = null;
        Directory.CreateDirectory(targetDirectory);
        try
        {
            if (store == "steam")
            {
                // Steam can only pin a version through a manifest id (observed by
                // running DepotDownloader once); without it the tool would fetch the
                // CURRENT depot, silently breaking the mod's version pin.
                if (string.IsNullOrWhiteSpace(manifestId))
                {
                    error = "steam version pin requires an observed manifest id";
                    return false;
                }
                if (depotDownloaderPath is null || !File.Exists(depotDownloaderPath))
                {
                    error = "DepotDownloader path not configured (PHAKET_DEPOTDOWNLOADER)";
                    return false;
                }
                return Run(depotDownloaderPath, BaseGameToolArguments.DepotDownloader(targetDirectory, manifestId), out error);
            }
            if (store == "epic")
            {
                if (legendaryPath is null || !File.Exists(legendaryPath))
                {
                    error = "legendary path not configured (PHAKET_LEGENDARY)";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(manifestId))
                {
                    error = "epic version pin requires a manifest file name";
                    return false;
                }
                var manifestFile = manifestId.EndsWith(".manifest", StringComparison.Ordinal) ? manifestId : manifestId + ".manifest";
                // Manifests live beside the per-version caches, never inside them
                // (CopyVanilla would otherwise ship them into the game tree).
                var manifestDir = Path.Combine(targetDirectory, "..", "..", "manifests");
                Directory.CreateDirectory(manifestDir);
                var manifestPath = Path.Combine(manifestDir, manifestFile);
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    using var response = http.GetAsync(BaseGameToolArguments.EpicManifestMirror + Uri.EscapeDataString(manifestFile)).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        error = $"epic manifest download failed: {(int)response.StatusCode}";
                        return false;
                    }
                    File.WriteAllBytes(manifestPath, response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    error = $"epic manifest download failed: {ex.Message}";
                    return false;
                }
                var epicAppId = Environment.GetEnvironmentVariable("PHAKET_EPIC_APP_ID") ?? "963137e4c29d4c79a81323b8fab03a40";
                if (!Run(legendaryPath, BaseGameToolArguments.Legendary(epicAppId, manifestPath, targetDirectory), out error))
                    return false;
                // legendary installs into <base>/<AppDir>; flatten a single child
                // directory so the cache holds the game tree directly.
                FlattenSingleChild(targetDirectory);
                return true;
            }
            error = $"unsupported store: {store}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool Run(string executable, IReadOnlyList<string> arguments, out string? error)
    {
        error = null;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = false, // DepotDownloader may need interactive auth
            },
        };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            error = "base game tool timed out";
            return false;
        }
        if (process.ExitCode != 0)
        {
            error = $"base game tool exited with code {process.ExitCode}";
            return false;
        }
        return true;
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
