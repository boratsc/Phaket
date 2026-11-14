using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phaket.Core.Omrl;

// Locale-aware error messages. Locales are discovered from embedded resources
// (Resources/errors.<locale>.json); adding a locale requires no Core code change.
// Fallback chain: requested locale -> en -> pl-PL -> first available (ordinal).
public static class ErrorMessages
{
    private static readonly Dictionary<string, FrozenDictionary<string, string>> Locales = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    static ErrorMessages()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var match = Regex.Match(name, @"^Phaket\.Core\.Resources\.errors\.(.+)\.json$");
            if (!match.Success) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            Locales[match.Groups[1].Value] = map.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    public static IReadOnlyCollection<string> AvailableLocales
    {
        get { lock (Gate) return Locales.Keys.ToArray(); }
    }

    // Hosts may supply additional locales at runtime without touching Core.
    public static void RegisterLocale(string locale, IReadOnlyDictionary<string, string> messages)
    {
        lock (Gate) Locales[locale] = messages.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static bool TryGetTemplate(string code, string locale, out string template)
    {
        lock (Gate)
        {
            foreach (var candidate in FallbackChain(locale))
            {
                if (Locales.TryGetValue(candidate, out var map) && map.TryGetValue(code, out template!))
                    return true;
            }
        }
        template = code;
        return false;
    }

    public static string Format(string code, string locale, IReadOnlyDictionary<string, string>? parameters = null)
    {
        TryGetTemplate(code, locale, out var template);
        if (parameters is null || parameters.Count == 0) return template;
        return Regex.Replace(template, @"\{([a-zA-Z0-9_]+)\}", m =>
            parameters.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
    }

    public static IReadOnlySet<string> Keys(string locale)
    {
        lock (Gate) return Locales.TryGetValue(locale, out var map)
            ? map.Keys.ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;
    }

    private static IEnumerable<string> FallbackChain(string locale)
    {
        yield return locale;
        if (!string.Equals(locale, "en", StringComparison.Ordinal)) yield return "en";
        if (!string.Equals(locale, "pl-PL", StringComparison.Ordinal)) yield return "pl-PL";
        string[] all;
        lock (Gate) all = Locales.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        foreach (var key in all) yield return key;
    }
}
