using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Phaket.InstallerStub;

public sealed record ReleaseEntry
{
    public required string Version { get; init; }
    public required string File { get; init; }
    public required string Sha256 { get; init; }
}

public sealed class Manifest
{
    public required string LatestVersion { get; init; }
    public required string DownloadBaseUrl { get; init; }
    public required IReadOnlyList<ReleaseEntry> Releases { get; init; }
}

public sealed class ManifestException(string message) : Exception(message);

/// <summary>
/// Kontrakt feedu (PAK-110): {success, latestVersion, downloadBaseUrl, releases[{Version, File, SHA256}]}.
/// Parsowanie jest tolerancyjne na wielkość liter i wielokrotne separatory wersji, ale odrzuca
/// absolutne URL-e w polach File/downloadBaseUrl — pobieramy tylko z bazy wskazanej w manifeście.
/// </summary>
public static class ManifestParser
{
    // 4 MB wystarcza na setki wydań; większa odpowiedź to błąd serwera, nie manifest.
    internal const int MaxBytes = 4 * 1024 * 1024;

    public static Manifest Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ManifestException("E400 manifest");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ManifestException("E400 manifest");
            var root = doc.RootElement;
            if (!TryGet(root, "success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new ManifestException("E401 success");

            var latest = TryGet(root, "latestVersion", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString() ?? "" : "";
            var baseUrl = TryGet(root, "downloadBaseUrl", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? "" : "";
            if (!TryGet(root, "releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
                throw new ManifestException("E402 releases");
            if (!IsWebSiteIfPresent(baseUrl)) throw new ManifestException("E403 downloadBaseUrl");

            var entries = new List<ReleaseEntry>();
            foreach (var item in releases.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var version = StringProp(item, "Version");
                var file = StringProp(item, "File");
                var sha = StringProp(item, "SHA256");
                // Tylko pełne paczki; delta nie zawiera całej aplikacji.
                if (version is null || file is null || sha is null || sha.Length != 64) continue;
                if (!HasNumericCore(version)) continue;
                if (!IsSafeFileName(file) && !IsWebSite(file)) continue;
                entries.Add(new ReleaseEntry { Version = version, File = file, Sha256 = sha.ToUpperInvariant() });
            }

            return new Manifest
            {
                LatestVersion = latest,
                DownloadBaseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/",
                Releases = entries
            };
        }
    }

    /// <summary>Najnowsza paczka instalacyjna według kolejności używanej przez Velopack.</summary>
    public static ReleaseEntry? SelectLatest(Manifest manifest)
    {
        ReleaseEntry? best = null;
        var bestKey = Array.Empty<string>();
        foreach (var entry in manifest.Releases)
        {
            // Pełne paczki (*-full.nupkg). Feed serwera publikuje tylko je, więc dopuszczamy każdą
            // inną paczkę .nupkg tego kanału — wybór nie może się rozjechać z nazwami z serwera.
            if (!entry.File.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                || entry.File.EndsWith("-delta.nupkg", StringComparison.OrdinalIgnoreCase)) continue;
            var key = VersionKey(entry.Version);
            if (best is null || CompareKeys(key, bestKey) > 0) { best = entry; bestKey = key; }
        }
        return best;
    }

    // Velopack sortuje semver: człony numeryczne, potem prerelease (bez prerelease = wyżej),
    // a 1.0.0 == 1.0.0.0. Oba porównania muszą być IDENTYCZNE z SortOrder w Velopack,
    // inaczej stub pobierze inną paczkę niż ta, którą zaoferuje Update.exe.
    internal static string[] VersionKey(string version)
    {
        var plus = version.Split('+')[0];
        var dash = plus.IndexOf('-');
        var numbers = (dash < 0 ? plus : plus[..dash]).Split('.');
        var fields = new string[4];
        for (var i = 0; i < 4; i++)
        {
            var text = i < numbers.Length ? numbers[i] : "0";
            fields[i] = ulong.TryParse(text, out var value) ? value.ToString() : "-1";
        }
        return dash < 0 ? fields : [.. fields, plus[(dash + 1)..] + "\u0001"];
    }

    internal static int CompareKeys(string[] a, string[] b)
    {
        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            if (i >= a.Length) return 1;   // brak prerelease = wydanie stabilne, wyżej
            if (i >= b.Length) return -1;
            int result;
            if (i < 4)
            {
                var x = ulong.Parse(a[i]);
                var y = ulong.Parse(b[i]);
                result = x.CompareTo(y);
            }
            else
            {
                result = string.CompareOrdinal(a[i], b[i]);
            }
            if (result != 0) return Math.Sign(result);
        }
        return 0;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string? StringProp(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        return null;
    }

    // Wersja musi zaczynać się trzema/czterema liczbami — inaczej sortowanie/porównanie rzuca.
    internal static bool HasNumericCore(string version)
    {
        var numbers = version.Split('+')[0].Split('-')[0].Split('.');
        return numbers.Length >= 3 && numbers.All(n => n.Length > 0 && n.All(char.IsAsciiDigit));
    }

    // Nazwa pliku nie może wyjść poza katalog roboczy; pełny HTTPS URL jest dozwolony (CDN paczek).
    internal static bool IsSafeFileName(string file) =>
        file.Length > 0 && file.IndexOfAny(['/', '\\', ':']) < 0 && file is not "." and not "..";

    // Lokalna nazwa pliku do zapisu: ostatni segment (URL albo nazwa); pusta/niebezpieczna → odrzucona.
    internal static string LocalName(string file)
    {
        if (IsSafeFileName(file)) return file;
        if (!IsWebSite(file) || !Uri.TryCreate(file, UriKind.Absolute, out var uri)) return "";
        var name = uri.Segments.Length > 0 ? uri.Segments[^1] : "";
        return IsSafeFileName(name) ? name : "";
    }

    internal static bool IsWebSite(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && url.Length > 0;

    // Puste pole = brak bazy w manifeście (pobieramy z katalogu manifestu); niepuste musi być HTTPS.
    internal static bool IsWebSiteIfPresent(string url) => url.Length == 0 || IsWebSite(url);
}