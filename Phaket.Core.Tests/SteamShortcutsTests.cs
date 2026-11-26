using System.Text;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms.SteamShortcuts;

namespace Phaket.Core.Tests;

// PAK-145: a modded game as a Steam non-Steam shortcut. Fixture trees only; the real Steam
// folder is never touched. The golden hex in SteamShortcutFixtures.cs was produced by
// steam-shortcut-editor 3.1.3 (npm, MIT — the library Heroic writes shortcuts.vdf with).
public class SteamShortcutsTests
{
    private const string User = "76561198000000001";
    private const string AppName = "Among Us — Town of Us";
    private const string Exe = "\"/Gry/Steam/Among Us/Among Us.exe\"";
    private const string StartDir = "\"/Gry/Steam/Among Us\"";

    // The Polish entry of the two-entry golden file.
    private const int FirmAppId = SteamShortcutFixtures.FirmAppId;
    private const int TownAppId = SteamShortcutFixtures.TownAppId;
    private const string FirmName = "Zmodowana gra: Ćma łąka";
    private const string FirmExe = "\"C:\\Gry użytkownika\\ćwiczenia\\Gra.exe\"";
    private const string FirmDir = "\"C:\\Gry użytkownika\"";

    private static byte[] Hex(string text) => Convert.FromHexString(text);

    private static string Fixture(out string steamRoot)
    {
        steamRoot = Directory.CreateTempSubdirectory("phaket-steam-shortcuts-").FullName;
        Directory.CreateDirectory(Path.Combine(steamRoot, "userdata", User, "config"));
        Directory.CreateDirectory(Path.Combine(steamRoot, "userdata", "ac")); // not a user
        return SteamShortcutService.ShortcutsPath(steamRoot, User);
    }

    private static SteamShortcut Shortcut(string name = AppName) => new(name, Exe, StartDir, "");

    private static void Upsert(BinaryVdfNode root, int appId, string name, string exe, string dir)
    {
        var entry = SteamShortcutsFile.CreateEntry(name, exe, dir, "");
        entry.Set("appid", appId); // the golden files pin the reference's ids
        SteamShortcutsFile.TagPhaket(entry); // the reference fixtures carry the "phaket" tag too
        SteamShortcutsFile.Upsert(root, entry);
    }

    // ── writer == reference, byte for byte ────────────────────────────────────────────────────

    [Fact]
    public void EmptyFileMatchesTheReference()
    {
        Assert.Equal(Hex(SteamShortcutFixtures.Empty), BinaryVdf.Write(SteamShortcutsFile.Parse([])));
    }

    [Fact]
    public void OneEntryWithTagsMatchesTheReference()
    {
        var root = SteamShortcutsFile.Parse([]);
        Upsert(root, 123454321, "Among Us", "\"/gry/Among Us.exe\"", "\"/gry\"");
        Assert.Equal(Hex(SteamShortcutFixtures.OneEntry), BinaryVdf.Write(root));
    }

    [Fact]
    public void TwoEntriesWithPolishNamesMatchTheReference()
    {
        var root = SteamShortcutsFile.Parse([]);
        Upsert(root, FirmAppId, FirmName, FirmExe, FirmDir);
        Upsert(root, TownAppId, "Town of Us Mira", "\"/home/gracz/Gry/Among Us.exe\"", "\"/home/gracz/Gry\"");
        // Byte for byte against the reference, including the "0"/"1" entry keys and both tags.
        Assert.Equal(Hex(SteamShortcutFixtures.TwoEntries), BinaryVdf.Write(root));
    }

