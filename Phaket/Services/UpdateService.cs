using System.Diagnostics;
using System.Text.Json;
using Velopack;
using Velopack.Exceptions;

namespace Phaket.Services;

public enum UpdateChannel
{
    Stable,
    Beta
}

public enum UpdateOutcome
{
    DevelopmentMode,
    UpToDate,
    Available,
    Error
}

public enum UpdateError
{
    None,
    NotInstalled,
    Checksum,
    Network,
    Download
}

public sealed record UpdateCheckResult(UpdateOutcome Outcome, string? Version, UpdateError Error = UpdateError.None);

public sealed record UpdateDownloadResult(bool Success, UpdateError Error = UpdateError.None);

// Thin seam over Velopack so tests never touch a real UpdateManager or the network.
public interface IUpdateSource
{
    bool IsInstalled { get; }
    string? CurrentVersion { get; }
    Task<string?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(string version, Action<int> progress, CancellationToken cancellationToken);
    void ApplyAndRestart(string version);
}

// Velopack in-app updates (PAK-121): feed/channel selection, check/download/apply and the
// user's channel preference in settings.json. Velopack itself only runs in installed builds.
public sealed class UpdateService
{
    // DO UZGODNIENIA PRZY DEPLOJU: docelowy adres statycznego feedu na VPS
    // (releases.{channel}.json + nupkg w jednym katalogu). Nadpisanie: PHAKET_UPDATE_FEED.
    internal const string DefaultFeedUrl = "https://phaket.app/updates/";

    private readonly string settingsPath;
    private readonly Func<UpdateChannel, IUpdateSource> sourceFactory;
    private IUpdateSource? source;
    private string? availableVersion;

    public UpdateService(string settingsPath, Func<UpdateChannel, IUpdateSource>? sourceFactory = null)
    {
        this.settingsPath = settingsPath;
        (Channel, ChannelExplicit) = ReadChannel(settingsPath);
        // PAK-124: until the user picks a channel on purpose, no ExplicitChannel is set, so
        // Velopack keeps the channel the installation came from (a beta build is not cut
        // back to stable). An explicit choice also enables deliberate downgrades.
        this.sourceFactory = sourceFactory
            ?? (channel => new VelopackUpdateSource(ResolveFeedUrl(), channel, ChannelExplicit));
    }

    public UpdateChannel Channel { get; private set; }

    // False while settings.json carries no channel choice: the installed package's channel wins.
    internal bool ChannelExplicit { get; private set; }

    // Assembly fallback keeps the version visible before a check and in dev builds.
    public string CurrentVersion => source?.CurrentVersion
        ?? (typeof(UpdateService).Assembly.GetName().Version is { } version ? version.ToString(3) : "0.0.0");

    public static string ResolveFeedUrl() =>
        Environment.GetEnvironmentVariable("PHAKET_UPDATE_FEED") is { Length: > 0 } custom
            ? custom
            : DefaultFeedUrl;

    public static string ChannelName(UpdateChannel channel) => channel == UpdateChannel.Beta ? "beta" : "stable";

    public static string FeedChannel(UpdateChannel channel, bool windows) =>
        (windows ? "win-x64-" : "linux-x64-") + ChannelName(channel);

    public static string KeyFor(UpdateError error) => error switch
    {
        UpdateError.NotInstalled => "Settings.Updates.DevMode",
        UpdateError.Checksum => "Settings.Updates.Error.Checksum",
        UpdateError.Network => "Settings.Updates.Error.Network",
        _ => "Settings.Updates.Error.Download"
    };

    public static UpdateChannel LoadChannel(string settingsPath) => ReadChannel(settingsPath).Channel;

