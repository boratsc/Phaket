using System.Diagnostics;
using System.Text.Json;
using Velopack;
using Velopack.Exceptions;
using Velopack.Logging;
using Velopack.Sources;

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
    // PAK-110 (serwer = źródło prawdy): manifesty leżą w http://host:port/api/releases?channel=<kanał>&os=<win|linux>,
    // pod którymi Velopack czyta i tak `releases.<kanał>.<os>.json`. Nadpisanie: PHAKET_UPDATE_FEED.
    internal const string DefaultFeedUrl = "https://phaket.app/api/releases";

    // Serwer przyjmuje tylko kanały release i beta (PhaketServer/Phaket.Api/Releases.cs Channels),
    // os=win|linux wybiera manifest; obie wartości to też segmenty nazwy pliku manifestu.

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
            ?? (channel => CreateVelopackSource(FeedUrl(channel, OperatingSystem.IsWindows()), channel, ChannelExplicit));
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

    // Nasz feed: kanał i system wybierają manifest (manifest = katalog kanału z PAK-110).
    public static string FeedUrl(UpdateChannel channel, bool windows) =>
        ResolveFeedUrl() + "?channel=" + ChannelName(channel) + "&os=" + (windows ? "win" : "linux");

    public static string ChannelName(UpdateChannel channel) => channel == UpdateChannel.Beta ? "beta" : "release";

    // Velopack sam czyta tylko własne kształty manifestu (SimpleWebSource dokleja "<os>/"), więc
    // kontrakt serwera PAK-110 czyta własne źródło. Kanał wybiera już adres manifestu.
    internal static IUpdateSource CreateVelopackSource(string feedUrl, UpdateChannel channel, bool explicitChannel) =>
        new VelopackUpdateSource(feedUrl, BuildOptions(channel, explicitChannel, OperatingSystem.IsWindows()));

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

    // Feed contains only the selected channel's packages, so no Velopack channel filter is needed
    // and the stored preference only decides which manifest URL gets checked.
    internal static UpdateOptions BuildOptions(UpdateChannel channel, bool explicitChannel, bool windows) => new()
    {
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

    public VelopackUpdateSource(string feedUrl, UpdateOptions options)
    {
        manager = new UpdateManager(new ServerFeedSource(feedUrl), options);
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

// Feed serwera PAK-110: GET /api/releases?channel=<kanał>&os=<win|linux> zwraca
// {success, latestVersion, downloadBaseUrl, releases[{Version, File, SHA256}]}, gdzie File to
// nazwa pliku doklejana do downloadBaseUrl. Velopack czyta wyłącznie ten kształt przez to źródło.
// Pełna nazwa interfejsu: Phaket.Services.IUpdateSource to własna szewka testowa.
internal sealed class ServerFeedSource(string manifestUrl) : Velopack.Sources.IUpdateSource
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var json = await http.GetStringAsync(manifestUrl).ConfigureAwait(false);
        return Parse(json);
    }

    // Osobno, żeby test bez sieci sprawdzał mapowanie kontraktu na VelopackAsset.
    internal static VelopackAssetFeed Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("success").GetBoolean() is false) throw new InvalidDataException("Feed reports failure.");
        var baseUrl = root.TryGetProperty("downloadBaseUrl", out var b) ? b.GetString() ?? "" : "";
        var assets = new List<VelopackAsset>();
        foreach (var release in root.GetProperty("releases").EnumerateArray())
        {
            var file = release.GetProperty("File").GetString() ?? "";
            assets.Add(new VelopackAsset
            {
                PackageId = "Phaket",
                Version = SemanticVersion.Parse(release.GetProperty("Version").GetString() ?? throw new InvalidDataException("Version")),
                FileName = file.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? file : baseUrl + file,
                SHA256 = release.TryGetProperty("SHA256", out var sha) ? sha.GetString() : null,
                Size = release.TryGetProperty("Size", out var size) ? size.GetInt64() : 0,
            });
        }
        return new VelopackAssetFeed { Assets = [.. assets] };
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default)
    {
        using var response = await http.GetAsync(releaseEntry.FileName, HttpCompletionOption.ResponseHeadersRead, cancelToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false);
        await using var target = File.Create(localFile);
        var buffer = new byte[81920];
        long done = 0, total = response.Content.Headers.ContentLength ?? 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancelToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancelToken).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Invoke((int)Math.Min(100, done * 100 / total));
        }
        progress?.Invoke(100);
    }
}