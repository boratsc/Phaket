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
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("Phaket.Assets.i18n.", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
                continue;
            var locale = name["Phaket.Assets.i18n.".Length..^".json".Length];
            using var stream = assembly.GetManifestResourceStream(name)!;
            locales[locale] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
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
