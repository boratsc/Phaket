using System.Text.Json;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms.Aurelia;

public sealed record AureliaManifest(string DepotId, string? DepotName, string Branch, string ManifestId, long? Size)
{
    // The pair a `downgrade --manifest {depot}:{manifest}` wants. Ids are u64 upstream and stay
    // text here for that reason.
    public string Pin => $"{DepotId}:{ManifestId}";
}

// Optional read-only complement to the local Steam detection (PAK-137). Aurelia adds what the
// appmanifests cannot say: the account's licences, Family Sharing, and a depot's manifest per
// branch. Every mismatch, missing binary or tool error ends in the local detection
// (SteamLibraryService, Source="local"), never in a failure for the user.
public sealed class AureliaLibraryService
{
    private readonly AureliaRunner runner;
    private readonly Func<CancellationToken, Task<IReadOnlyList<OwnedSteamGame>>>? local;
    private readonly IReadOnlyList<GameDefinition> definitions;
    private readonly TimeSpan capabilitiesTtl;
    private readonly SemaphoreSlim gate = new(1, 1);
    private (string Stamp, DateTimeOffset At, AureliaCapabilities? Capabilities)? gateCache;

    public AureliaLibraryService(AureliaRunner runner, string? steamId = null, IReadOnlyList<GameDefinition>? definitions = null,
        string? dataRoot = null, string? userProfile = null, TimeSpan? capabilitiesTtl = null)
        : this(runner, steamId is null ? null : ct => new SteamLibraryService(steamId, definitions ?? [], dataRoot, userProfile).GetOwnedGamesAsync(ct),
            definitions ?? [], capabilitiesTtl)
    { }

    public AureliaLibraryService(AureliaRunner runner, Func<CancellationToken, Task<IReadOnlyList<OwnedSteamGame>>>? localDetection,
        IReadOnlyList<GameDefinition> definitions, TimeSpan? capabilitiesTtl = null)
    {
        this.runner = runner;
        local = localDetection;
        this.definitions = definitions;
        // Short by default: an answer read before `aurelia login` must not survive it. Capabilities
        // runs offline, so a refresh costs a process start, not a Steam logon.
        this.capabilitiesTtl = capabilitiesTtl ?? TimeSpan.FromMinutes(1);
    }

    public string ConfigDir => runner.ConfigDir;

