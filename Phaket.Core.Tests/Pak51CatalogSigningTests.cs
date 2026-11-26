using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Phaket.Core.Catalog;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-51: the catalog client trusts only a built-in Ed25519 key (idle list ships empty).
// A local stub stands in for the server; no live HTTP.
public sealed class Pak51CatalogSigningTests
{
    private static readonly (Ed25519PrivateKeyParameters Private, byte[] Public) TestKey = MakeKey();

    private static (Ed25519PrivateKeyParameters Private, byte[] Public) MakeKey()
    {
        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        var key = new Ed25519PrivateKeyParameters(seed, 0);
        return (key, key.GeneratePublicKey().GetEncoded());
    }

    private static string Sign(string body)
    {
        // The id is checked separately; signing only needs the private half.
        var signer = new Ed25519Signer();
        signer.Init(true, TestKey.Private);
        var message = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body));
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    private sealed class FakeTransport : ICatalogTransport
    {
        public Queue<CatalogResponse> Responses { get; } = new();
        public List<string> Requests { get; } = [];
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        { Requests.Add(path); return Task.FromResult(Responses.Dequeue()); }
    }

    private static string Mod(string id) => System.Text.Json.JsonSerializer.Serialize(new ThunderstoreMod(id,
        "among-us", "ns", id, $"ns-{id}", "", "owner", "https://example.test", null, false,
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        new ThunderstoreVersion("1.0.0", $"ns-{id}-1.0.0", "https://example.test/mod", 1,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), [])));

    private static string Page(string mod, long revision = 1) =>
        "{\"revision\":" + revision + ",\"mods\":[" + mod + "],\"nextCursor\":null}";

    private static (FakeTransport T, ModCatalogStore S, ModCatalogClient C, string Dir) Setup()
    {
        var dir = Directory.CreateTempSubdirectory("pak51-signing-").FullName;
        var transport = new FakeTransport();
        var store = new ModCatalogStore(Path.Combine(dir, "db.sqlite"));
        return (transport, store, new ModCatalogClient(transport, store), dir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CatalogResponse Signed(string body) => new(200, "e",
        body, Sign(body), ModCatalogSigning.KeyIdOf(TestKey.Public));

    [Fact]
    public void KeyIdMatchesTheServersHexSha256Prefix()
    {
        // Server: hex(SHA-256(public key))[0..16].
        Assert.Equal(Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(TestKey.Public)).ToLowerInvariant()[..16],
            ModCatalogSigning.KeyIdOf(TestKey.Public));
        Assert.Equal(16, ModCatalogSigning.KeyIdOf(TestKey.Public).Length);
    }

    [Fact]
    public async Task SignedSnapshotIsVerifiedAndStored()
    {
        ModCatalogSigning.AddTrustedKeyForTests(TestKey.Public);
        var (t, s, c, dir) = Setup();
        try
        {
            var body = Page(Mod("a"));
            t.Responses.Enqueue(Signed(body));
            await c.SyncAsync("among-us", Ct);
            Assert.Equal(CatalogTrust.Verified, c.LastResult!.Trust);
            Assert.Equal("a", Assert.Single(s.GetMods("among-us")).Id);
        }
        finally { s.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task BadSignatureIsRejectedAndLastGoodKept()
    {
        ModCatalogSigning.AddTrustedKeyForTests(TestKey.Public);
        var (t, s, c, dir) = Setup();
        try
        {
            t.Responses.Enqueue(Signed(Page(Mod("a"))));
            await c.SyncAsync("among-us", Ct);
            // Signature computed over a different body: genuine key id, forged content.
            var forged = Page(Mod("evil"));
            t.Responses.Enqueue(new(200, "e2", forged, Sign(Page(Mod("other"))), ModCatalogSigning.KeyIdOf(TestKey.Public)));
            var error = await Assert.ThrowsAsync<PhaketException>(() => c.SyncAsync("among-us", Ct));
            Assert.Equal(ErrorCodes.CatalogSignatureInvalid, error.Error.Code);
            Assert.Equal("a", Assert.Single(s.GetMods("among-us")).Id);
        }
        finally { s.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task UnknownKeyIdIsRejected()
    {
        ModCatalogSigning.AddTrustedKeyForTests(TestKey.Public);
        var (t, s, c, dir) = Setup();
        try
        {
            var body = Page(Mod("a"));
            t.Responses.Enqueue(new(200, "e", body, Sign(body), "0123456789abcdef"));
            var error = await Assert.ThrowsAsync<PhaketException>(() => c.SyncAsync("among-us", Ct));
            Assert.Equal(ErrorCodes.CatalogSignatureUnknownKey, error.Error.Code);
            Assert.Empty(s.GetMods("among-us"));
        }
        finally { s.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task UnsignedResponseIsAcceptedAsUnverified()
    {
        var (t, s, c, dir) = Setup();
        try
        {
            t.Responses.Enqueue(new(200, "e", Page(Mod("a"))));
            await c.SyncAsync("among-us", Ct);
            Assert.Equal(CatalogTrust.Unverified, c.LastResult!.Trust);
            Assert.Single(s.GetMods("among-us"));
        }
        finally { s.Dispose(); Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("signature", null)]
    [InlineData("signature", "")]
    [InlineData(null, "0123456789abcdef")]
    [InlineData("", "0123456789abcdef")]
    public void IncompleteSignatureHeaderPairIsRejected(string? signature, string? keyId)
    {
        var error = Assert.Throws<PhaketException>(() => ModCatalogSigning.Verify("{}", signature, keyId));
        Assert.Equal(ErrorCodes.CatalogSignatureInvalid, error.Error.Code);
    }

    [Fact]
    public async Task StaleSnapshotRevisionIsRejected()
    {
        var (t, s, c, dir) = Setup();
        try
        {
            // A snapshot applies only when it is newer than the cached revision. A full
            // snapshot sets the same revision for every game, so valheim's snapshot raises
            // the shared revision and the older among-us page must not be written.
            t.Responses.Enqueue(new(200, "e0", Page(Mod("a"), revision: 4)));
            await c.SyncAsync("among-us", Ct);
            t.Responses.Enqueue(new(200, "e", Page(Mod("v"), revision: 5)));
            await c.SyncAsync("valheim", Ct);
            // A delta only needs a revision: it must be refused before anything is applied.
            t.Responses.Enqueue(new(200, null, """{"reset":false,"revision":4,"changes":[]}"""));
            await Assert.ThrowsAsync<PhaketException>(() => c.SyncAsync("among-us", Ct));
            // Nothing of the stale answer reached the cache: no "b", the last good rows stay.
            Assert.DoesNotContain(s.GetMods("among-us"), m => m.Id == "b");
        }
        finally { s.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StaleDeltaRevisionIsRejected()
    {
        var (t, s, c, dir) = Setup();
        try
        {
            t.Responses.Enqueue(new(200, "e", Page(Mod("a"), revision: 4)));
            await c.SyncAsync("among-us", Ct);
            t.Responses.Enqueue(new(200, null, $$"""{"reset":false,"revision":4,"changes":[{"op":"upsert","mod":{{Mod("b")}}}]}"""));
            var error = await Assert.ThrowsAsync<PhaketException>(() => c.SyncAsync("among-us", Ct));
            Assert.Equal(ErrorCodes.CatalogRevisionStale, error.Error.Code);
            Assert.Equal("a", Assert.Single(s.GetMods("among-us")).Id);
        }
        finally { s.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SignedDeltaIsVerified()
    {
        ModCatalogSigning.AddTrustedKeyForTests(TestKey.Public);
        var (t, s, c, dir) = Setup();
        try
        {
            t.Responses.Enqueue(Signed(Page(Mod("a"))));
            await c.SyncAsync("among-us", Ct);
            var delta = $$"""{"reset":false,"revision":2,"changes":[{"op":"upsert","mod":{{Mod("b")}}}]}""";
            t.Responses.Enqueue(Signed(delta));
            await c.SyncAsync("among-us", Ct);
            Assert.Equal(CatalogTrust.Verified, c.LastResult!.Trust);
            Assert.Equal(["a", "b"], s.GetMods("among-us").Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal));
        }
        finally { s.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void SignatureCodesAreLocalizedInPolishAndEnglish()
    {
        foreach (var code in new[] { ErrorCodes.CatalogSignatureInvalid, ErrorCodes.CatalogSignatureUnknownKey, ErrorCodes.CatalogRevisionStale })
        {
            Assert.True(ErrorMessages.TryGetTemplate(code, "pl-PL", out var pl));
            Assert.True(ErrorMessages.TryGetTemplate(code, "en", out var en));
            Assert.NotEqual(code, pl);
            Assert.NotEqual(code, en);
            Assert.NotEqual(pl, en);
        }
    }
}