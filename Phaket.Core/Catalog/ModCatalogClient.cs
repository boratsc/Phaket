using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Phaket.Core.Catalog;

public sealed record ThunderstoreVersion(string VersionNumber, string FullName, string DownloadUrl,
    long? FileSize, DateTimeOffset DateCreated, IReadOnlyList<string> Dependencies, bool IsActive = true,
    // SusModder catalog fields (PAK-95); all optional so Thunderstore records and
    // older caches deserialize unchanged.
    IReadOnlyList<ModArtifact>? Artifacts = null,
    string? Packaging = null,
    string? AmongVersion = null,
    string? Arch = null,
    string? Source = null,
    string? Sha256 = null,
    // Server-reported availability (PAK-101). Thunderstore and older caches carry none.
    string? Availability = null)
{
    // A version the source no longer serves: no usable download target. A version with an
    // artifacts list is usable only when one artifact has a non-empty https URL plus either
    // the declared sha256 the installer verifies or, for Thunderstore, a declared size; a
    // placeholder row must not look installable.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUnavailable => string.Equals(Availability, "unavailable", StringComparison.OrdinalIgnoreCase)
        || (Artifacts is { Count: > 0 } ? !Artifacts.Any(IsUsableArtifact) : string.IsNullOrWhiteSpace(DownloadUrl));

    // PAK-126: the Thunderstore v1 API publishes no sha256, so the server writes artifacts
    // with sha256=null. Such an artifact is still installable: the downloader enforces the
    // declared size, the ZIP signature is validated, the plan warns
    // THUNDERSTORE_HASH_UNVERIFIED and the registry records ArtifactIntegrity=unverified.
    // This only trusts the Thunderstore source; any other hash-less artifact stays unusable.
    private static bool IsUsableArtifact(ModArtifact artifact) =>
        !string.IsNullOrWhiteSpace(artifact.Url)
        && Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (!string.IsNullOrWhiteSpace(artifact.Sha256)
            || (string.Equals(artifact.Source, "thunderstore", StringComparison.OrdinalIgnoreCase)
                && artifact.SizeBytes is > 0));
    [System.Text.Json.Serialization.JsonIgnore] public bool IsAvailable => !IsUnavailable;
}
public sealed record ThunderstoreMod(string Id, string GameId, string Namespace, string Name, string FullName,
    string Description, string Owner, string PackageUrl, string? IconUrl, bool IsDeprecated,
    DateTimeOffset DateUpdated, ThunderstoreVersion Latest,
    string? License = null, bool? RepoAvailable = null,
    // SusModder catalog fields sent at mod level (PAK-101); Thunderstore records leave them null.
    string? Packaging = null, string? AmongVersion = null, string? Source = null, string? ExternalId = null)
{
    // Catalog badges and details (PAK-101): display keys stay language-independent.
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourceKey => string.IsNullOrWhiteSpace(Source) ? "Catalog.Source.thunderstore" : "Catalog.Source." + Source;
    [System.Text.Json.Serialization.JsonIgnore] public bool HasLicense => !string.IsNullOrWhiteSpace(License);
    [System.Text.Json.Serialization.JsonIgnore] public bool HasNoLicense => !HasLicense;
    [System.Text.Json.Serialization.JsonIgnore] public bool RepoUnavailable => RepoAvailable == false;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsFullPackage => Packaging == "full";
    [System.Text.Json.Serialization.JsonIgnore]
    public string PackagingKey => Packaging switch { "full" => "Packaging.Full", "dll" => "Packaging.Dll", _ => "" };
    [System.Text.Json.Serialization.JsonIgnore] public bool HasPackaging => PackagingKey.Length > 0;
    [System.Text.Json.Serialization.JsonIgnore] public bool FollowsToU => string.Equals(AmongVersion, "ToU", StringComparison.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore] public bool HasPinnedAmongVersion => !string.IsNullOrWhiteSpace(AmongVersion) && !FollowsToU;
}

// One downloadable variant of a mod version. Stores/arch narrow which
// installation facts the artifact may serve; null/empty means "any".
public sealed record ModArtifact(string Url, string? Arch = null, IReadOnlyList<string>? Stores = null,
    string? Sha256 = null, long? SizeBytes = null, string? Source = null, string? AssetName = null);
public sealed record ModIndexPage(long Revision, DateTimeOffset GeneratedAt,
    IReadOnlyList<ThunderstoreMod> Mods, string? NextCursor);
