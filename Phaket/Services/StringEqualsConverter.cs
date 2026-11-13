using System.Globalization;
using Avalonia.Data.Converters;

namespace Phaket.Services;

// True when the bound value equals the converter parameter (nav active state).
public sealed class StringEqualsConverter : IValueConverter
{
    public static StringEqualsConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
