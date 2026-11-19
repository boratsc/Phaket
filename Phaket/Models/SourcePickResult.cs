namespace Phaket.Models;

public abstract record SourcePickResult;

public sealed record GitHubReleasePickResult(
    string Repo,
    string Tag,
    long AssetId,
    string AssetName,
    string DownloadUrl,
    long Size,
    bool IsDemo = false) : SourcePickResult;

public sealed record NexusManualPickResult(
    string Url,
    string Name,
    string PinnedVersion,
    string LocalArchivePath) : SourcePickResult;
