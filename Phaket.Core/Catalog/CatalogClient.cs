using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Phaket.Core.Catalog;

// Local catalog cache (spec §7.12.2, ADR-030): snapshot + deltas with tombstones,
// ETag, offline reads with last-sync date. The client never downloads game files.
public sealed record CatalogBuild(
    string Id, string GameId, string Store, string VersionLabel, string BuildId,
    IReadOnlyList<string> DepotIds, IReadOnlyList<string> ManifestIds, string Branch,
    string AvailabilityStatus, string Source, string SourceReference,
    DateTimeOffset? ObservedAt, DateTimeOffset? VerifiedAt, double Confidence, string Provenance);

public sealed record CatalogResponse(int StatusCode, string? ETag, string? Body);

public interface ICatalogTransport
{
    Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken);
}

public sealed class HttpCatalogTransport : ICatalogTransport
{
    private readonly HttpClient http;
    public HttpCatalogTransport(HttpClient http) => this.http = http;

    public async Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        using var response = await http.SendAsync(request, cancellationToken);
        var etag = response.Headers.ETag?.ToString();
        var body = response.StatusCode == System.Net.HttpStatusCode.NotModified ? null : await response.Content.ReadAsStringAsync(cancellationToken);
        return new CatalogResponse((int)response.StatusCode, etag, body);
    }
}

public sealed class CatalogStore : IDisposable
{
    private readonly SqliteConnection connection;

