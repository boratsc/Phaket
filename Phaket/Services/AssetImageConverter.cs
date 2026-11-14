using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Phaket.Services;

public sealed class AssetImageConverter : IValueConverter
{
    public static AssetImageConverter Instance { get; } = new();
    private readonly Dictionary<string, Bitmap> images = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !Uri.TryCreate(path, UriKind.Absolute, out var uri) || !AssetLoader.Exists(uri)) return null;
        if (!images.TryGetValue(path, out var image))
        {
            using var stream = AssetLoader.Open(uri);
            images[path] = image = new Bitmap(stream);
        }
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
