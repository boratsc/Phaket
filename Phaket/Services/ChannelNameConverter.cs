using System.Globalization;
using Avalonia.Data.Converters;

namespace Phaket.Services;

// The server names default channels with language-neutral ids ("general", "help"); the chat
// shows the reader's translation for the known ones and the server-provided name for the rest.
// shortcut: the label refreshes with the channel list, not on the language-change event;
// upgrade if channels stay on screen while the UI language is switched.
public sealed class ChannelNameConverter : IValueConverter
{
    public static ChannelNameConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string name && KeyOf(name) is { } key ? LocalizationService.Instance[key]
        : value as string ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static string? KeyOf(string name) => name.Trim().ToLowerInvariant() switch
    {
        "general" => "Channels.general",
        "help" => "Channels.help",
        _ => null
    };
}