    public CatalogStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS catalog_builds(
                id TEXT PRIMARY KEY, game_id TEXT NOT NULL, store TEXT NOT NULL, version_label TEXT NOT NULL,
                build_id TEXT NOT NULL, depot_ids TEXT NOT NULL, manifest_ids TEXT NOT NULL, branch TEXT NOT NULL,
                availability_status TEXT NOT NULL, source TEXT NOT NULL, source_reference TEXT NOT NULL,
                observed_at TEXT, verified_at TEXT, confidence REAL NOT NULL, provenance TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS catalog_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public long Revision => long.TryParse(GetMeta("revision"), out var rev) ? rev : 0;
    public string? ETag => GetMeta("etag");
    public DateTimeOffset? LastSync => DateTimeOffset.TryParse(GetMeta("lastSync"), out var ts) ? ts : null;

    public string? GetMeta(string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM catalog_meta WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO catalog_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public void ApplyChanges(JsonElement changes, long newRevision, string? etag)
    {
        using var tx = connection.BeginTransaction();
        foreach (var change in changes.EnumerateArray())
        {
            var type = change.GetProperty("type").GetString();
            var buildId = change.GetProperty("buildId").GetString()!;
            if (type == "revoke")
            {
                using var del = connection.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM catalog_builds WHERE id=$id";
                del.Parameters.AddWithValue("$id", buildId);
                del.ExecuteNonQuery();
            }
            else
            {
                Upsert(change.GetProperty("payload"), tx);
            }
        }
        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = "INSERT INTO catalog_meta(key,value) VALUES('revision',$rev) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            meta.Parameters.AddWithValue("$rev", newRevision.ToString());
            meta.ExecuteNonQuery();
        }
        if (etag is not null)
        {
            using var meta = connection.CreateCommand();
            meta.Transaction = tx;
            meta.CommandText = "INSERT INTO catalog_meta(key,value) VALUES('etag',$etag) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            meta.Parameters.AddWithValue("$etag", etag);
            meta.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ReplaceAll(JsonElement items, long revision, string? etag)
    {
        using var tx = connection.BeginTransaction();
        using (var clear = connection.CreateCommand()) { clear.Transaction = tx; clear.CommandText = "DELETE FROM catalog_builds"; clear.ExecuteNonQuery(); }
        foreach (var item in items.EnumerateArray()) Upsert(item, tx);
        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = "INSERT INTO catalog_meta(key,value) VALUES('revision',$rev) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            meta.Parameters.AddWithValue("$rev", revision.ToString());
            meta.ExecuteNonQuery();
        }
        if (etag is not null)
        {
            using var meta = connection.CreateCommand();
            meta.Transaction = tx;
            meta.CommandText = "INSERT INTO catalog_meta(key,value) VALUES('etag',$etag) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            meta.Parameters.AddWithValue("$etag", etag);
            meta.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private void Upsert(JsonElement payload, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO catalog_builds(id,game_id,store,version_label,build_id,depot_ids,manifest_ids,branch,availability_status,source,source_reference,observed_at,verified_at,confidence,provenance)
            VALUES($id,$game,$store,$version,$build,$depot,$manifest,$branch,$status,$source,$ref,$observed,$verified,$confidence,$provenance)
            ON CONFLICT(id) DO UPDATE SET game_id=excluded.game_id,store=excluded.store,version_label=excluded.version_label,build_id=excluded.build_id,depot_ids=excluded.depot_ids,manifest_ids=excluded.manifest_ids,branch=excluded.branch,availability_status=excluded.availability_status,source=excluded.source,source_reference=excluded.source_reference,observed_at=excluded.observed_at,verified_at=excluded.verified_at,confidence=excluded.confidence,provenance=excluded.provenance
            """;
        cmd.Parameters.AddWithValue("$id", payload.GetProperty("id").GetString()!);
        cmd.Parameters.AddWithValue("$game", payload.GetProperty("gameId").GetString()!);
        cmd.Parameters.AddWithValue("$store", payload.GetProperty("store").GetString()!);
        cmd.Parameters.AddWithValue("$version", payload.GetProperty("versionLabel").GetString()!);
        cmd.Parameters.AddWithValue("$build", payload.GetProperty("buildId").GetString()!);
        cmd.Parameters.AddWithValue("$depot", payload.GetProperty("depotIds").GetRawText());
        cmd.Parameters.AddWithValue("$manifest", payload.GetProperty("manifestIds").GetRawText());
        cmd.Parameters.AddWithValue("$branch", payload.GetProperty("branch").GetString()!);
        cmd.Parameters.AddWithValue("$status", payload.GetProperty("availabilityStatus").GetString()!);
        cmd.Parameters.AddWithValue("$source", payload.GetProperty("source").GetString()!);
        cmd.Parameters.AddWithValue("$ref", payload.GetProperty("sourceReference").GetString()!);
        cmd.Parameters.AddWithValue("$observed", payload.TryGetProperty("observedAt", out var obs) && obs.ValueKind != JsonValueKind.Null ? obs.GetString()! : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$verified", payload.TryGetProperty("verifiedAt", out var ver) && ver.ValueKind != JsonValueKind.Null ? ver.GetString()! : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$confidence", payload.GetProperty("confidence").GetDouble());
        cmd.Parameters.AddWithValue("$provenance", payload.GetProperty("provenance").GetString()!);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<CatalogBuild> GetBuilds(string gameId, string? store = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,game_id,store,version_label,build_id,depot_ids,manifest_ids,branch,availability_status,source,source_reference,observed_at,verified_at,confidence,provenance FROM catalog_builds WHERE game_id=$game AND ($store IS NULL OR store=$store) ORDER BY id";
        cmd.Parameters.AddWithValue("$game", gameId);
        cmd.Parameters.AddWithValue("$store", (object?)store ?? DBNull.Value);
        var results = new List<CatalogBuild>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CatalogBuild(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                JsonSerializer.Deserialize<string[]>(reader.GetString(5))!, JsonSerializer.Deserialize<string[]>(reader.GetString(6))!,
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
                reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
                reader.GetDouble(13), reader.GetString(14)));
        }
        return results;
    }

    // Download proposals exclude unavailable/unknown builds (spec §7.12.4).
    public IReadOnlyList<CatalogBuild> GetDownloadProposals(string gameId) =>
        GetBuilds(gameId).Where(b => b.AvailabilityStatus is "known" or "verified" or "downloadable").ToArray();

    public void MarkSynced() => SetMeta("lastSync", DateTimeOffset.UtcNow.ToString("O"));

    public void Dispose() => connection.Dispose();
}

public sealed class CatalogClient
{
    private readonly ICatalogTransport transport;
    private readonly CatalogStore store;

    public CatalogClient(ICatalogTransport transport, CatalogStore store)
    {
        this.transport = transport;
        this.store = store;
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var since = store.Revision;
        while (true)
        {
            // The changes endpoint is revision-based; the snapshot ETag does not
            // apply to it, so do not send If-None-Match here.
            var response = await transport.GetAsync($"/catalog/changes?since={since}&limit=100", null, cancellationToken);
            if (response.StatusCode == 304) { store.MarkSynced(); return; }
            if (response.StatusCode == 410)
            {
                await FullSyncAsync(cancellationToken);
                return;
            }
            if (response.StatusCode != 200 || response.Body is null)
                throw new HttpRequestException($"catalog sync failed: {response.StatusCode}");
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            store.ApplyChanges(root.GetProperty("changes"), since, null);
            since = root.GetProperty("nextSince").GetInt64();
            store.SetMeta("revision", since.ToString());
            if (!root.GetProperty("hasMore").GetBoolean()) break;
        }
        store.MarkSynced();
    }

    public async Task FullSyncAsync(CancellationToken cancellationToken = default)
    {
        // Accumulate every page before committing: an interrupted download must
        // not leave a partially-replaced catalog or advance the revision early.
        string? cursor = null;
        long revision = 0;
        string? etag = null;
        var all = new List<JsonElement>();
        while (true)
        {
            var path = "/catalog/index?limit=100" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var response = await transport.GetAsync(path, null, cancellationToken);
            if (response.StatusCode != 200 || response.Body is null)
                throw new HttpRequestException($"catalog snapshot failed: {response.StatusCode}");
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            revision = root.GetProperty("revision").GetInt64();
            etag = response.ETag;
            foreach (var item in root.GetProperty("items").EnumerateArray())
                all.Add(item.Clone());
            cursor = root.TryGetProperty("nextCursor", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;
            if (cursor is null) break;
        }
        var items = JsonSerializer.SerializeToElement(all);
        store.ReplaceAll(items, revision, etag);
        store.MarkSynced();
    }
}