    // ── reader ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadsTheReferenceFileWithEveryField()
    {
        var entries = SteamShortcutsFile.Entries(SteamShortcutsFile.Parse(Hex(SteamShortcutFixtures.TwoEntries)));
        Assert.Equal(2, entries.Count);

        Assert.Equal(FirmAppId, entries[0].GetInt32("appid"));
        Assert.Equal(FirmName, entries[0].GetString("AppName"));
        Assert.Equal(FirmExe, entries[0].GetString("Exe"));
        Assert.Equal(FirmDir, entries[0].GetString("StartDir"));
        Assert.Equal("", entries[0].GetString("LaunchOptions"));
        Assert.Equal(1, entries[0].GetInt32("AllowDesktopConfig"));
        Assert.Contains("phaket", entries[0].GetNode("tags")!.Members.Select(m => m.RawValue is { } v ? Encoding.UTF8.GetString(v) : ""));

        Assert.Equal(TownAppId, entries[1].GetInt32("appid"));
        Assert.Equal("Town of Us Mira", entries[1].GetString("AppName"));
        Assert.Equal("\"/home/gracz/Gry/Among Us.exe\"", entries[1].GetString("Exe"));
        Assert.Equal(0, entries[1].GetInt32("IsHidden"));
        // Both entries carry the phaket tag, and it reads back as a string, not a number.
        Assert.True(SteamShortcutsFile.IsPhaket(entries[0]));
        Assert.Equal(SteamShortcutsFile.Tag, entries[1].GetNode("tags")!.GetString("0"));
    }

    [Fact]
    public void RoundTripsTheReferenceFilesByteForByte()
    {
        foreach (var hex in new[] { SteamShortcutFixtures.Empty, SteamShortcutFixtures.OneEntry, SteamShortcutFixtures.TwoEntries })
            Assert.Equal(Hex(hex), BinaryVdf.Write(SteamShortcutsFile.Parse(Hex(hex))));
    }

    [Fact]
    public void Int64LastPlayTimeFromOtherToolsSurvives()
    {
        // Other tools store LastPlayTime as 0x07 int64; reading and rewriting keeps that member.
        var file = Hex(SteamShortcutFixtures.TwoEntries);
        var key = Encoding.UTF8.GetBytes("LastPlayTime\0");
        var at = file.AsSpan().IndexOf(key);
        Assert.True(at > 0);
        Assert.Equal(0x02, file[at - 1]);
        var swapped = file[..(at - 1)].Concat(new byte[] { 0x07 }).Concat(key)
            .Concat(BitConverter.GetBytes(1527542942L)).Concat(file[(at + key.Length + 4)..]).ToArray();

        var entry = SteamShortcutsFile.Entries(SteamShortcutsFile.Parse(swapped))[0];
        Assert.Equal(8, entry.Find("LastPlayTime")!.RawValue!.Length);
        Assert.Equal(swapped, BinaryVdf.Write(SteamShortcutsFile.Parse(swapped)));
    }

    [Fact]
    public void TruncatedOrUnknownFileIsRefused()
    {
        var file = Hex(SteamShortcutFixtures.TwoEntries);
        foreach (var cut in new[] { file.Length - 1, file.Length / 2, 20 })
            Assert.Throws<FormatException>(() => SteamShortcutsFile.Parse(file[..cut]));
        Assert.Throws<FormatException>(() => SteamShortcutsFile.Parse([0x00, (byte)'x', 0x01])); // no terminator
        Assert.Throws<FormatException>(() => SteamShortcutsFile.Parse([0x00, (byte)'a', 0x00, 0x00, 0x00, 0x63, 0x00])); // unknown 0x63
        // A root without "shortcuts" is not a shortcuts file.
        Assert.Throws<FormatException>(() => SteamShortcutsFile.Parse([0x00, .. "other\0"u8, 0x08]));
    }

