namespace Phaket.Core.Omrl;

// Deterministic metadata fallback (spec §11): requested -> en -> pl-PL -> first
// available (ordinal). Missing translations never block planning or installation.
public static class LocalizedText
{
    public static string? Resolve(IReadOnlyDictionary<string, string>? texts, string locale)
    {
        if (texts is null || texts.Count == 0) return null;
        if (texts.TryGetValue(locale, out var value)) return value;
        if (texts.TryGetValue("en", out value)) return value;
        if (texts.TryGetValue("pl-PL", out value)) return value;
        return texts.OrderBy(pair => pair.Key, StringComparer.Ordinal).First().Value;
    }
}
