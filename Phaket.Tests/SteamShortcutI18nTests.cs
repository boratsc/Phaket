using System.Text.Json;

namespace Phaket.Tests;

// The two hand-written language files: same keys, and every new Steam/saves key present with
// real wording (the resource is unreadable past "Game.Back" in some builds, so checked here).
public class SteamShortcutI18nTests
{
    // The app embeds its language files (Phaket.csproj), so what ships is read from the
    // assembly the tests run against — not from a copy next to the test binary.
    private static Dictionary<string, string> Load(string file)
    {
        using var stream = typeof(Phaket.Services.LocalizationService).Assembly
            .GetManifestResourceStream("Phaket.Assets.i18n." + file)
            ?? throw new InvalidOperationException($"embedded {file} not found");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    private static Dictionary<string, string> Source(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(SourceRoot(), "Phaket", "Assets", "i18n", file)))!;

    // src/Phaket.Tests/bin/Debug/net10.0 -> the repository root.
    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Phaket.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Phaket.slnx not found above the test output");
    }

    [Fact]
    public void BothLanguagesCarryTheSameSteamAndSavesKeys()
    {
        var pl = Source("pl-PL.json");
        var en = Source("en.json");
        Assert.Equal(pl.Keys.Order(StringComparer.Ordinal), en.Keys.Order(StringComparer.Ordinal));
        // Every new saves/conflict key, named here so a rename cannot ship half-translated.
        var savesAndConflicts = pl.Keys.Where(key =>
            key.StartsWith("SaveBackup.", StringComparison.Ordinal) || key.StartsWith("Conflict.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(savesAndConflicts);
        var polishOnly = new[] { "się", "nie", "pliki", "kopie", "zapisy", "zostanie", "gry" };
        foreach (var key in savesAndConflicts)
        {
            Assert.True(pl.TryGetValue(key, out var polish) && !string.IsNullOrWhiteSpace(polish), key);
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english), key);
            Assert.DoesNotContain(polishOnly, word => english!.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var key in new[]
                 {
                     "SteamShortcut.Add", "SteamShortcut.Remove", "SteamShortcut.RestartSteam",
                     "SteamShortcut.AddHeader", "SteamShortcut.RemoveHeader",
                     "SteamShortcut.AddConsent", "SteamShortcut.RemoveConsent",
                     "SteamShortcut.Added", "SteamShortcut.Removed",
                     "SteamShortcut.AddedRestartSteam", "SteamShortcut.RemovedRestartSteam",
                     "SteamShortcut.NotFound", "SteamShortcut.Failed",
                     "Game.PreparingSaves", "Game.SavesSharedTitle", "Game.SavesShared", "Game.Dismiss",
                 })
        {
            Assert.True(pl.TryGetValue(key, out var polish) && !string.IsNullOrWhiteSpace(polish), key);
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english), key);
        }
        // Plain words for a 15-year-old: no machine identifiers leak into the texts.
        Assert.DoesNotContain(pl.Values.Concat(en.Values),
            text => text.Contains("env override", StringComparison.OrdinalIgnoreCase) || text.Contains("shortcutId", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Dodaj do Steam", pl["SteamShortcut.Add"]);
        // The English file is English: no Polish wording may leak into it.
        Assert.Equal("Add to Steam", en["SteamShortcut.Add"]);
        Assert.DoesNotContain(en.Values, text =>
            text.Contains("zapis", StringComparison.OrdinalIgnoreCase) || text.Contains("Steam? Phaket zapisze", StringComparison.Ordinal));
        Assert.Equal("Usuń ze Steam", pl["SteamShortcut.Remove"]);
        Assert.Contains("Phaket zapisze skrót w plikach Steam", pl["SteamShortcut.AddConsent"]);
    }

    [Fact]
    public void BothShippedResourcesCarryTheSameKeysAsTheSource()
    {
        foreach (var file in new[] { "pl-PL.json", "en.json" })
            Assert.Equal(Source(file).Keys.Order(StringComparer.Ordinal), Load(file).Keys.Order(StringComparer.Ordinal));
    }
}