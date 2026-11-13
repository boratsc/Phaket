using Microsoft.Data.Sqlite;

namespace Phaket.Core.Platforms;

// Community compatibility reports (spec §8.7): every user can report a result,
// trusted and untrusted reports are kept separate, and each report is tied to
// specific game/mod versions + environment. Reports are evidence, not votes;
// a positive report never auto-promotes to "verified". Local PoC store.
public enum CompatibilityResult { Working, Partial, Broken, Unknown }

public enum ReporterTrust { Untrusted, Verified, TrustedTester, Author, Automated }

public sealed record CompatibilityReportRecord(
    string Id, string GameId, string GameVersion, string Os, string Runtime,
    string ModId, string ModVersion, CompatibilityResult Result, ReporterTrust Trust,
    DateTimeOffset ObservedAt, string? Evidence, string? Comment);

public sealed class CompatibilityReportStore : IDisposable
{
    private readonly SqliteConnection connection;

    public CompatibilityReportStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS compatibility_reports(
                id TEXT PRIMARY KEY, game_id TEXT NOT NULL, game_version TEXT NOT NULL,
                os TEXT NOT NULL, runtime TEXT NOT NULL, mod_id TEXT NOT NULL, mod_version TEXT NOT NULL,
                result TEXT NOT NULL, trust TEXT NOT NULL, observed_at TEXT NOT NULL,
                evidence TEXT, comment TEXT);
            CREATE INDEX IF NOT EXISTS idx_compat_game ON compatibility_reports(game_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Add(CompatibilityReportRecord report)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO compatibility_reports(id,game_id,game_version,os,runtime,mod_id,mod_version,result,trust,observed_at,evidence,comment)
            VALUES($id,$game,$gv,$os,$rt,$mod,$mv,$result,$trust,$obs,$ev,$comment)
            """;
        cmd.Parameters.AddWithValue("$id", report.Id);
        cmd.Parameters.AddWithValue("$game", report.GameId);
        cmd.Parameters.AddWithValue("$gv", report.GameVersion);
        cmd.Parameters.AddWithValue("$os", report.Os);
        cmd.Parameters.AddWithValue("$rt", report.Runtime);
        cmd.Parameters.AddWithValue("$mod", report.ModId);
        cmd.Parameters.AddWithValue("$mv", report.ModVersion);
        cmd.Parameters.AddWithValue("$result", report.Result.ToString());
        cmd.Parameters.AddWithValue("$trust", report.Trust.ToString());
        cmd.Parameters.AddWithValue("$obs", report.ObservedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$ev", (object?)report.Evidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$comment", (object?)report.Comment ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<CompatibilityReportRecord> GetReports(string gameId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,game_id,game_version,os,runtime,mod_id,mod_version,result,trust,observed_at,evidence,comment FROM compatibility_reports WHERE game_id=$game ORDER BY observed_at DESC";
        cmd.Parameters.AddWithValue("$game", gameId);
        var results = new List<CompatibilityReportRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CompatibilityReportRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                Enum.Parse<CompatibilityResult>(reader.GetString(7)), Enum.Parse<ReporterTrust>(reader.GetString(8)),
                DateTimeOffset.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return results;
    }

    // Summary keeps trusted and untrusted counts separate (never merged into a
    // single score). A result is "confirmed" only with a trusted positive report.
    public (int Trusted, int Untrusted, int Positive) Summarize(string gameId)
    {
        var reports = GetReports(gameId);
        var trusted = reports.Count(r => r.Trust is ReporterTrust.TrustedTester or ReporterTrust.Author or ReporterTrust.Verified);
        var untrusted = reports.Count - trusted;
        var positive = reports.Count(r => r.Result == CompatibilityResult.Working);
        return (trusted, untrusted, positive);
    }

    public void Dispose() => connection.Dispose();
}