// PAK-97: upstream SusModder full×dll pair status as the server serves it (F, W, NT, NW).
public sealed record ModCompatibility(string FullId, string DllId, string Status);
public sealed record ThunderstoreModDetails(ThunderstoreMod Mod, IReadOnlyList<ThunderstoreVersion> Versions,
    DateTimeOffset FetchedAt);

public sealed class ModCatalogStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection db;
    public ModCatalogStore(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        // Same busy wait as CatalogStore: the mod cache and the build registry share one
// file, and a concurrent writer must not surface as a raw "database is locked".
        db = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=5"); db.Open();
        // The `data` JSON is the row schema: optional catalog fields (PAK-95:
        // packaging, amongVersion, arch, artifacts, license) need no ALTER TABLE,
        // and caches written before them deserialize with nulls.
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS mods(id TEXT PRIMARY KEY, game_id TEXT NOT NULL, full_name TEXT NOT NULL, data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS mod_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS mod_details(id TEXT PRIMARY KEY,data TEXT NOT NULL,fetched_at TEXT NOT NULL); CREATE TABLE IF NOT EXISTS mod_compat(game_id TEXT NOT NULL, full_id TEXT NOT NULL, dll_id TEXT NOT NULL, status TEXT NOT NULL, PRIMARY KEY(game_id, full_id, dll_id));";
        cmd.ExecuteNonQuery();
    }
    public long Revision => long.TryParse(Meta("revision"), out var n) ? n : 0;
    internal long GameRevision(string gameId) => long.TryParse(Meta($"rev:{gameId}"), out var n) ? n : 0;
    internal long DeltaCursor => Math.Min(GameRevision("among-us"), GameRevision("valheim"));
    public DateTimeOffset? LastSync => DateTimeOffset.TryParse(Meta("lastSync"), out var d) ? d : null;
    public string? ETag => Meta("etag");
    internal bool HasSnapshot(string gameId) => Meta($"snapshotted:{gameId}") == "true";
    internal string? GameETag(string gameId) => Meta($"etag:{gameId}");
    internal void MarkSynced() { using var tx=db.BeginTransaction(); Set(db,tx,"lastSync",DateTimeOffset.UtcNow.ToString("O")); tx.Commit(); }
    private string? Meta(string key) { using var c=db.CreateCommand(); c.CommandText="SELECT value FROM mod_meta WHERE key=$k"; c.Parameters.AddWithValue("$k",key); return c.ExecuteScalar() as string; }
    private static void Set(SqliteConnection db, SqliteTransaction tx, string key, string value) { using var c=db.CreateCommand(); c.Transaction=tx; c.CommandText="INSERT INTO mod_meta VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value"; c.Parameters.AddWithValue("$k",key); c.Parameters.AddWithValue("$v",value); c.ExecuteNonQuery(); }
    public IReadOnlyList<ThunderstoreMod> GetMods(string gameId) { using var c=db.CreateCommand(); c.CommandText="SELECT data FROM mods WHERE game_id=$g ORDER BY full_name"; c.Parameters.AddWithValue("$g",gameId); using var r=c.ExecuteReader(); var items=new List<ThunderstoreMod>(); while(r.Read()) items.Add(JsonSerializer.Deserialize<ThunderstoreMod>(r.GetString(0), JsonOptions)!); return items; }
    // PAK-97: raw matrix rows for the game; an absent pair means "unknown", never "untested".
    public IReadOnlyList<ModCompatibility> GetCompatibility(string gameId) { using var c=db.CreateCommand(); c.CommandText="SELECT full_id,dll_id,status FROM mod_compat WHERE game_id=$g ORDER BY full_id,dll_id"; c.Parameters.AddWithValue("$g",gameId); using var r=c.ExecuteReader(); var items=new List<ModCompatibility>(); while(r.Read()) items.Add(new(r.GetString(0),r.GetString(1),r.GetString(2))); return items; }
    internal void Replace(string gameId,IEnumerable<ThunderstoreMod> mods,long revision,string? etag,IEnumerable<ModCompatibility>? compatibility=null) { using var tx=db.BeginTransaction(); using(var c=db.CreateCommand()){c.Transaction=tx;c.CommandText="DELETE FROM mods WHERE game_id=$game";c.Parameters.AddWithValue("$game",gameId);c.ExecuteNonQuery();}
        // PAK-97: the matrix is replaced with the snapshot it belongs to; a page set that
        // carried no matrix clears the game's pairs rather than serving stale ones.
        using(var c=db.CreateCommand()){c.Transaction=tx;c.CommandText="DELETE FROM mod_compat WHERE game_id=$game";c.Parameters.AddWithValue("$game",gameId);c.ExecuteNonQuery();}
        foreach(var pair in compatibility ?? []) using(var c=db.CreateCommand()){c.Transaction=tx;c.CommandText="INSERT OR REPLACE INTO mod_compat VALUES($game,$full,$dll,$status)";c.Parameters.AddWithValue("$game",gameId);c.Parameters.AddWithValue("$full",pair.FullId);c.Parameters.AddWithValue("$dll",pair.DllId);c.Parameters.AddWithValue("$status",pair.Status);c.ExecuteNonQuery();}
        foreach(var m in mods) Upsert(m,tx); Set(db,tx,"revision",revision.ToString()); Set(db,tx,$"rev:{gameId}",revision.ToString()); if(etag is not null) { Set(db,tx,"etag",etag); Set(db,tx,$"etag:{gameId}",etag); } Set(db,tx,$"snapshotted:{gameId}","true"); Set(db,tx,"lastSync",DateTimeOffset.UtcNow.ToString("O")); tx.Commit(); }
    internal void Apply(IEnumerable<ThunderstoreMod> mods,IEnumerable<string> tombstones,long revision) { using var tx=db.BeginTransaction(); foreach(var id in tombstones){using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="DELETE FROM mods WHERE id=$id";c.Parameters.AddWithValue("$id",id);c.ExecuteNonQuery();} foreach(var m in mods) Upsert(m,tx); Set(db,tx,"revision",revision.ToString()); Set(db,tx,"rev:among-us",revision.ToString()); Set(db,tx,"rev:valheim",revision.ToString()); Set(db,tx,"lastSync",DateTimeOffset.UtcNow.ToString("O")); tx.Commit(); }
    private void Upsert(ThunderstoreMod m,SqliteTransaction tx) { using var c=db.CreateCommand(); c.Transaction=tx;c.CommandText="INSERT INTO mods VALUES($id,$game,$name,$data) ON CONFLICT(id) DO UPDATE SET game_id=excluded.game_id,full_name=excluded.full_name,data=excluded.data"; c.Parameters.AddWithValue("$id",m.Id);c.Parameters.AddWithValue("$game",m.GameId);c.Parameters.AddWithValue("$name",m.FullName);c.Parameters.AddWithValue("$data",JsonSerializer.Serialize(m, JsonOptions));c.ExecuteNonQuery(); }
    internal ThunderstoreModDetails? GetDetails(string id) { using var c=db.CreateCommand(); c.CommandText="SELECT data,fetched_at FROM mod_details WHERE id=$id"; c.Parameters.AddWithValue("$id",id); using var r=c.ExecuteReader(); return r.Read()? JsonSerializer.Deserialize<ThunderstoreModDetails>(r.GetString(0), JsonOptions)! with { FetchedAt=DateTimeOffset.Parse(r.GetString(1)) }:null; }
    internal void SaveDetails(string id,ThunderstoreModDetails value) { using var c=db.CreateCommand(); c.CommandText="INSERT INTO mod_details VALUES($id,$data,$at) ON CONFLICT(id) DO UPDATE SET data=excluded.data,fetched_at=excluded.fetched_at"; c.Parameters.AddWithValue("$id",id); c.Parameters.AddWithValue("$data",JsonSerializer.Serialize(value, JsonOptions)); c.Parameters.AddWithValue("$at",value.FetchedAt.ToString("O")); c.ExecuteNonQuery(); }
    public void Dispose()=>db.Dispose();
}

