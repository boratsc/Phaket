using System.Globalization;
using Avalonia.Data.Converters;

namespace Phaket.Services;

// "{count} {form}" for a counted noun (PAK-89): the bound values are the count and
// the localized one/few/many forms, so the text follows the active language.
// The Polish few rule (last digit 2-4, but not 12-14) also fits English, where
// the few and many forms are the same word.
public sealed class PluralCountConverter : IMultiValueConverter
{
    public static PluralCountConverter Instance { get; } = new();

    public static int FormIndex(int count) =>
        count == 1 ? 0
        : count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14) ? 1
        : 2;

    // Same "{count} {form}" text as the converter, for view models that compose it with other fields.
    public static string Label(int count) =>
        $"{count} {LocalizationService.Instance[FormIndex(count) switch
        {
            0 => "Catalog.ModsCount.One",
            1 => "Catalog.ModsCount.Few",
            _ => "Catalog.ModsCount.Many"
        }]}";

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count >= 4 && values[0] is int count && values[FormIndex(count) + 1] is string form
            ? $"{count} {form}"
            : null;
}