    // The gate answers from its cache until the binary changes (path, size, mtime) or the TTL runs
    // out. A capabilities call is offline, so this is cheap; a missing or broken tool yields null,
    // which the callers read as "use the local detection".
    public async Task<AureliaCapabilities?> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        string stamp = runner.Stamp;
        await gate.WaitAsync(ct);
        try
        {
            if (gateCache is { } cached && cached.Stamp == stamp && DateTimeOffset.UtcNow - cached.At < capabilitiesTtl) return cached.Capabilities;
            AureliaCapabilities? capabilities = null;
            try
            {
                var run = await runner.RunAsync(AureliaContract.Capabilities(), ct);
                // A non-zero exit or an error document means the gate is off, whatever else came out.
                if (run.ExitCode == 0 && AureliaOutput.Failure(run) is null) capabilities = AureliaCapabilities.Parse(run.Stdout);
            }
            catch (Exception ex) when (ex is PhaketException or ArgumentException) { }
            gateCache = (stamp, DateTimeOffset.UtcNow, capabilities);
            return capabilities;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> IsSupportedAsync(CancellationToken ct = default) => (await GetCapabilitiesAsync(ct))?.IsSupported == true;

    public async Task<IReadOnlyList<OwnedSteamGame>> GetOwnedGamesAsync(CancellationToken ct = default)
    {
        if (!await IsSupportedAsync(ct)) return await LocalAsync(ct);
        try
        {
            var run = await runner.RunAsync(AureliaContract.List(), ct);
            if (AureliaOutput.Failure(run) is not null || AureliaOutput.Result(run) is not { ValueKind: JsonValueKind.Array } rows)
                return await LocalAsync(ct);
            var games = rows.EnumerateArray().Select(ToGame).Where(game => game is not null).Select(game => game!).ToArray();
            // A row without the promised fields means a contract change mid-run, and a half-read
            // library is worse than the local list.
            return games.Length == rows.GetArrayLength() ? games : await LocalAsync(ct);
        }
        catch (Exception ex) when (ex is PhaketException or ArgumentException or InvalidOperationException)
        { return await LocalAsync(ct); }
    }

    // Each depot's current manifest per branch; Steam exposes only the current ids, which is what
    // pins a downgrade (`{depot}:{manifest}`). Throws the typed Phaket code: the caller that shows
    // manifests needs the reason, unlike the library listing which always has the local answer.
    public async Task<IReadOnlyList<AureliaManifest>> GetManifestsAsync(string appId, string? depotId = null, CancellationToken ct = default)
    {
        // The argv is built first: a refused argument must never cost a process start, whatever
        // the tool's state is.
        var arguments = AureliaContract.Manifests(AureliaContract.AppId(appId), depotId is null ? null : AureliaContract.DepotId(depotId));
        await RequireSupportedAsync(ct);
        var run = await runner.RunAsync(arguments, ct);
        return AureliaOutput.Rows(Checked(run), "manifests")
            .Where(row => row.ValueKind == JsonValueKind.Object && AureliaOutput.Id(row, "depot_id") is not null &&
                AureliaOutput.String(row, "branch") is not null && AureliaOutput.Id(row, "manifest_id") is not null)
            .Select(row => new AureliaManifest(
                AureliaOutput.Id(row, "depot_id")!, AureliaOutput.String(row, "depot_name"), AureliaOutput.String(row, "branch")!,
                AureliaOutput.Id(row, "manifest_id")!, Size(row)))
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetBranchesAsync(string appId, CancellationToken ct = default)
    {
        var arguments = AureliaContract.Branches(appId);
        await RequireSupportedAsync(ct);
        var root = Checked(await runner.RunAsync(arguments, ct));
        return root is { ValueKind: JsonValueKind.Object } document && document.TryGetProperty("branches", out var branches) && branches.ValueKind == JsonValueKind.Array
            ? branches.EnumerateArray().Where(branch => branch.ValueKind == JsonValueKind.String).Select(branch => branch.GetString()!).ToArray()
            : [];
    }

    private async Task RequireSupportedAsync(CancellationToken ct)
    {
        if (!await IsSupportedAsync(ct)) throw new PhaketException(ErrorCodes.AureliaUnavailable, ("reason", "capabilities"));
    }

    private static JsonElement? Checked(AureliaRunResult run) =>
        AureliaOutput.Failure(run) is { } failure
            ? throw new PhaketException(AureliaOutput.Code(failure.Type), ("detail", failure.Message))
            : AureliaOutput.Result(run);

    private async Task<IReadOnlyList<OwnedSteamGame>> LocalAsync(CancellationToken ct) => local is null ? [] : await local(ct);

    private OwnedSteamGame? ToGame(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || AureliaListFields.Missing(row)) return null;
        string? appId = AureliaOutput.Id(row, "app_id");
        string? name = AureliaOutput.String(row, "name");
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name)) return null;
        // Family-Shared titles are playable but not licensed to this account: kept visible, at lower
        // confidence, and never counted as a licence.
        bool licensed = AureliaOutput.Bool(row, "is_owned") == true && AureliaOutput.Bool(row, "is_family_shared") != true;
        return new OwnedSteamGame(appId, name, AureliaOutput.Int(row, "playtime_forever_minutes"),
            definitions.FirstOrDefault(definition => definition.StoreIds?.GetValueOrDefault("steam") == appId),
            licensed ? "aurelia" : "aurelia-family", LowerConfidence: !licensed);
    }

    // A depot's byte size; a value beyond long range is not a size worth showing.
    private static long? Size(JsonElement row) =>
        row.TryGetProperty("size", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var size) && Math.Abs(size) < 9.2e18 ? (long)size : null;
}