public sealed class ModCatalogClient(ICatalogTransport transport, ModCatalogStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<ThunderstoreModDetails> GetModAsync(string id,CancellationToken cancellationToken=default)
    {
        if(string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Mod id is required",nameof(id));
        CatalogResponse response;
        try { response=await transport.GetAsync($"/mods/{Uri.EscapeDataString(id)}",null,cancellationToken); }
        catch(HttpRequestException)
        {
            var offline=store.GetDetails(id);
            if(offline is not null) return offline;
            throw new HttpRequestException("MOD_DETAILS_UNAVAILABLE");
        }
        if(response.StatusCode==200 && response.Body is not null)
        {
            using var doc=JsonDocument.Parse(response.Body); var root=doc.RootElement;
            var mod=root.GetProperty("mod").Deserialize<ThunderstoreMod>(JsonOptions)!;
            var versions=root.GetProperty("versions").Deserialize<IReadOnlyList<ThunderstoreVersion>>(JsonOptions)!;
            var value=new ThunderstoreModDetails(mod,versions,DateTimeOffset.UtcNow); store.SaveDetails(id,value); return value;
        }
        if(response.StatusCode==404) throw new HttpRequestException("MOD_NOT_FOUND",null, System.Net.HttpStatusCode.NotFound);
        var cached=store.GetDetails(id);
        if(cached is not null) return cached;
        throw new HttpRequestException("MOD_DETAILS_UNAVAILABLE");
    }
    public async Task SyncAsync(string gameId,CancellationToken cancellationToken=default)
    {
        if(gameId is not ("among-us" or "valheim")) throw new ArgumentException("Unsupported game id",nameof(gameId));
        if(!store.HasSnapshot(gameId)) { await Snapshot(gameId,cancellationToken); return; }
        var rev=store.DeltaCursor;
        var delta=await transport.GetAsync($"/mods/changes?since={rev}",null,cancellationToken);
        if(delta.StatusCode==410) { await Snapshot(gameId,cancellationToken); return; }
        if(delta.StatusCode==200 && delta.Body is not null)
        {
            // shortcut: /mods/changes carries no compatibility matrix (PAK-97 contract), so the
            // matrix refreshes only on a full snapshot; a status change waits for the next
            // incremental full refresh/etag miss. Add a matrix field to changes to close it.
            using var doc=JsonDocument.Parse(delta.Body); var root=doc.RootElement;
            if(root.GetProperty("reset").GetBoolean()) { await Snapshot(gameId,cancellationToken); return; }
            var upserts=new List<ThunderstoreMod>(); var deleted=new List<string>();
            foreach(var item in root.GetProperty("changes").EnumerateArray())
                if(item.GetProperty("op").GetString()=="tombstone") deleted.Add(item.GetProperty("id").GetString()!);
                else upserts.Add(item.GetProperty("mod").Deserialize<ThunderstoreMod>(JsonOptions)!);
            store.Apply(upserts,deleted,root.GetProperty("revision").GetInt64()); return;
        }
        throw new HttpRequestException($"mod catalog sync failed: {delta.StatusCode}");
    }
    private async Task Snapshot(string gameId,CancellationToken ct)
    {
        string? cursor=null,etag=null; long revision=0; var all=new List<ThunderstoreMod>();
        // PAK-97: pairs arrive once per full index (the contract has no separate matrix
        // endpoint); every page repeats or extends them, so later pages win per pair.
        var compatibility=new Dictionary<(string Full,string Dll),ModCompatibility>();
        // PAK-128: the server allows 500 rows per page; 100 pages were ~5x the requests
        // needed and tripped the per-IP limiter (429) during a full sync.
        do { var path=$"/mods/index?game={Uri.EscapeDataString(gameId)}&limit=500"+(cursor is null?"":$"&cursor={Uri.EscapeDataString(cursor)}"); var res=await transport.GetAsync(path,cursor is null?store.GameETag(gameId):null,ct);
            if(res.StatusCode==304) { if(cursor is null) store.MarkSynced(); return; } if(res.StatusCode!=200||res.Body is null) throw new HttpRequestException($"mod catalog snapshot failed: {res.StatusCode}");
            using var doc=JsonDocument.Parse(res.Body); var root=doc.RootElement; revision=root.GetProperty("revision").GetInt64(); etag=res.ETag;
            all.AddRange(root.GetProperty("mods").EnumerateArray().Select(x=>x.Deserialize<ThunderstoreMod>(JsonOptions)!)); cursor=root.TryGetProperty("nextCursor",out var next)&&next.ValueKind!=JsonValueKind.Null?next.GetString():null;
            if(root.TryGetProperty("compatibility",out var matrix)&&matrix.ValueKind==JsonValueKind.Array)
                foreach(var row in matrix.EnumerateArray())
                    if(row.TryGetProperty("fullId",out var full)&&row.TryGetProperty("dllId",out var dll)&&row.TryGetProperty("status",out var status)
                        && full.GetString() is { Length: > 0 } fullId && dll.GetString() is { Length: > 0 } dllId && status.GetString() is { Length: > 0 } raw)
                        compatibility[(fullId,dllId)]=new(fullId,dllId,raw);
        } while(cursor is not null);
        store.Replace(gameId,all,revision,etag,compatibility.Values);
    }
}