    private static (UpdateChannel Channel, bool Explicit) ReadChannel(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return (UpdateChannel.Stable, false);
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!doc.RootElement.TryGetProperty("updateChannel", out var value)) return (UpdateChannel.Stable, false);
            return (string.Equals(value.GetString(), "beta", StringComparison.OrdinalIgnoreCase)
                ? UpdateChannel.Beta
                : UpdateChannel.Stable, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (UpdateChannel.Stable, false);
        }
    }

    // Velopack options for a source. Without an explicit user choice the channel is inherited
    // from the installation and downgrades stay refused.
    internal static UpdateOptions BuildOptions(UpdateChannel channel, bool explicitChannel, bool windows) => new()
    {
        ExplicitChannel = explicitChannel ? FeedChannel(channel, windows) : null,
        AllowVersionDowngrade = explicitChannel
    };

    // A channel switch must be explicit and may move to a lower version on the new channel.
    // Choosing the already active channel still records the preference: that is what turns
    // the inherited channel into a fixed one.
    public bool SetChannel(UpdateChannel channel)
    {
        var rebuild = Channel != channel || !ChannelExplicit;
        Channel = channel;
        ChannelExplicit = true;
        if (rebuild) { availableVersion = null; source = null; }
        return SettingsFile.TryUpdate(settingsPath, node => node["updateChannel"] = ChannelName(channel));
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        IUpdateSource current = Source;
        if (!current.IsInstalled)
            return new UpdateCheckResult(UpdateOutcome.DevelopmentMode, CurrentVersion);

        try
        {
            availableVersion = await current.CheckAsync(cancellationToken).ConfigureAwait(false);
            return availableVersion is null
                ? new UpdateCheckResult(UpdateOutcome.UpToDate, CurrentVersion)
                : new UpdateCheckResult(UpdateOutcome.Available, availableVersion);
        }
        catch (Exception ex) when (ex is NotInstalledException)
        {
            return new UpdateCheckResult(UpdateOutcome.DevelopmentMode, CurrentVersion, UpdateError.NotInstalled);
        }
        catch (Exception ex) when (ex is ChecksumFailedException)
        {
            return new UpdateCheckResult(UpdateOutcome.Error, CurrentVersion, UpdateError.Checksum);
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            return new UpdateCheckResult(UpdateOutcome.Error, CurrentVersion, UpdateError.Network);
        }
        catch (Exception)
        {
            // A malformed PHAKET_UPDATE_FEED or an unexpected source failure must not escape the UI.
            return new UpdateCheckResult(UpdateOutcome.Error, CurrentVersion, UpdateError.Network);
        }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IUpdateSource current = Source;
        if (!current.IsInstalled) return new UpdateDownloadResult(false, UpdateError.NotInstalled);
        if (availableVersion is null) return new UpdateDownloadResult(false, UpdateError.Download);

        try
        {
            await current.DownloadAsync(availableVersion, value => progress?.Report(value), cancellationToken)
                .ConfigureAwait(false);
            return new UpdateDownloadResult(true);
        }
        catch (NotInstalledException)
        {
            return new UpdateDownloadResult(false, UpdateError.NotInstalled);
        }
        catch (ChecksumFailedException)
        {
            return new UpdateDownloadResult(false, UpdateError.Checksum);
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            return new UpdateDownloadResult(false, UpdateError.Network);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AcquireLockFailedException)
        {
            return new UpdateDownloadResult(false, UpdateError.Download);
        }
        catch (Exception)
        {
            return new UpdateDownloadResult(false, UpdateError.Download);
        }
    }

    // On success this exits the process and restarts the updated build; the error is only
    // observable when the launch itself was refused.
    public UpdateError ApplyAndRestart()
    {
        IUpdateSource current = Source;
        if (!current.IsInstalled) return UpdateError.NotInstalled;
        if (availableVersion is null) return UpdateError.Download;
        try
        {
            current.ApplyAndRestart(availableVersion);
            return UpdateError.None;
        }
        catch (NotInstalledException)
        {
            return UpdateError.NotInstalled;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or AcquireLockFailedException)
        {
            return UpdateError.Download;
        }
    }

    private IUpdateSource Source => source ??= sourceFactory(Channel);

    private static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;
}

// Velopack-backed source. Created lazily, so dev runs and tests never build an UpdateManager
// unless a check is actually requested.
internal sealed class VelopackUpdateSource : IUpdateSource
{
    private readonly UpdateManager manager;
    private UpdateInfo? pending;

    public VelopackUpdateSource(string feedUrl, UpdateChannel channel, bool explicitChannel = true)
    {
        manager = new UpdateManager(feedUrl, UpdateService.BuildOptions(channel, explicitChannel, OperatingSystem.IsWindows()));
    }

    public bool IsInstalled => manager.IsInstalled;

    public string? CurrentVersion => manager.CurrentVersion?.ToString();

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pending = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return pending?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAsync(string version, Action<int> progress, CancellationToken cancellationToken)
    {
        pending ??= await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (pending is null) throw new InvalidOperationException("No update is available to download.");
        await manager.DownloadUpdatesAsync(pending, progress, cancellationToken).ConfigureAwait(false);
    }

    public void ApplyAndRestart(string version)
    {
        if (pending is null) throw new InvalidOperationException("No downloaded update to apply.");
        Trace.WriteLine($"Applying Phaket update {version}.");
        manager.ApplyUpdatesAndRestart(pending.TargetFullRelease);
    }
}