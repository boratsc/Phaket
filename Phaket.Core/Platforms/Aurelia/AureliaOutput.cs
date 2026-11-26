using System.Globalization;
using System.Text.Json;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms.Aurelia;

// One typed Aurelia failure. `Hint` is Aurelia's own English suggestion and stays out of the
// localized messages: it is diagnostic detail, not product text.
public sealed record AureliaFailure(string? Type, string Message, string? Hint, int ExitCode);

public sealed record AureliaCapabilities(string Version, int Schema, IReadOnlyList<string> Commands, IReadOnlyList<string> Features)
{
    // Every version Phaket has been written against reports schema 1; a newer contract is not
    // guessed at, it is left to the local detection.
    public const int SupportedSchema = 1;

    public static readonly string[] RequiredCommands = ["list", "manifests", "branches", "capabilities"];

    public bool IsSupported =>
        Version is { Length: > 0 } && Schema == SupportedSchema &&
        RequiredCommands.All(required => Commands.Contains(required, StringComparer.Ordinal));

    // Capabilities is a bare document (printed pretty, not as a `result` event): read it whole.
    public static AureliaCapabilities? Parse(string text)
    {
        foreach (var value in AureliaJson.Values(AureliaJson.StripAnsi(text)).Reverse())
        {
            // A document without a schema number is not a capabilities report (an error line is not).
            if (value is not { ValueKind: JsonValueKind.Object } document || !document.TryGetProperty("schema", out var s) || !s.TryGetInt32(out var schema)) continue;
            // `commands` is a map {name: detail}; `features` is a list of behaviours flags don't reveal.
            var commands = document.TryGetProperty("commands", out var c) && c.ValueKind == JsonValueKind.Object ? c.EnumerateObject().Select(p => p.Name).ToArray() : [];
            var features = document.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                : [];
            return new AureliaCapabilities(AureliaOutput.String(document, "version") ?? "", schema, commands, features);
        }
        return null;
    }
}

// The fields a list row must carry for this driver; missing ones mean the contract moved.
public static class AureliaListFields
{
    public static readonly string[] Required = ["app_id", "name", "is_installed", "update_available", "is_owned", "is_family_shared", "active_branch"];

    public static bool Missing(JsonElement row) => Required.Any(field => !row.TryGetProperty(field, out _));
}

public static class AureliaOutput
{
    // The value a command is read from: the last event-tagged value on stdout (its one `result`
    // line), else the last stdout value (a bare array/object command), else the last stderr value
    // (a typed error). An `error` line is a failure, never a result.
    public static JsonElement? Result(AureliaRunResult run)
    {
        string stdout = AureliaJson.StripAnsi(run.Stdout);
        var values = AureliaJson.Values(stdout);
        return values.LastOrDefault(value => value is { ValueKind: JsonValueKind.Object } element && String(element, "event") is not null)
            ?? Bare(stdout)
            ?? AureliaJson.Last(AureliaJson.StripAnsi(run.Stderr));
    }

    // The command's own answer: the last value that is no event and no failure line (a bare array,
// or a single object).
    private static bool IsFailure(JsonElement document) =>
        document.TryGetProperty("error", out _) || String(document, "event") == "error"
        || document.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False;

    private static JsonElement? Bare(string stdout) => AureliaJson.Values(stdout).LastOrDefault(value => value is { ValueKind: JsonValueKind.Array }
        || value is { ValueKind: JsonValueKind.Object } document && String(document, "event") is null && !IsFailure(document));

    // A failure is an `error` line, an `{"event":"error"}` event, or an `ok:false` result.
    public static AureliaFailure? Failure(AureliaRunResult run)
    {
        if (Result(run) is not { ValueKind: JsonValueKind.Object } document || !IsFailure(document)) return null;
        return new AureliaFailure(
            String(document, "type"),
            String(document, "error") is { Length: > 0 } error ? error : String(document, "message") ?? "",
            String(document, "hint"),
            run.ExitCode);
    }

    // Aurelia's snake_case error type on a Phaket code; an unknown one is the tool's generic failure.
    public static string Code(string? type) => type switch
    {
        "offline" => ErrorCodes.AureliaOffline,
        "auth_required" or "login_denied" or "login_expired" => ErrorCodes.AureliaAuthRequired,
        "timeout" or "network_timeout" => ErrorCodes.AureliaTimeout,
        "cancelled" or "input_closed" => ErrorCodes.AureliaCancelled,
        "rate_limited" => ErrorCodes.AureliaRateLimited,
        _ => ErrorCodes.AureliaFailed
    };

    // Rows of a top-level array, or of the named array a command wraps them in.
    public static IReadOnlyList<JsonElement> Rows(JsonElement? root, string arrayProperty) => root?.ValueKind switch
    {
        JsonValueKind.Array => root.Value.EnumerateArray().ToArray(),
        JsonValueKind.Object when root.Value.TryGetProperty(arrayProperty, out var array) && array.ValueKind == JsonValueKind.Array => array.EnumerateArray().ToArray(),
        _ => []
    };

    public static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // `playtime_forever_minutes` is null for a title with no record; an oversized playtime
    // saturates instead of throwing.
    public static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? (int)Math.Clamp(number, 0, int.MaxValue)
            : null;

    public static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    // Steam reports ids as numbers on one path and strings on another (Heroic's FIXME); both
    // become the same decimal text.
    public static string? Id(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetUInt64(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null,
            _ => null
        };
    }

    }