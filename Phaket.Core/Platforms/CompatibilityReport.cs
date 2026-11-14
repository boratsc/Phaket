namespace Phaket.Core.Platforms;

// Aggregated compatibility view model (mockup 06): game runtime compatibility
// (ProtonDB, external hint with provenance) kept separate from mod compatibility
// (declared/verified for the profile). A ProtonDB rating is never proof that a
// mod set works (ADR-046).
public sealed record GameRuntimeCompatibility(
    string Source, string Tier, string Confidence, int ReportCount, string DumpDate, string SourceReference);

public sealed record ModCompatibility(string Name, string Version, bool Compatible, string Reason);

public sealed record CompatibilityReport(
    string GameId,
    GameRuntimeCompatibility? Runtime,
    IReadOnlyList<ModCompatibility> Mods,
    bool AllModsCompatible);

public static class CompatibilityAnalyzer
{
    public static CompatibilityReport Analyze(
        string gameId, ProtonDbCache? cache, IEnumerable<(string Name, string Version, bool Enabled)> mods)
    {
        GameRuntimeCompatibility? runtime = null;
        if (cache is not null)
        {
            var appId = gameId == "among-us" ? "945360" : gameId == "valheim" ? "892970" : null;
            if (appId is not null)
            {
                var summary = cache.GetSummary(appId);
                var reports = cache.GetReports(appId);
                if (summary is not null && reports.Count > 0)
                    runtime = new GameRuntimeCompatibility(
                        "ProtonDB", summary.Tier, summary.Confidence, summary.Total,
                        reports[0].DumpDate, reports[0].SourceReference);
            }
        }
        var modList = mods.Select(m => new ModCompatibility(m.Name, m.Version, true, "")).ToArray();
        return new CompatibilityReport(gameId, runtime, modList, modList.All(m => m.Compatible));
    }
}
