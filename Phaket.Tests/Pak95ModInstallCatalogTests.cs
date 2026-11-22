using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Catalog;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Services;

namespace Phaket.Tests;

// PAK-95: ModInstallService consumes catalog packaging/artifacts without the
// Thunderstore path regressing. Sandbox only; no network and no real game installs.
public sealed class Pak95ModInstallCatalogTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ModName = "Submerged";
    private const string Url = "https://susmodder-cdn.ovh/mods/submerged/1.0.0.zip";

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        return buffer.ToArray();
    }

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

    private sealed class Packages(string url, byte[] bytes) : IPackageTransport
    {
        public int Calls { get; private set; }
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken)
        {
            Calls++;
            return source == url
                ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
                : throw new InvalidOperationException($"unexpected download: {source}");
        }
    }

    private sealed class Receipts : IReceiptTransport
    {
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (ThunderstoreMod Mod, ThunderstoreVersion Version) CatalogEntry(byte[] zip, string arch, string amongVersion)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
        var version = new ThunderstoreVersion("1.0.0", $"{ModName}-1.0.0", Url, zip.Length, DateTimeOffset.UnixEpoch,
            [], true, [new ModArtifact(Url, arch, ["steam", "itch"], hash, zip.Length, "susmodder-cdn", "submerged.zip")],
            "dll", amongVersion, arch, "susmodder", null);
        return (new ThunderstoreMod(ModName, "among-us", "Sus", ModName, ModName, "desc", "sus", "https://example.test",
            null, false, DateTimeOffset.UnixEpoch, version), version);
    }

    [Fact]
    public async Task DllCatalogPackagePlansAndInstallsOnlyWritableAreasInSandbox()
    {
        var root = Directory.CreateTempSubdirectory("pak95-install-").FullName;
        try
        {
            var zip = Zip(("BepInEx/plugins/Submerged/Submerged.dll", "mod"),
                ("Among Us_Data/sharedassets0.assets", "vanilla"));
            var (mod, version) = CatalogEntry(zip, "x86", "ToU");
            using var store = new ModCatalogStore(Path.Combine(root, "catalog.db"));
            var service = new ModInstallService(root, new ModCatalogClient(new Catalog(mod, version), store),
                new Packages(Url, zip), new Receipts());

            var preparation = await service.PrepareAsync("among-us", ModName, "1.0.0", "",
                new("", "steam", true, "x86"), ct: TestContext.Current.CancellationToken);

            var plan = Assert.Single(preparation.Plans);
            var operation = Assert.Single(plan.Operations);
            Assert.Equal("archive.extract", operation.Kind);
            Assert.Contains("BepInEx/**", operation.Parameters!["include"]);
            Assert.True(File.Exists(Path.Combine(preparation.PackageRoots[0], "package.zip")));

            var outcome = await service.ExecuteAsync(preparation, true, ct: TestContext.Current.CancellationToken);
            Assert.True(outcome.Success, outcome.ErrorCode);
            var game = Path.Combine(preparation.SandboxRoot, "game");
            Assert.True(File.Exists(Path.Combine(game, "BepInEx", "plugins", "Submerged", "Submerged.dll")));
            Assert.False(File.Exists(Path.Combine(game, "Among Us_Data", "sharedassets0.assets")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogPackageRefusesWhenNoArtifactMatchesTheInstallation()
    {
        var root = Directory.CreateTempSubdirectory("pak95-install-refuse-").FullName;
        try
        {
            var zip = Zip(("BepInEx/plugins/Submerged/Submerged.dll", "mod"));
            var (mod, version) = CatalogEntry(zip, "x64", "ToU");
            using var store = new ModCatalogStore(Path.Combine(root, "catalog.db"));
            var service = new ModInstallService(root, new ModCatalogClient(new Catalog(mod, version), store),
                new Packages(Url, zip), new Receipts());

            var error = await Assert.ThrowsAsync<PhaketException>(() => service.PrepareAsync("among-us", ModName,
                "1.0.0", "", new("", "steam", true, "x86"), ct: TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCodes.ModArtifactUnavailable, error.Error.Code);
            Assert.Equal("x86", error.Error.Parameters["arch"]);
        }
        finally { Directory.Delete(root, true); }
    }

    // K1: the observed hash of a download must never become the pin. Without a
    // server-declared sha256 there is nothing to trust, so the install is refused
    // before any byte is fetched.
    [Fact]
    public async Task CatalogPackageWithoutDeclaredHashIsRefusedBeforeDownload()
    {
        var root = Directory.CreateTempSubdirectory("pak95-install-hash-").FullName;
        try
        {
            var zip = Zip(("BepInEx/plugins/Submerged/Submerged.dll", "mod"));
            var version = new ThunderstoreVersion("1.0.0", $"{ModName}-1.0.0", Url, zip.Length, DateTimeOffset.UnixEpoch,
                [], true, [new ModArtifact(Url, "x86", ["steam", "itch"], null, zip.Length, "susmodder-cdn", "submerged.zip")],
                "dll", "ToU", "x86", "susmodder", null);
            var mod = new ThunderstoreMod(ModName, "among-us", "Sus", ModName, ModName, "desc", "sus", "https://example.test",
                null, false, DateTimeOffset.UnixEpoch, version);
            using var store = new ModCatalogStore(Path.Combine(root, "catalog.db"));
            var packages = new Packages(Url, zip);
            var service = new ModInstallService(root, new ModCatalogClient(new Catalog(mod, version), store),
                packages, new Receipts());

            var error = await Assert.ThrowsAsync<PhaketException>(() => service.PrepareAsync("among-us", ModName,
                "1.0.0", "", new("", "steam", true, "x86"), ct: TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCodes.ModArtifactHashUndeclared, error.Error.Code);
            Assert.Equal(0, packages.Calls);
        }
        finally { Directory.Delete(root, true); }
    }
}
