using System.Globalization;
using Avalonia.Data.Converters;

namespace Phaket.Services;

// Resolves a localization key to the current-language string (for bindings
// where the key itself is the bound value, e.g. compatibility status).
public sealed class LocKeyConverter : IValueConverter
{
    public static LocKeyConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? LocalizationService.Instance[key] : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
