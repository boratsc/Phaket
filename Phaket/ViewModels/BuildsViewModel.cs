using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Catalog;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class BuildsViewModel : ViewModelBase
{
    private readonly Action back;
    private readonly string gameId;
    private static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "catalog.db");

    public BuildsViewModel(GameCatalogItem game, Action back)
    {
        Game = game;
        gameId = game.Definition.Slug;
        this.back = back;
        _ = RefreshCommand.ExecuteAsync(null);
    }

    public GameCatalogItem Game { get; }
    public string GameTitle => Game.Title;
    public ObservableCollection<CatalogBuild> Builds { get; } = [];
    [ObservableProperty] private string endpoint = CatalogSettings.Load(CatalogSettings.DefaultSettingsPath);
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isOffline;
    [ObservableProperty] private string lastSyncText = "—";
    [ObservableProperty] private bool isEmpty;

    [RelayCommand] private void Back() => back();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var address = Endpoint;
        var result = await Task.Run(async () =>
        {
            IReadOnlyList<CatalogBuild> builds = [];
            DateTimeOffset? lastSync = null;
            string? error = null;
            var offline = false;
            try
            {
                using var store = new CatalogStore(CachePath);
                try
                {
                    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        throw new UriFormatException();
                    using var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15) };
                    await new CatalogClient(new HttpCatalogTransport(http), store).FullSyncAsync();
                }
                catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException)
                {
                    offline = true;
                    error = "Catalog.Offline";
                }
                catch (UriFormatException) { error = "Builds.InvalidEndpoint"; }
                catch (Exception) { error = "Builds.SyncError"; }
                builds = store.GetBuilds(gameId);
                lastSync = store.LastSync;
            }
            catch (Exception) { error = "Builds.CacheError"; }
            return (builds, lastSync, error, offline);
        });
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Builds.Clear();
            foreach (var build in result.builds) Builds.Add(build);
            IsEmpty = Builds.Count == 0;
            IsOffline = result.offline;
            StatusMessage = result.error is null ? "" : LocalizationService.Instance[result.error];
            LastSyncText = result.lastSync?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
        });
    }
}

public sealed class BuildStatusBrushConverter : IValueConverter
{
    public static BuildStatusBrushConverter Instance { get; } = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "verified" or "downloadable" => Brushes.LightGreen,
        "known" => Brushes.Khaki,
        "unavailable" => Brushes.LightCoral,
        _ => Brushes.LightGray
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