    // ── ids ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IdsFollowHeroicsFormulaAndAreDeterministic()
    {
        // crc32(appName + exe) | 0x80000000, from node's zlib in the reference environment.
        Assert.Equal(FirmAppId, SteamShortcutsFile.GenerateShortcutId(FirmExe, FirmName));
        Assert.Equal(FirmAppId, SteamShortcutsFile.GenerateShortcutId(FirmExe, FirmName)); // deterministic
        Assert.Equal(TownAppId, SteamShortcutsFile.GenerateShortcutId("\"/home/gracz/Gry/Among Us.exe\"", "Town of Us Mira"));
        // The top bit is always set, so the id never collides with a real Steam appid.
        Assert.True(FirmAppId < 0);
        // Swapping name and exe, or editing either, gives another id.
        Assert.NotEqual(FirmAppId, SteamShortcutsFile.GenerateShortcutId(FirmName, FirmExe));
        Assert.NotEqual(FirmAppId, SteamShortcutsFile.GenerateShortcutId(FirmExe, FirmName + " 2"));
        Assert.NotEqual(FirmAppId, SteamShortcutsFile.GenerateShortcutId("x" + FirmExe, FirmName));
        // The service writes exactly the formula's id into the entry.
        var root = SteamShortcutsFile.Parse([]);
        Upsert(root, FirmAppId, FirmName, FirmExe, FirmDir);
        var entry = Assert.Single(SteamShortcutsFile.Entries(SteamShortcutsFile.Parse(BinaryVdf.Write(root))));
        Assert.Equal(FirmAppId, entry.GetInt32("appid"));
    }

    // ── service ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddingAShortcutKeepsForeignFileBytesAndIsIdempotent()
    {
        var path = Fixture(out var root);
        var foreign = Hex(SteamShortcutFixtures.TwoEntries);
        File.WriteAllBytes(path, foreign);
        var service = new SteamShortcutService(() => false);

        var first = await service.AddShortcutAsync(root, User, Shortcut(), consent: true, TestContext.Current.CancellationToken);
        var afterFirst = File.ReadAllBytes(path);
        var second = await service.AddShortcutAsync(root, User, Shortcut(), consent: true, TestContext.Current.CancellationToken);

        Assert.Equal(first.AppId, second.AppId);
        Assert.Equal($"steam://rungameid/{(uint)first.AppId}", first.RunGameUri);
        Assert.False(first.SteamRunning);
        Assert.Equal(afterFirst, File.ReadAllBytes(path)); // re-adding writes the same bytes
        // Both foreign entries survived byte for byte; only entry "2" was appended after them.
        Assert.Equal(foreign[..^3], afterFirst[..(foreign.Length - 3)]);

        var entries = SteamShortcutsFile.Entries(SteamShortcutsFile.Parse(afterFirst));
        Assert.Equal(3, entries.Count);
        Assert.Equal(FirmName, entries[0].GetString("AppName"));
        Assert.Equal("Town of Us Mira", entries[1].GetString("AppName"));
        Assert.Equal(SteamShortcutsFile.Tag, entries[2].GetNode("tags")!.GetString("0"));
        Assert.Equal(AppName, entries[2].GetString("AppName"));
        Assert.Equal(Exe, entries[2].GetString("Exe"));
        Assert.Equal(StartDir, entries[2].GetString("StartDir"));
        Assert.Equal(SteamShortcutsFile.GenerateShortcutId(Exe, AppName), entries[2].GetInt32("appid"));
    }

    [Fact]
    public async Task AddingToAFreshUserWritesAReadableFile()
    {
        var path = Fixture(out var root);
        var service = new SteamShortcutService(() => false);
        var result = await service.AddShortcutAsync(root, User, Shortcut(), consent: true, TestContext.Current.CancellationToken);

        var entries = SteamShortcutsFile.Entries(SteamShortcutsFile.Parse(File.ReadAllBytes(path)));
        Assert.Equal(result.AppId, Assert.Single(entries).GetInt32("appid"));
        Assert.False(result.BackupCreated); // nothing to back up yet
        Assert.Equal([result.AppId], service.ListPhaketShortcuts(root, User).Select(s => s.AppId));
    }

