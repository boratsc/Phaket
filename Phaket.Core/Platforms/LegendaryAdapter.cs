using System.Diagnostics;
using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

public sealed record LegendaryGame(string AppName, string Title, string InstallPath, string Version, string Platform);

// Epic detection via Legendary (PAK-13, ADR-020): reads installed.json facts
// only. NEVER reads user.json or any credential file; auth stays in Legendary.
public sealed class LegendaryAdapter
{
    private readonly IReadOnlyList<GameDefinition> definitions;

    public LegendaryAdapter(IReadOnlyList<GameDefinition> definitions) => this.definitions = definitions;

    public static string DefaultConfigDir(string home) => Path.Combine(home, ".config", "legendary");

    public IReadOnlyList<LegendaryGame> DetectInstalled(string configDir)
    {
        var installedPath = Path.Combine(configDir, "installed.json");
        if (!File.Exists(installedPath)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(installedPath));
        var games = new List<LegendaryGame>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var entry = property.Value;
            games.Add(new LegendaryGame(
                property.Name,
                entry.TryGetProperty("title", out var title) ? title.GetString() ?? property.Name : property.Name,
                entry.TryGetProperty("install_path", out var path) ? path.GetString() ?? "" : "",
                entry.TryGetProperty("version", out var version) ? version.GetString() ?? "" : "",
                entry.TryGetProperty("platform", out var platform) ? platform.GetString() ?? "Windows" : "Windows"));
        }
        return games;
    }

    public IReadOnlyList<Installation> ToInstallations(IReadOnlyList<LegendaryGame> games) =>
        games.Select(game =>
        {
            var definition = definitions.FirstOrDefault(d =>
                d.StoreIds?.TryGetValue("epic", out var epicId) == true
                && string.Equals(epicId, game.AppName, StringComparison.OrdinalIgnoreCase));
            return new Installation(
                $"legendary-{game.AppName}",
                $"{definition?.Slug ?? game.AppName}-epic",
                game.InstallPath);
        }).ToArray();

    public IReadOnlyList<string> ProbeVersion(LegendaryGame game) =>
        game.Version.Length > 0 ? [game.Version] : [];
}

// Verified-process CLI invocation: no shell, timeout, cancellation, JSON output.
public interface ILegendaryCli
{
    Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class LegendaryCli : ILegendaryCli
{
    private readonly string binaryPath;

    public LegendaryCli(string binaryPath)
    {
        if (!File.Exists(binaryPath))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"legendary binary not found: {binaryPath}"));
        this.binaryPath = binaryPath;
    }

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false, // never a shell; arguments are passed verbatim
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)
            ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "failed to start legendary"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            // Read both streams concurrently: a full stderr buffer must not
            // deadlock the process until timeout.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            var output = await stdoutTask;
            if (process.ExitCode != 0)
                throw new PhaketException(ErrorCodes.ValidationFailed,
                    ("detail", $"legendary exited with {process.ExitCode}: {await stderrTask}"));
            return output;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }
    }
}
