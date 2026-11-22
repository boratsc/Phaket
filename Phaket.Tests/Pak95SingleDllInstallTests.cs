using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Phaket.Core.Catalog;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Services;

namespace Phaket.Tests;

// PAK-95 follow-up: a catalog `dll` artifact published as a bare .dll is cached and
// copied without ever being opened as a ZIP. Sandbox only, no network, no real game.
public sealed class Pak95SingleDllInstallTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ModName = "LevelImposter";
    private const string DllUrl = "https://susmodder-cdn.ovh/mods/levelimposter/1.2.4/LevelImposter.dll";
    private const string ZipUrl = "https://susmodder-cdn.ovh/mods/submerged/1.0.0.zip";
    private static readonly byte[] DllBytes = Encoding.UTF8.GetBytes("MZ fake plugin bytes");

    private sealed class Catalog(ThunderstoreMod mod, ThunderstoreVersion version) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            if (path.StartsWith("/mods/changes?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, "{\"revision\":1,\"changes\":[],\"reset\":true}"));
            if (path.StartsWith("/mods/index?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null,
                    JsonSerializer.Serialize(new { revision = 1, mods = new[] { mod }, nextCursor = (string?)null }, Json)));
            if (path.StartsWith("/mods/", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null,
                    JsonSerializer.Serialize(new { mod, versions = new[] { version } }, Json)));
            return Task.FromResult(new CatalogResponse(404, null, null));
        }
    }

    private sealed class Packages(string url, byte[]? bytes) : IPackageTransport
    {
        public List<string> Requested { get; } = [];
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken)
        {
            Requested.Add(source);
            return source == url && bytes is not null
                ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
                : throw new InvalidOperationException($"unexpected download: {source}");
        }
    }

    private sealed class Receipts : IReceiptTransport
    {
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ThunderstoreVersion Version(byte[] bytes, string url, string? assetName) =>
        new("1.0.0", $"{ModName}-1.0.0", url, bytes.Length, DateTimeOffset.UnixEpoch, [], true,
            [new ModArtifact(url, "x64", ["steam", "msstore"], Convert.ToHexStringLower(SHA256.HashData(bytes)),
                bytes.Length, "susmodder-cdn", assetName)],
            "dll", "ToU", "x64", "susmodder", null);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("pak95-dll-install-").FullName;
        public Packages Packages { get; }
        public ModInstallService Service { get; }
        private readonly ModCatalogStore store;

        public Fixture(byte[] bytes, string url, string? assetName)
        {
            var version = Version(bytes, url, assetName);
            var mod = new ThunderstoreMod(ModName, "among-us", "Sus", ModName, ModName, "desc", "sus",
                "https://example.test", null, false, DateTimeOffset.UnixEpoch, version);
            Packages = new Packages(url, bytes);
            store = new ModCatalogStore(Path.Combine(Root, "catalog.db"));
            Service = new ModInstallService(Root, new ModCatalogClient(new Catalog(mod, version), store),
                Packages, new Receipts());
        }

        public void Dispose()
        {
            store.Dispose();
            Directory.Delete(Root, true);
        }
    }

    [Fact]
    public async Task SingleDllArtifactIsCachedAsFileAndCopiedIntoPlugins()
    {
        using var fixture = new Fixture(DllBytes, DllUrl, "LevelImposter.dll");
        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new("", "steam", true, "x64"), ct: TestContext.Current.CancellationToken);

        var gameRoot = Path.Combine(preparation.SandboxRoot, "game");
        var plugin = Path.Combine(gameRoot, "BepInEx", "plugins", "LevelImposter.dll");
        var plan = Assert.Single(preparation.Plans);
        var copy = Assert.Single(plan.Operations);
        Assert.Equal("file.copy", copy.Kind);
        Assert.Equal(plugin, copy.TargetPath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(DllBytes)),
            Assert.Single(plan.Verification).Sha256);
        Assert.True(File.Exists(Path.Combine(preparation.PackageRoots[0], "package")));
        Assert.False(File.Exists(Path.Combine(preparation.PackageRoots[0], "package.zip")));
        var preview = Assert.Single(preparation.Preview);
        Assert.Equal(["BepInEx/plugins/LevelImposter.dll"], preview.Files);
        Assert.Equal(DllBytes.Length, preview.SizeBytes);

        var outcome = await fixture.Service.ExecuteAsync(preparation, true, ct: TestContext.Current.CancellationToken);
        Assert.True(outcome.Success, outcome.ErrorCode);
        Assert.Equal(DllBytes, File.ReadAllBytes(plugin));
        Assert.DoesNotContain(fixture.Packages.Requested, url => url.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingleDllArtifactWithTraversalAssetNameIsRefusedBeforeDownload()
    {
        using var fixture = new Fixture(DllBytes, DllUrl, "../../evil.dll");
        var error = await Assert.ThrowsAsync<PhaketException>(() => fixture.Service.PrepareAsync("among-us",
            ModName, "1.0.0", "", new("", "steam", true, "x64"), ct: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ModSourceInvalid, error.Error.Code);
        Assert.Contains("unsafe dll file name", error.Error.Parameters["detail"]);
        Assert.Empty(fixture.Packages.Requested);
    }

    [Fact]
    public async Task ZipDllArtifactInstallPathIsUnchanged()
    {
        byte[] zip;
        using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(archive.CreateEntry("BepInEx/plugins/LevelImposter/LevelImposter.dll").Open()))
                writer.Write("mod");
            zip = buffer.ToArray();
        }
        using var fixture = new Fixture(zip, ZipUrl, "submerged.zip");
        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new("", "steam", true, "x64"), ct: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(preparation.PackageRoots[0], "package.zip")));
        Assert.Equal("archive.extract", Assert.Single(Assert.Single(preparation.Plans).Operations).Kind);

        var outcome = await fixture.Service.ExecuteAsync(preparation, true, ct: TestContext.Current.CancellationToken);
        Assert.True(outcome.Success, outcome.ErrorCode);
        Assert.True(File.Exists(Path.Combine(preparation.SandboxRoot, "game",
            "BepInEx", "plugins", "LevelImposter", "LevelImposter.dll")));
    }
}