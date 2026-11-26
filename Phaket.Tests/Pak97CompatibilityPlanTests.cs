using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-97: the client side of the SusModder full×dll matrix. The catalog snapshot fills the
// cache, the plan reads it back, NW blocks the plan and NT reaches the plan warnings.
// Fixtures only: no network, no real game installs.
public sealed class Pak97CompatibilityPlanTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        return buffer.ToArray();
    }

    private static ThunderstoreVersion SusVersion(string version, string url, byte[] bytes, string packaging, string? amongVersion) =>
        new(version, "SusModder-" + packaging + "-" + version, url, bytes.Length, DateTimeOffset.UnixEpoch, [], true,
            [new ModArtifact(url, "x86", ["steam", "itch"], Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length,
                "susmodder-cdn", packaging + ".zip")],
            packaging, amongVersion, "x86", "susmodder", null);

    // A full mod and a dll, keyed by the upstream external ids the matrix uses.
    private static (ThunderstoreMod Mod, ThunderstoreVersion Version) Entry(string id, string externalId, string name,
        string url, byte[] bytes, string packaging, string? amongVersion)
    {
        var version = SusVersion(packaging == "full" ? "1.7.3" : "1.0.0", url, bytes, packaging, amongVersion);
        return (new ThunderstoreMod(id, GameId, "SusModder", name, "SusModder-" + name, "desc", "SusModder",
            "https://example.test", null, false, DateTimeOffset.UnixEpoch, version, Packaging: packaging,
            AmongVersion: amongVersion, Source: "susmodder", ExternalId: externalId), version);
    }

    private const string GameId = "among-us";
    private const string SusModder = "SusModder";

    private sealed record Pair(string FullId, string DllId, string Status);

    private sealed class Catalog(ThunderstoreMod[] mods, Pair[] matrix) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            if (path.StartsWith("/mods/changes?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, "{\"revision\":1,\"changes\":[],\"reset\":true}"));
            if (path.StartsWith("/mods/index?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, JsonSerializer.Serialize(
                    new { revision = 1, mods, compatibility = matrix, nextCursor = (string?)null }, Json)));
            if (path.StartsWith("/mods/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/mods/".Length..]);
                var mod = mods.FirstOrDefault(item => item.Id == id);
                return Task.FromResult(mod is null
                    ? new CatalogResponse(404, null, null)
                    : new CatalogResponse(200, null, JsonSerializer.Serialize(new { mod, versions = new[] { mod.Latest } }, Json)));
            }
            return Task.FromResult(new CatalogResponse(404, null, null));
        }
    }

    // The loader artifact is pinned by a fixed hash in Core, so a hand-built ZIP can only
    // fail it; a non-matching source downloads through the real transport (network).
    private sealed class Packages(IReadOnlyDictionary<string, byte[]> files) : IPackageTransport
    {
        private readonly HttpPackageTransport fallback = new();
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken) => files.TryGetValue(source, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : fallback.OpenReadAsync(source, cancellationToken);
    }

    private sealed class Vanilla : IBaseGameSource
    {
        public Task<BaseGamePrepareResult> TryPrepareAsync(string store, string version, string? manifestId,
            string targetDirectory, CancellationToken ct = default)
        {
            Directory.CreateDirectory(targetDirectory);
            File.WriteAllText(Path.Combine(targetDirectory, "Among Us.exe"), "provisioned");
            return Task.FromResult(BaseGamePrepareResult.Ok);
        }
    }

    private const string FullUrl = "https://susmodder-cdn.ovh/mods/mira/1.7.3.zip";
    private const string DllUrl = "https://susmodder-cdn.ovh/mods/submerged/1.0.0.zip";

    private static ModpackDefinition Pack(string dllId, string gameId) => new()
    {
        Id = "pak97-pack", Name = "Compatibility pack", Author = "Test", Version = "1", GameId = gameId,
        Items =
        [
            new ModpackMod { Name = "SusModder-Mira", Version = "1.7.3", Source = "susmodder", ModId = "13" },
            new ModpackMod { Name = "SusModder-Submerged", Version = "1.0.0", Source = "susmodder", ModId = dllId },
        ]
    };

    private static (ModpackInstallService Service, string Root) Fixture(string dllExternalId, Pair[] matrix)
    {
        var root = Directory.CreateTempSubdirectory("pak97-compat-").FullName;
        var fullBytes = Zip(("BepInEx/plugins/TownOfUsMira.dll", "full"), ("Among Us_Data/sharedassets0.assets", "vanilla"));
        var dllBytes = Zip(("BepInEx/plugins/Submerged/Submerged.dll", "dll"));
        var full = Entry("sus-mira", "13", "Mira", FullUrl, fullBytes, "full", "2026-8-18").Mod;
        var dll = Entry("sus-submerged", dllExternalId, "Submerged", DllUrl, dllBytes, "dll", "ToU").Mod;
        var files = new Dictionary<string, byte[]> { [FullUrl] = fullBytes, [DllUrl] = dllBytes };
        var service = new ModpackInstallService(root, catalogTransport: new Catalog([full, dll], matrix), packageTransport: new Packages(files))
        {
            InstallServiceFactory = (client, profiles, credentials, prompt) =>
                new ModInstallService(root, client, new Packages(files), profiles: profiles, baseGameSource: new Vanilla(),
                    steamCredentials: credentials, steamPrompt: prompt),
        };
        return (service, root);
    }

    // A minimal PE32 (x86) header: the install's own arch probe is then a fact, not a guess.
    private static void WritePe(string path)
    {
        var bytes = new byte[512];
        void U16(int o, ushort v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o), v);
        void U32(int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(o), v);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, 0x14c);
        U16(0x94, 224);
        U16(0x98, 0x10b);
        U32(0x98 + 92, 16);
        File.WriteAllBytes(path, bytes);
    }

    private static GameCatalogItem Game(string root)
    {
        var gameRoot = Path.Combine(root, "Among Us");
        Directory.CreateDirectory(gameRoot);
        WritePe(Path.Combine(gameRoot, "Among Us.exe"));
        var game = new GameCatalogItem(GameDefinitions.LoadAll().Single(item => item.Slug == GameId), "", "");
        game.Installations.Add(new SteamInstallation(new Installation("test", "steam-edition", gameRoot),
            new SteamAppManifest("945360", "Among Us", "1", "public", "24302054", "Among Us", 1),
            new SteamLibrary(root, false), GameAvailability.Installed));
        return game;
    }

    [Fact]
    public async Task Not_working_pair_blocks_the_plan()
    {
        var (service, root) = Fixture("5", [new("13", "5", "NW")]);
        try
        {
            var plan = await service.PrepareAsync(Game(root), Pack("5", GameId), TestContext.Current.CancellationToken);
            Assert.False(plan.CanInstall);
            Assert.Equal(ErrorCodes.ModCompatibilityBlocked, plan.BlockReason);
            Assert.Equal("Mod SusModder-Mira i dll SusModder-Submerged są oznaczone w tabeli zgodności jako niedziałające razem. Usuń jedno z nich z planu.",
                ErrorMessages.Format(plan.BlockReason!, "pl-PL", new Dictionary<string, string>
                {
                    ["full"] = "SusModder-Mira", ["dll"] = "SusModder-Submerged"
                }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Untested_pair_reaches_the_plan_warnings()
    {
        var (service, root) = Fixture("5", [new("13", "5", "NT")]);
        try
        {
            var plan = await service.PrepareAsync(Game(root), Pack("5", GameId), TestContext.Current.CancellationToken);
            Assert.True(plan.CanInstall, plan.BlockReason);
            Assert.Contains("MOD_COMPATIBILITY_UNTESTED|SusModder-Mira|SusModder-Submerged", plan.Warnings);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("pl-PL", "Mod SusModder-Mira i dll SusModder-Submerged nie były testowane razem. Instalacja zostanie wykonana, ale połączenie może nie działać.")]
    [InlineData("en", "The mod SusModder-Mira and the dll SusModder-Submerged have not been tested together. The installation will proceed, but the combination may not work.")]
    public void Untested_warning_renders_in_both_languages(string language, string expected)
    {
        var parts = "MOD_COMPATIBILITY_UNTESTED|SusModder-Mira|SusModder-Submerged".Split('|');
        var text = ErrorMessages.Format(parts[0], language, new Dictionary<string, string> { ["full"] = parts[1], ["dll"] = parts[2] });
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("F")]
    [InlineData("W")]
    public async Task Favourable_pair_leaves_the_plan_silent(string status)
    {
        var (service, root) = Fixture("5", [new("13", "5", status)]);
        try
        {
            var plan = await service.PrepareAsync(Game(root), Pack("5", GameId), TestContext.Current.CancellationToken);
            Assert.True(plan.CanInstall, plan.BlockReason);
            Assert.DoesNotContain(plan.Warnings, warning => warning.StartsWith("MOD_COMPATIBILITY", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Pair_absent_from_the_matrix_leaves_the_plan_silent()
    {
        // The plan's dll is external id 6; the matrix only names 5, so the pair is unknown,
        // not "untested", and says nothing.
        var (service, root) = Fixture("6", [new("13", "5", "NW")]);
        try
        {
            var plan = await service.PrepareAsync(Game(root), Pack("6", GameId), TestContext.Current.CancellationToken);
            Assert.True(plan.CanInstall, plan.BlockReason);
            Assert.DoesNotContain(plan.Warnings, warning => warning.StartsWith("MOD_COMPATIBILITY", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Pair_with_a_missing_side_leaves_the_plan_silent()
    {
        // Only the dll is in the composition: a full×dll pair cannot be evaluated, so even
        // a matrix row that names this dll as NW changes nothing.
        var (service, root) = Fixture("5", [new("13", "5", "NW")]);
        try
        {
            var pack = new ModpackDefinition
            {
                Id = "pak97-dll-only", Name = "Dll only", Author = "Test", Version = "1", GameId = GameId,
                Items = [new ModpackMod { Name = "SusModder-Submerged", Version = "1.0.0", Source = "susmodder", ModId = "5" }]
            };
            var plan = await service.PrepareAsync(Game(root), pack, TestContext.Current.CancellationToken);
            Assert.True(plan.CanInstall, plan.BlockReason);
            Assert.DoesNotContain(plan.Warnings, warning => warning.StartsWith("MOD_COMPATIBILITY", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}