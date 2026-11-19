using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace Phaket.Services;

// UI localization (PAK-16): pl-PL default, en full parity. All user-visible
// strings come from resources; switching language raises indexer change
// notifications so every binding updates without rebuild or data loss.
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> locales = new(StringComparer.Ordinal);
    private string currentLanguage = "pl-PL";

    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string prefix = "Phaket.Assets.i18n.";
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(name => name.Contains(".d.", StringComparison.Ordinal) ? 1 : 0)
                     .ThenBy(name => name, StringComparer.Ordinal))
        {
            var resourcePath = name[prefix.Length..^".json".Length];
            var satelliteIndex = resourcePath.IndexOf(".d.", StringComparison.Ordinal);
            var locale = satelliteIndex < 0 ? resourcePath : resourcePath[..satelliteIndex];
            if (!locales.TryGetValue(locale, out var map))
                locales[locale] = map = new Dictionary<string, string>(StringComparer.Ordinal);

            using var stream = assembly.GetManifestResourceStream(name)!;
            var resource = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            foreach (var (key, value) in resource)
            {
                if (map.ContainsKey(key))
                    System.Diagnostics.Trace.TraceWarning("Duplicate localization key '{0}' in {1}; later resource wins.", key, name);
                map[key] = value;
            }
        }
    }

    public IReadOnlyList<string> AvailableLanguages => locales.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    public string CurrentLanguage
    {
        get => currentLanguage;
        set
        {
            if (currentLanguage == value || !locales.ContainsKey(value)) return;
            currentLanguage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    // Indexer for XAML: {Binding [Key], Source={x:Static services:LocalizationService.Instance}}
    public string this[string key]
    {
        get
        {
            if (locales.TryGetValue(currentLanguage, out var map) && map.TryGetValue(key, out var value))
                return value;
            if (locales.TryGetValue("en", out var en) && en.TryGetValue(key, out var enValue))
                return enValue;
            if (locales.TryGetValue("pl-PL", out var pl) && pl.TryGetValue(key, out var plValue))
                return plValue;
            return key;
        }
    }

    public IReadOnlySet<string> Keys(string locale) =>
        locales.TryGetValue(locale, out var map) ? map.Keys.ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
}
