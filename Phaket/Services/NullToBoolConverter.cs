using System.Globalization;
using Avalonia.Data.Converters;

namespace Phaket.Services;

public sealed class NullToBoolConverter : IValueConverter
{
    public static NullToBoolConverter Instance { get; } = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
