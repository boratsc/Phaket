namespace Phaket.InstallerStub.Tests;

// Kolejność MUSI być identyczna z Velopack SortOrder — inaczej stub instalowałby inną
// wersję, niż Update.exe zaproponuje później jako najnowszą.
public sealed class VersionSelectionTests
{
    private const string Sha = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private static ReleaseEntry? Latest(params string[] versions) =>
        ManifestParser.SelectLatest(ManifestParser.Parse(Bytes.Manifest(
            versions.Select(v => (v, $"Phaket-{v}-full.nupkg", Sha)).ToArray())));

    [Theory]
    [InlineData(new[] { "0.1.0", "0.2.0", "0.10.0" }, "0.10.0")]                 // 0.10 > 0.2 numerycznie
    [InlineData(new[] { "1.0.0-beta1", "1.0.0", "0.9.9" }, "1.0.0")]              // stabilne > prerelease
    [InlineData(new[] { "1.0.0-beta2", "1.0.0-beta10" }, "1.0.0-beta2")]          // Velopack porównuje prerelease tekstem
    [InlineData(new[] { "1.0.0", "1.0.0.0" }, "1.0.0")]                           // 1.0.0 == 1.0.0.0: zostaje pierwszy
    [InlineData(new[] { "1.0.0.1", "1.0.0" }, "1.0.0.1")]
    [InlineData(new[] { "2.0.0+build5", "1.9.9" }, "2.0.0+build5")]               // metadata build nie liczy się
    [InlineData(new[] { "0.1.0", "0.1.1-preview.3" }, "0.1.1-preview.3")]
    public void Selects_newest_full_package(string[] versions, string expected) =>
        Assert.Equal(expected, Latest(versions)?.Version);

    [Fact]
    public void No_full_package_returns_null() =>
        Assert.Null(ManifestParser.SelectLatest(ManifestParser.Parse(Bytes.Manifest(
            ("1.0.0", "Phaket-1.0.0-delta.nupkg", Sha)))));

    [Fact]
    public void Setup_entry_is_found_next_to_the_package()
    {
        var manifest = ManifestParser.Parse(Bytes.Manifest(
            ("1.0.0", "Phaket-1.0.0-full.nupkg", Sha),
            ("1.0.0", "Phaket-Setup.exe", Sha)));

        Assert.Equal("Phaket-Setup.exe", FirstInstall.SetupEntry(manifest)!.File);
    }

    [Fact]
    public void Build_number_follows_msix_scheme()
    {
        Assert.Equal(1_00_00_00, FirstInstall.BuildNumber("1.0.0"));
        Assert.Equal(1_01_00_05, FirstInstall.BuildNumber("1.1.5"));
        Assert.Equal(10_00_00_00, FirstInstall.BuildNumber("10.0.0"));
    }

    [Fact]
    public void Feed_generated_by_release_script_matches_stub_expectations()
    {
        // Kształt pliku z serwera (PAK-110): File jest nazwą pliku, adres dokłada downloadBaseUrl.
        const string sha = "3E23E8160039594A33894F6564E1B1348BBD7A0088D42C4ACB73EEAED59C009D";
        var json = $$"""
        {"success":true,"latestVersion":"0.2.0","downloadBaseUrl":"https://phaket.app/releases/release/win/",
         "releases":[{"Version":"0.2.0","File":"Phaket-0.2.0-full.nupkg","SHA256":"{{sha}}"},
                     {"Version":"0.1.0","File":"Phaket-0.1.0-full.nupkg","SHA256":"{{sha}}"}]}
        """;

        var latest = ManifestParser.SelectLatest(ManifestParser.Parse(json));

        Assert.Equal("0.2.0", latest!.Version);
        Assert.Equal("Phaket-0.2.0-full.nupkg", ManifestParser.LocalName(latest.File));
    }
}
