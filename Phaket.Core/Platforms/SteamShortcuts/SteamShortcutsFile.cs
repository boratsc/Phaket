using System.Text;

namespace Phaket.Core.Platforms.SteamShortcuts;

// shortcuts.vdf for one Steam user: the "shortcuts" dictionary maps "0", "1", … to entries.
// No dependency, no execution; reads and writes Valve's binary KeyValues via BinaryVdf, whose
// writer is byte-identical to steam-shortcut-editor (MIT), the library Heroic writes these files
// with. Field order and ids follow Heroic's nonesteamgame.ts / steamhelper.ts.
public static class SteamShortcutsFile
{
    public const string RootKey = "shortcuts";

    // Marks a Phaket-managed entry; Steam shows it as an ordinary collection.
    public const string Tag = "phaket";

    // An empty grid image next to Steam's default, so Steam draws the shortcut title instead of
    // a borrowed logo (the same bytes as Heroic's transparentSteamLogoHex: 1x1 RGBA8 PNG).
    public static byte[] TransparentLogoPng { get; } = Convert.FromHexString(
        "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000b494441541895636000020000050001d69f16cd0000000049454e44ae426082");

    // CRC32 of the app name followed by the executable path, as Steam computes it (Heroic,
    // steam-rom-manager both hash appname + exe): the low bit forced on, the sign bit set.
    // Deterministic, so re-adding never duplicates an entry.
    public static int GenerateShortcutId(string exe, string appName) => unchecked((int)(Crc32(Encoding.UTF8.GetBytes(appName + exe)) | 0x80000000u));

    // Steam's 64-bit big-picture library id for a shortcut: the id above shifted up, | 0x02000000.
    public static long GenerateAppId(string exe, string appName) =>
        ((long)unchecked((uint)GenerateShortcutId(exe, appName)) << 32) | 0x02000000;

    // The short id Steam uses for the other grids (Heroic's generateShortAppId): the top 32 bits of
    // GenerateAppId, i.e. the signed shortcut id itself.
    public static int GenerateShortAppId(string exe, string appName) => GenerateShortcutId(exe, appName);

    public static BinaryVdfNode Parse(byte[] content)
    {
        if (content.Length == 0) return NewRoot();
        // Content we cannot read is refused, never replaced: the user's other shortcuts live there.
        var root = BinaryVdf.Parse(content);
        if (root.GetNode(RootKey) is null) throw new FormatException("Not a shortcuts.vdf root");
        return root;
    }

    public static IReadOnlyList<BinaryVdfNode> Entries(BinaryVdfNode root) =>
        root.GetNode(RootKey)?.Members.Where(m => m.Node is not null).Select(m => m.Node!).ToArray()
        ?? throw new FormatException("Missing \"shortcuts\" section");

    public static bool IsPhaket(BinaryVdfNode entry) =>
        entry.GetNode("tags")?.Members.Any(m =>
            m.RawValue is { } value && string.Equals(Encoding.UTF8.GetString(value), Tag, StringComparison.OrdinalIgnoreCase)) == true;

    // Appends the entry under the next free index, or replaces the entry with the same appid in
    // place (same exe and name = same game, so re-adding never duplicates). Other entries are
    // not touched, and the replaced entry keeps its own tags: leaving a collection is the user's call.
    public static void Upsert(BinaryVdfNode root, BinaryVdfNode entry)
    {
        var shortcuts = root.GetNode(RootKey) ?? throw new FormatException("Missing \"shortcuts\" section");
        var appId = entry.GetInt32("appid");
        var existing = shortcuts.Members.FirstOrDefault(m => m.Node?.GetInt32("appid") == appId);
        if (existing is null)
        {
            var index = 0;
            while (shortcuts.Members.Any(m => m.Key == index.ToString(System.Globalization.CultureInfo.InvariantCulture))) index++;
            existing = NewDictionary(index.ToString(System.Globalization.CultureInfo.InvariantCulture), new BinaryVdfNode());
            shortcuts.Members.Add(existing);
        }
        var tags = existing.Node!.Find("tags");
        existing.Node!.Members.Clear();
        existing.Node!.Members.AddRange(entry.Members);
        if (tags is not null) existing.Node!.ReplaceOrAdd(tags);
    }

    // Removes the Phaket-tagged entries with these app ids; the others keep their indices and bytes.
    public static int RemovePhaket(BinaryVdfNode root, IReadOnlyCollection<int> appIds) =>
        root.GetNode(RootKey)?.Members.RemoveAll(m => m.Node is not null && IsPhaket(m.Node) && appIds.Contains(m.Node.GetInt32("appid")))
        ?? throw new FormatException("Missing \"shortcuts\" section");

    // The fields of one entry, in the order Heroic writes them. No tags: collections are the
    // user's, so the caller adds the "phaket" tag when it wants the entry listed under it.
    // Exe and StartDir are quoted the way the reference writes them, so a path with spaces
    // ("Program Files") still starts; the id hashes the same quoted values it stores.
    public static BinaryVdfNode CreateEntry(string appName, string exe, string startDir, string launchOptions, string icon = "")
    {
        // The id hashes exactly the values stored below: the quoted ones, like every entry in
        // the file (Steam's own convention, and what the service matches when removing).
        exe = Quote(exe);
        startDir = Quote(startDir);
        var entry = new BinaryVdfNode();
        entry.Set("appid", GenerateShortcutId(exe, appName));
        entry.Set("AppName", appName);
        entry.Set("Exe", exe);
        entry.Set("StartDir", startDir);
        entry.Set("icon", icon);
        entry.Set("ShortcutPath", "");
        entry.Set("LaunchOptions", launchOptions);
        entry.Set("IsHidden", 0);
        entry.Set("AllowDesktopConfig", 1);
        entry.Set("AllowOverlay", 1);
        entry.Set("OpenVR", 0);
        entry.Set("Devkit", 0);
        entry.Set("DevkitGameID", "");
        entry.Set("DevkitOverrideAppID", 0);
        entry.Set("LastPlayTime", 0);
        entry.Set("FlatpakAppID", "");
        return entry;
    }

    // The path Steam runs: one pair of quotes, whatever form the caller passed (a raw path,
    // or the quoted value another helper already produced).
    public static string Quote(string path) =>
        path.Length == 0 ? path : "\"" + path.Trim('"') + "\"";

    // Marks the entry as ours: a collection named "phaket" (tags "0", like Heroic writes them).
    public static void TagPhaket(BinaryVdfNode entry) =>
        entry.ReplaceOrAdd(NewDictionary("tags", new BinaryVdfNode
        {
            Members = { new BinaryVdfMember { Kind = BinaryVdfKind.String, Key = "0", RawValue = Encoding.UTF8.GetBytes(Tag) } },
        }));

    private static BinaryVdfNode NewRoot() => new() { Members = { NewDictionary(RootKey, new BinaryVdfNode()) } };

    private static BinaryVdfMember NewDictionary(string key, BinaryVdfNode node) =>
        new() { Kind = BinaryVdfKind.Dictionary, Key = key, Node = node };

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}