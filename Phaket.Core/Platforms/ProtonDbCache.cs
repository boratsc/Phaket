using System.Text.Json;
using Microsoft.Data.Sqlite;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

// ProtonDB knowledge import per the approved decision (Outline, PAK-11):
// official ODbL dumps only; report content is UNTRUSTED DATA with provenance.
// Nothing here executes report content; launchOptions are stored verbatim as
// hints for the typed Launch Strategy pipeline (dry-run + acceptance).
public sealed record ProtonDbReport(
    string AppId, string Title, string? ProtonVersion, string Variant, string Verdict,
    string? Notes, string? LaunchOptions, long Timestamp, string? Os, string? Gpu);

public sealed record CachedProtonDbReport(
    ProtonDbReport Report, string GameId, string EditionId,
    string Source, string SourceReference, string DumpDate, DateTimeOffset FetchedAt, string License);

public sealed record ProtonDbSummary(string AppId, int Total, int Positive, string Tier, string Confidence);

public sealed class ProtonDbCache : IDisposable
{
    public const string License = "ODbL-1.0";
    private readonly SqliteConnection connection;

    public ProtonDbCache(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS protondb_reports(
                app_id TEXT NOT NULL, game_id TEXT NOT NULL, edition_id TEXT NOT NULL,
                title TEXT NOT NULL, proton_version TEXT, variant TEXT NOT NULL, verdict TEXT NOT NULL,
                notes TEXT, launch_options TEXT, ts INTEGER NOT NULL, os TEXT, gpu TEXT,
                source TEXT NOT NULL, source_reference TEXT NOT NULL, dump_date TEXT NOT NULL,
                fetched_at TEXT NOT NULL, license TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_protondb_app ON protondb_reports(app_id);
            """;
        cmd.ExecuteNonQuery();
    }

    // Imports a dump stream (reports_piiremoved.json array), filtering to the
    // requested Steam appIds. Steam reports map to the steam edition only —
    // never to Epic (spec §7.13.2).
    public int Import(Stream dumpStream, IReadOnlyDictionary<string, GameDefinition> definitionsBySteamAppId,
        string sourceReference, string dumpDate)
    {
        using var doc = JsonDocument.Parse(dumpStream);
        var fetchedAt = DateTimeOffset.UtcNow;
        var count = 0;
        using var tx = connection.BeginTransaction();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var appId = element.GetProperty("app").GetProperty("steam").GetProperty("appId").GetString();
            if (appId is null || !definitionsBySteamAppId.TryGetValue(appId, out var definition)) continue;
            var responses = element.GetProperty("responses");
            var report = new ProtonDbReport(
                appId,
                element.GetProperty("app").GetProperty("title").GetString() ?? "",
                responses.TryGetProperty("protonVersion", out var pv) ? pv.GetString() : null,
                responses.TryGetProperty("variant", out var variant) ? variant.GetString() ?? "official" : "official",
                responses.TryGetProperty("verdict", out var verdict) ? verdict.GetString() ?? "unknown" : "unknown",
                responses.TryGetProperty("notes", out var notes) ? notes.GetProperty("verdict").GetString() : null,
                responses.TryGetProperty("launchOptions", out var lo) ? lo.GetString() : null,
                element.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : 0,
                element.TryGetProperty("system", out var system) && system.TryGetProperty("os", out var os) ? os.GetString() : null,
                element.TryGetProperty("system", out var system2) && system2.TryGetProperty("gpu", out var gpu) ? gpu.GetString() : null);
            Insert(report, definition.Slug, $"{definition.Slug}-steam", sourceReference, dumpDate, fetchedAt, tx);
            count++;
        }
        tx.Commit();
        return count;
    }

    private void Insert(ProtonDbReport report, string gameId, string editionId,
        string sourceReference, string dumpDate, DateTimeOffset fetchedAt, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO protondb_reports(app_id,game_id,edition_id,title,proton_version,variant,verdict,notes,launch_options,ts,os,gpu,source,source_reference,dump_date,fetched_at,license)
            VALUES($app,$game,$edition,$title,$pv,$variant,$verdict,$notes,$lo,$ts,$os,$gpu,$source,$ref,$dump,$fetched,$license)
            """;
        cmd.Parameters.AddWithValue("$app", report.AppId);
        cmd.Parameters.AddWithValue("$game", gameId);
        cmd.Parameters.AddWithValue("$edition", editionId);
        cmd.Parameters.AddWithValue("$title", report.Title);
        cmd.Parameters.AddWithValue("$pv", (object?)report.ProtonVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$variant", report.Variant);
        cmd.Parameters.AddWithValue("$verdict", report.Verdict);
        cmd.Parameters.AddWithValue("$notes", (object?)report.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lo", (object?)report.LaunchOptions ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", report.Timestamp);
        cmd.Parameters.AddWithValue("$os", (object?)report.Os ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gpu", (object?)report.Gpu ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", "protondb");
        cmd.Parameters.AddWithValue("$ref", sourceReference);
        cmd.Parameters.AddWithValue("$dump", dumpDate);
        cmd.Parameters.AddWithValue("$fetched", fetchedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$license", License);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<CachedProtonDbReport> GetReports(string appId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT app_id,title,proton_version,variant,verdict,notes,launch_options,ts,os,gpu,game_id,edition_id,source,source_reference,dump_date,fetched_at,license FROM protondb_reports WHERE app_id=$app ORDER BY ts DESC";
        cmd.Parameters.AddWithValue("$app", appId);
        var results = new List<CachedProtonDbReport>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var report = new ProtonDbReport(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9));
            results.Add(new CachedProtonDbReport(report, reader.GetString(10), reader.GetString(11),
                reader.GetString(12), reader.GetString(13), reader.GetString(14),
                DateTimeOffset.Parse(reader.GetString(15)), reader.GetString(16)));
        }
        return results;
    }

    // Offline summaries carry the dump date — never "current confirmation".
    public ProtonDbSummary? GetSummary(string appId)
    {
        var reports = GetReports(appId);
        if (reports.Count == 0) return null;
        var positive = reports.Count(r => r.Report.Verdict is "yes" or "true");
        var ratio = (double)positive / reports.Count;
        var tier = ratio >= 0.9 ? "platinum" : ratio >= 0.7 ? "gold" : ratio >= 0.5 ? "silver" : ratio >= 0.3 ? "bronze" : "borked";
        var confidence = reports.Count >= 50 ? "strong" : reports.Count >= 10 ? "moderate" : "weak";
        return new ProtonDbSummary(appId, reports.Count, positive, tier, confidence);
    }

    public void Dispose() => connection.Dispose();
}
