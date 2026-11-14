using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Phaket.Services;

// Only the upstream CDN is trusted: catalog metadata must not probe local services.
public sealed class CatalogIcon : Image
{
    public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<CatalogIcon, string?>(nameof(Url));
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Limit = new(6);
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new();
    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty) _ = LoadAsync(Url);
    }
    private async Task LoadAsync(string? url)
    {
        Source = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "ccdn.thunderstore.io" || !uri.IsDefaultPort) return;
        // Bound the process cache; visible recycled rows can reload after eviction.
        if (Cache.Count > 256) Cache.Clear();
        var image = await Cache.GetOrAdd(url!, _ => DownloadAsync(uri));
        await Dispatcher.UIThread.InvokeAsync(() => { if (Url == url) Source = image; });
    }
    private static async Task<Bitmap?> DownloadAsync(Uri uri)
    {
        await Limit.WaitAsync();
        try {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 2_000_000) return null;
            await using var input = await response.Content.ReadAsStreamAsync();
            using var data = new MemoryStream();
            var buffer = new byte[16384]; int n;
            while ((n = await input.ReadAsync(buffer)) > 0) { if (data.Length + n > 2_000_000) return null; data.Write(buffer, 0, n); }
            data.Position = 0;
            return new Bitmap(data);
        } catch (Exception) { return null; }
        finally { Limit.Release(); }
    }
}
