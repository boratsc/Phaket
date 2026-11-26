namespace Phaket.InstallerStub.Tests;

// Kontrakt feedu PAK-110: {success, latestVersion, downloadBaseUrl, releases[{Version, File, SHA256}]}.
public sealed class ManifestTests
{
    private const string Sha = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void Parses_valid_manifest()
    {
        var manifest = ManifestParser.Parse(Bytes.Manifest(("0.1.0", "Phaket-0.1.0-full.nupkg", Sha)));

        Assert.Equal("0.1.0", manifest.LatestVersion);
        Assert.Equal("https://cdn.example/phaket/", manifest.DownloadBaseUrl);
        var entry = Assert.Single(manifest.Releases);
        Assert.Equal("Phaket-0.1.0-full.nupkg", entry.File);
        Assert.Equal(Sha, entry.Sha256);
    }

    [Theory]
    [InlineData("""{"latestVersion":"1.0.0","downloadBaseUrl":"https://x/","releases":[]}""")]        // brak success
    [InlineData("""{"success":false,"latestVersion":"1.0.0","releases":[]}""")]                     // success=false
    [InlineData("""{"success":true}""")]                                                            // brak releases
    [InlineData("""{"success":true,"releases":{}}""")]                                              // releases nie-tablica
    [InlineData("""not json""")]                                                                    // uszkodzony JSON
    [InlineData("""[]""")]                                                                          // korzeń to tablica
    public void Rejects_invalid_manifest(string json) =>
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(json));

    [Fact]
    public void Empty_releases_yield_no_latest()
    {
        var manifest = ManifestParser.Parse("""{"success":true,"latestVersion":"1.0.0","releases":[]}""");

        Assert.Empty(manifest.Releases);
        Assert.Null(ManifestParser.SelectLatest(manifest));
    }

    [Fact]
    public void Skips_entries_without_full_nupkg()
    {
        var manifest = ManifestParser.Parse(Bytes.Manifest(
            ("1.0.0", "Phaket-1.0.0-delta.nupkg", Sha),
            ("1.0.0", "Phaket-Portable.zip", Sha),
            ("1.0.0", "Phaket-Setup.exe", Sha)));

        Assert.Equal(3, manifest.Releases.Count);
        Assert.Null(ManifestParser.SelectLatest(manifest));
    }

    [Theory]
    [InlineData("../../escape-full.nupkg")]
    [InlineData("sub/dir/Phaket-1.0.0-full.nupkg")]
    [InlineData(@"..\Phaket-1.0.0-full.nupkg")]
    [InlineData("C:Phaket-1.0.0-full.nupkg")]
    public void Skips_unsafe_file_names(string file)
    {
        var manifest = ManifestParser.Parse(Bytes.Manifest(("1.0.0", file, Sha)));

        Assert.Empty(manifest.Releases);
    }

    [Fact]
    public void Skips_entries_with_malformed_hash()
    {
        var manifest = ManifestParser.Parse(Bytes.Manifest(("1.0.0", "Phaket-1.0.0-full.nupkg", "XYZ")));

        Assert.Empty(manifest.Releases);
    }

    [Fact]
    public void Rejects_plain_http_download_base()
    {
        var json = $$"""{"success":true,"latestVersion":"1.0.0","downloadBaseUrl":"http://cdn.example/","releases":[]}""";

        Assert.Throws<ManifestException>(() => ManifestParser.Parse(json));
    }

    [Fact]
    public void Property_names_are_case_insensitive()
    {
        var json = $$"""{"Success":true,"LatestVersion":"1.2.3","Releases":[{"version":"1.2.3","file":"Phaket-1.2.3-full.nupkg","sha256":"{{Sha}}"}]}""";

        var entry = ManifestParser.SelectLatest(ManifestParser.Parse(json));
        Assert.NotNull(entry);
        Assert.Equal("Phaket-1.2.3-full.nupkg", entry!.File);
    }
}