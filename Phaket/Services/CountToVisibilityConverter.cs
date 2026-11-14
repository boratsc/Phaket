using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Phaket.Services;

// Shows the empty-state hint only when the list is empty.
public sealed class CountToVisibilityConverter : IValueConverter
{
    public static CountToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
