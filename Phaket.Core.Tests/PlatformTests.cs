using System.Text;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

// PAK-11: ProtonDB import from ODbL dump format, provenance, offline cache,
// untrusted content handling. Fixture data only — no network.
public class ProtonDbTests
{
    private const string DumpFixture = """
        [
          {
            "app": { "steam": { "appId": "945360" }, "title": "Among Us" },
            "responses": {
              "variant": "official", "verdict": "yes",
              "notes": { "verdict": "Works with BepInEx via winhttp override" },
              "launchOptions": "WINEDLLOVERRIDES=\"winhttp.dll=n,b\" %command%"
            },
            "system": { "os": "Arch Linux", "gpu": "AMD Radeon RX 6700 XT" },
            "timestamp": 1730000000
          },
          {
            "app": { "steam": { "appId": "945360" }, "title": "Among Us" },
            "responses": { "variant": "ge", "protonVersion": "GE-Proton9-20", "verdict": "no" },
            "timestamp": 1730000100
          },
          {
            "app": { "steam": { "appId": "892970" }, "title": "Valheim" },
            "responses": { "variant": "official", "verdict": "yes" },
            "timestamp": 1730000200
          },
          {
            "app": { "steam": { "appId": "999999" }, "title": "Other Game" },
            "responses": { "variant": "official", "verdict": "yes" },
            "timestamp": 1730000300
          }
        ]
        """;

    private static readonly IReadOnlyDictionary<string, GameDefinition> Definitions =
        new Dictionary<string, GameDefinition>
        {
            ["945360"] = new("among-us", ["BepInEx/**"], new Dictionary<string, string> { ["steam"] = "945360" }),
            ["892970"] = new("valheim", ["BepInEx/**"], new Dictionary<string, string> { ["steam"] = "892970" }),
        };

    [Fact]
    public void ImportFiltersToRequestedGamesAndKeepsProvenance()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-protondb-").FullName;
        try
        {
            using var cache = new ProtonDbCache(Path.Combine(dir, "cache.db"));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DumpFixture));
            var count = cache.Import(stream, Definitions,
                "https://github.com/bdefore/protondb-data reports_nov1_2026.tar.gz", "2026-11-01");
            Assert.Equal(3, count); // other game filtered out

            var reports = cache.GetReports("945360");
            Assert.Equal(2, reports.Count);
            Assert.All(reports, r =>
            {
                Assert.Equal("among-us", r.GameId);
                Assert.Equal("among-us-steam", r.EditionId); // never epic
                Assert.Equal("protondb", r.Source);
                Assert.Equal("ODbL-1.0", r.License);
                Assert.Equal("2026-11-01", r.DumpDate);
                Assert.Contains("reports_nov1_2026", r.SourceReference);
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OfflineSummaryCarriesCountsNotCurrentConfirmation()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-protondb-").FullName;
        try
        {
            using var cache = new ProtonDbCache(Path.Combine(dir, "cache.db"));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DumpFixture));
            cache.Import(stream, Definitions, "fixture", "2026-11-01");
            var summary = cache.GetSummary("945360")!;
            Assert.Equal(2, summary.Total);
            Assert.Equal(1, summary.Positive);
            Assert.Equal("weak", summary.Confidence);
            Assert.Null(cache.GetSummary("000000"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LaunchOptionsAreStoredAsDataOnly()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-protondb-").FullName;
        try
        {
            using var cache = new ProtonDbCache(Path.Combine(dir, "cache.db"));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DumpFixture));
            cache.Import(stream, Definitions, "fixture", "2026-11-01");
            var report = cache.GetReports("945360").First(r => r.Report.LaunchOptions is not null);
            // The hint survives verbatim as data for the typed launch pipeline...
            Assert.Contains("%command%", report.Report.LaunchOptions!);
            // ...and the cache exposes no execution API (only reads/writes of data).
            Assert.Equal("protondb", report.Source);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CacheSurvivesReopen()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-protondb-").FullName;
        try
        {
            var path = Path.Combine(dir, "cache.db");
            using (var cache = new ProtonDbCache(path))
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DumpFixture));
                cache.Import(stream, Definitions, "fixture", "2026-11-01");
            }
            using var reopened = new ProtonDbCache(path);
            Assert.Equal(2, reopened.GetReports("945360").Count);
            Assert.Single(reopened.GetReports("892970"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

// PAK-13: Legendary detection reads facts only, never credentials.
public class LegendaryTests
{
    private static readonly GameDefinition AmongUsEpic = new("among-us", ["BepInEx/**"],
        new Dictionary<string, string> { ["epic"] = "AmongUsEpicId" });

    [Fact]
    public void DetectsInstalledGamesFromInstalledJson()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-legendary-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "installed.json"), """
                {
                  "AmongUsEpicId": {
                    "title": "Among Us",
                    "install_path": "/games/AmongUs",
                    "version": "2026.3.5",
                    "platform": "Windows"
                  }
                }
                """);
            // A credential file exists but must never be read or surfaced.
            File.WriteAllText(Path.Combine(dir, "user.json"), """{"access_token":"SECRET-TOKEN"}""");
            var adapter = new LegendaryAdapter([AmongUsEpic]);
            var games = adapter.DetectInstalled(dir);
            var game = Assert.Single(games);
            Assert.Equal("AmongUsEpicId", game.AppName);
            Assert.Equal("/games/AmongUs", game.InstallPath);
            Assert.Equal(["2026.3.5"], adapter.ProbeVersion(game));
            var installations = adapter.ToInstallations(games);
            Assert.Equal("among-us-epic", installations[0].EditionId);
            Assert.DoesNotContain("SECRET-TOKEN", string.Join(" ", games.Select(g => g.Title + g.InstallPath + g.Version)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MissingConfigYieldsNoInstallations()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-legendary-").FullName;
        try
        {
            var adapter = new LegendaryAdapter([AmongUsEpic]);
            Assert.Empty(adapter.DetectInstalled(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class FakeCli : ILegendaryCli
    {
        public IReadOnlyList<string>? LastArguments { get; private set; }
        public Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            LastArguments = arguments;
            return Task.FromResult("""{"result":"ok"}""");
        }
    }

    [Fact]
    public async Task LaunchDryRunUsesTypedArgumentsNotShell()
    {
        var cli = new FakeCli();
        var output = await cli.RunAsync(["launch", "AmongUsEpicId", "--dry-run", "--offline"], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Contains("ok", output);
        Assert.Equal(["launch", "AmongUsEpicId", "--dry-run", "--offline"], cli.LastArguments);
        // No shell metacharacters are interpreted: arguments are a typed list.
        Assert.DoesNotContain(cli.LastArguments!, a => a.Contains(';') || a.Contains('|'));
    }

    [Fact]
    public void MissingBinaryIsRejected()
    {
        Assert.Throws<PhaketException>(() => new LegendaryCli("/nonexistent/legendary"));
    }
}
