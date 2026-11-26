namespace Phaket.Core.Platforms.Aurelia;

// Read-only allowlist for the Aurelia driver (PAK-137). Aurelia writes Steam's appmanifest and
// libraryfolders.vdf for install/move/relink/import and can clash with a running client, so the
// refusal lives here, before a process starts, independently of what the tool itself allows.
public static class AureliaContract
{
    // A Steam app or depot id: digits only, so no branch password, path or flag value gets through.
    public static string AppId(string value) => value.Length is > 0 and <= 10 && value.All(char.IsAsciiDigit)
        ? value
        : throw new ArgumentException("Aurelia app id must be a decimal number", nameof(value));

    public static string DepotId(string value) => AppId(value);

    // The argv for one read-only command, `--json` last (Aurelia accepts the global flag anywhere).
    // Every other command is an ArgumentException with nothing executed.
    public static string[] Capabilities() => ["capabilities", "--json"];

    public static string[] List() => ["list", "--json"];

    public static string[] Manifests(string appId, string? depotId = null) => depotId is null
        ? ["manifests", AppId(appId), "--json"]
        : ["manifests", AppId(appId), "--depot", DepotId(depotId), "--json"];

    public static string[] Branches(string appId) => ["branches", AppId(appId), "--json"];

    public static string[] Version() => ["--version", "--json"];
}