    [Fact]
    public void PolishNamesAndSpacesSurviveRoundTrip()
    {
        var root = SteamShortcutsFile.Parse([]);
        Upsert(root, FirmAppId, FirmName, FirmExe, FirmDir);
        var entry = Assert.Single(SteamShortcutsFile.Entries(SteamShortcutsFile.Parse(BinaryVdf.Write(root))));
        Assert.Equal(FirmName, entry.GetString("AppName"));
        Assert.Equal(FirmExe, entry.GetString("Exe"));
        Assert.Equal(FirmDir, entry.GetString("StartDir"));
        Assert.Equal(FirmAppId, entry.GetInt32("appid"));
        Assert.True(SteamShortcutsFile.IsPhaket(entry));
    }

    [Fact]
    public async Task RemovingDeletesOnlyItsOwnEntryAndArtwork()
    {
        var path = Fixture(out var root);
        // A foreign file: the reference entries without our tag, as another tool leaves them.
        var foreignRoot = SteamShortcutsFile.Parse(Hex(SteamShortcutFixtures.TwoEntries));
        foreach (var e in SteamShortcutsFile.Entries(foreignRoot)) e.Remove("tags");
        var foreign = BinaryVdf.Write(foreignRoot);
        File.WriteAllBytes(path, foreign);
        var service = new SteamShortcutService(() => false);
        await service.AddShortcutAsync(root, User, Shortcut(), consent: true, TestContext.Current.CancellationToken);
        var grid = Path.Combine(root, "userdata", User, "config", "grid");
        var logo = Path.Combine(grid, $"{(uint)SteamShortcutsFile.GenerateShortcutId(Exe, AppName)}_logo.png");
        Assert.True(File.Exists(logo));

        var result = await service.RemoveShortcutAsync(root, User, shortcutPath: null, exe: null, consent: true, TestContext.Current.CancellationToken);

        Assert.Equal(SteamShortcutsFile.GenerateShortcutId(Exe, AppName), result!.AppId);
        Assert.Empty(service.ListPhaketShortcuts(root, User));
        Assert.Equal(foreign, File.ReadAllBytes(path)); // the foreign file is back, byte for byte
        Assert.False(File.Exists(logo));
        Assert.Null(await service.RemoveShortcutAsync(root, User, null, null, consent: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovingByExeOnlyTouchesThatEntry()
    {
        var path = Fixture(out var root);
        var service = new SteamShortcutService(() => false);
        await service.AddShortcutAsync(root, User, Shortcut("Gra A"), consent: true, TestContext.Current.CancellationToken);
        var b = await service.AddShortcutAsync(root, User, new SteamShortcut("Gra B", "\"/gry/b.exe\"", "\"/gry\"", ""), consent: true, TestContext.Current.CancellationToken);
        Assert.Equal(2, service.ListPhaketShortcuts(root, User).Count);

        var removed = await service.RemoveShortcutAsync(root, User, shortcutPath: null, exe: "\"/gry/b.exe\"", consent: true, TestContext.Current.CancellationToken);

        Assert.Equal(b.AppId, removed!.AppId);
        Assert.Equal(SteamShortcutsFile.GenerateShortcutId(Exe, "Gra A"), Assert.Single(service.ListPhaketShortcuts(root, User)).AppId);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task WithoutConsentNothingIsWritten()
    {
        var path = Fixture(out var root);
        var service = new SteamShortcutService(() => false);
        var error = await Assert.ThrowsAsync<PhaketException>(() =>
            service.AddShortcutAsync(root, User, Shortcut(), consent: false, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.SteamShortcutConsentRequired, error.Error.Code);
        Assert.Contains("zgody", error.LocalizedMessage("pl-PL"));
        Assert.Contains("consent", error.LocalizedMessage("en"));
        Assert.Equal(ErrorCodes.SteamShortcutConsentRequired,
            (await Assert.ThrowsAsync<PhaketException>(() =>
                service.RemoveShortcutAsync(root, User, null, null, consent: false, TestContext.Current.CancellationToken))).Error.Code);
        var config = Path.Combine(root, "userdata", User, "config");
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(config, "grid")));
        Assert.Empty(Directory.GetFiles(config)); // not even a temp file
    }

    [Fact]
    public async Task BackupHoldsTheFileFromBeforeThePhaketWrite()
    {
        var path = Fixture(out var root);
        var original = Hex(SteamShortcutFixtures.TwoEntries);
        File.WriteAllBytes(path, original);
        var service = new SteamShortcutService(() => false);

        var first = await service.AddShortcutAsync(root, User, Shortcut(), consent: true, TestContext.Current.CancellationToken);
        Assert.True(first.BackupCreated);
        Assert.Equal(original, File.ReadAllBytes(path + SteamShortcutService.BackupSuffix));

        var second = await service.AddShortcutAsync(root, User, Shortcut("Druga gra"), consent: true, TestContext.Current.CancellationToken);
        // The backup is the file from before the second change, so one step back is always possible.
        Assert.True(second.BackupCreated);
        Assert.NotEqual(original, File.ReadAllBytes(path + SteamShortcutService.BackupSuffix));
        Assert.False(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Any());
    }

    [Fact]
    public async Task BuiltInLogoIsAValidTransparentPng()
    {
        var path = Fixture(out var root);
        var service = new SteamShortcutService(() => false);
        var result = await service.AddShortcutAsync(root, User, Shortcut(), consent: true, TestContext.Current.CancellationToken);
        var logo = Path.Combine(root, "userdata", User, "config", "grid", $"{(uint)result.AppId}_logo.png");
        var bytes = File.ReadAllBytes(logo);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]); // PNG signature
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 6 }, bytes[16..26]);                // IHDR 1x1 RGBA8
        Assert.Equal("IEND", Encoding.ASCII.GetString(bytes[^8..^4]));
        Assert.Equal(SteamShortcutsFile.TransparentLogoPng, bytes); // Heroic's transparentSteamLogoHex
        Assert.Contains(logo, result.ArtworkPaths);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CallerArtworkIsWrittenUnderSteamGridNames()
    {
        Fixture(out var root);
        var service = new SteamShortcutService(() => true);
        var result = await service.AddShortcutAsync(root, User,
            new SteamShortcut(AppName, Exe, StartDir, "", CoverJpg: [1, 2, 3], HeroJpg: [4, 5, 6]), consent: true,
            TestContext.Current.CancellationToken);
        var grid = Path.Combine(root, "userdata", User, "config", "grid");
        var id = (uint)result.AppId;
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(grid, id + "p.jpg")));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(grid, id + "_hero.jpg")));
        Assert.True(File.Exists(Path.Combine(grid, id + "_logo.png")));
        Assert.True(result.SteamRunning); // caller shows "restart Steam" (SteamShortcut.RestartSteam)
    }

    [Fact]
    public async Task CorruptFileIsRefusedAndLeftUnchanged()
    {
        var path = Fixture(out var root);
        var corrupt = new byte[] { 0x00, (byte)'s', 0x01, (byte)'x' };
        File.WriteAllBytes(path, corrupt);
        var service = new SteamShortcutService(() => false);
        var error = await Assert.ThrowsAsync<PhaketException>(() =>
            service.AddShortcutAsync(root, User, Shortcut(), consent: true, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.SteamShortcutsCorrupt, error.Error.Code);
        Assert.Contains("skrótów", error.LocalizedMessage("pl-PL"));
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + SteamShortcutService.BackupSuffix));
        // A half-written reference file is corrupt too.
        File.WriteAllBytes(path, Hex(SteamShortcutFixtures.TwoEntries)[..40]);
        Assert.Equal(ErrorCodes.SteamShortcutsCorrupt, (await Assert.ThrowsAsync<PhaketException>(() =>
            service.RemoveShortcutAsync(root, User, null, null, consent: true, TestContext.Current.CancellationToken))).Error.Code);
        Assert.Throws<PhaketException>(() => service.ListPhaketShortcuts(root, User)); // never "no shortcuts"
    }

    [Fact]
    public async Task UnknownUserOrRootOutsideTheSteamLayoutIsRefused()
    {
        var path = Fixture(out var root);
        var service = new SteamShortcutService(() => false);
        foreach (var user in new[] { "ac", "", "../..", "7656119800000000x" })
            Assert.Equal(ErrorCodes.ValidationFailed, (await Assert.ThrowsAsync<PhaketException>(() =>
                service.AddShortcutAsync(root, user, Shortcut(), consent: true, TestContext.Current.CancellationToken))).Error.Code);
        var stray = Directory.CreateTempSubdirectory("phaket-not-steam-").FullName;
        try
        {
            Assert.Equal(ErrorCodes.ValidationFailed, (await Assert.ThrowsAsync<PhaketException>(() =>
                service.AddShortcutAsync(stray, User, Shortcut(), consent: true, TestContext.Current.CancellationToken))).Error.Code);
            Assert.Empty(Directory.GetFileSystemEntries(stray));
        }
        finally { Directory.Delete(stray, true); }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ControlCharactersInTheEntryAreRefusedBeforeAnyWrite()
    {
        var path = Fixture(out var root);
        var service = new SteamShortcutService(() => false);
        foreach (var bad in new[] { Shortcut("Gra\nDruga"), new SteamShortcut("Gra", "\"/gry/a.exe\"\r", "\"/gry\"", ""), Shortcut(" ") })
            Assert.Equal(ErrorCodes.ValidationFailed, (await Assert.ThrowsAsync<PhaketException>(() =>
                service.AddShortcutAsync(root, User, bad, consent: true, TestContext.Current.CancellationToken))).Error.Code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SteamUsersAndRootsComeFromTheRealLayout()
    {
        var path = Fixture(out var root);
        Assert.Equal([User], SteamShortcutService.SteamUsers(root)); // "ac" is Steam's account cache
        Assert.Equal(User, SteamShortcutService.DefaultUser(root));
        Assert.Equal(User, SteamShortcutService.DefaultUser(root, User));
        Assert.Null(SteamShortcutService.DefaultUser(root, "123")); // unknown user: no write target
        Assert.Equal(Path.Combine(root, "userdata", User, "config", "shortcuts.vdf"), path);

        // Native and Flatpak roots are both found; a folder without userdata is not a Steam root.
        var home = Directory.CreateTempSubdirectory("phaket-home-").FullName;
        try
        {
            var native = Path.Combine(home, ".local", "share", "Steam");
            Directory.CreateDirectory(Path.Combine(native, "userdata", User));
            var flatpak = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
            Directory.CreateDirectory(Path.Combine(flatpak, "userdata", User));
            // Both roots have a user; a root without one is not found.
            Assert.Equal([native, flatpak], SteamShortcutService.FindSteamRoots("linux", home));
            Assert.Equal([User], SteamShortcutService.SteamUsers(flatpak));
        }
        finally { Directory.Delete(home, true); }
    }

    [Theory]
    [InlineData("pl-PL", "zgody")]
    [InlineData("en", "consent")]
    public void ConsentMessageIsLocalized(string locale, string fragment) =>
        Assert.Contains(fragment, new PhaketException(ErrorCodes.SteamShortcutConsentRequired).LocalizedMessage(locale));

    [Fact]
    public void BothErrorLocalesCarryTheNewCodes()
    {
        foreach (var code in new[] { ErrorCodes.SteamShortcutConsentRequired, ErrorCodes.SteamShortcutsCorrupt })
        {
            Assert.True(ErrorMessages.TryGetTemplate(code, "pl-PL", out var pl), code);
            Assert.True(ErrorMessages.TryGetTemplate(code, "en", out var en), code);
            Assert.Contains("Phaket", pl);
            Assert.Contains("Phaket", en);
        }
    }
}