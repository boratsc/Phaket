using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Phaket.Core.Accounts;
using Phaket.Services;
using Phaket.Views;

namespace Phaket.ViewModels;

public partial class AccountsViewModel : ViewModelBase
{
    public ProviderAccountViewModel[] Providers { get; }
    private readonly EpicAccountService epicService;
    private readonly ProviderAccountViewModel epicProvider;
    private EpicAuthWindow? epicAuth;

    public AccountsViewModel()
    {
        string clientId = ProviderAccountViewModel.LoadClientId();
        // Epic sign-in runs in an embedded WebView dialog (with a manual
        // fallback) instead of a bare system-browser tab.
        epicService = new EpicAccountService(null, url => Dispatcher.UIThread.Post(() => OpenEpicAuth(url)));
        Providers =
        [
            new(new DiscordAccountService(), "Discord", nameof(MaterialIconKind.Chat), "Account.Login"),
            new(new SteamAccountService(), "Steam", nameof(MaterialIconKind.Steam), "Account.LoginSteam"),
            new(epicService, "Epic Games", nameof(MaterialIconKind.GamepadVariant), "Account.LoginEpic"),
            new(new ItchAccountService(clientId), "itch.io", nameof(MaterialIconKind.Store), "Account.LoginItch", clientId)
        ];
        epicProvider = Providers[2];
    }

    private void OpenEpicAuth(string url)
    {
        var vm = new EpicAuthViewModel(url,
            code => { try { epicService.SubmitCode(code); } catch (Exception) { /* login already finished or cancelled */ } },
            () => epicAuth?.Close());
        epicAuth = new EpicAuthWindow(vm);
        epicAuth.Closed += (_, _) =>
        {
            if (!vm.CodeSubmitted) epicProvider.CancelLogin();
            epicAuth = null;
        };
        epicAuth.Show();
    }
}

public partial class ProviderAccountViewModel : ViewModelBase
{
    private IAccountService service;
    private static string ClientIdPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "itch-clientid.txt");
    public string DisplayName { get; }
    public MaterialIconKind IconKind { get; }
    public string LoginKey { get; }
    public string LoginText => LocalizationService.Instance[LoginKey];
    public bool IsSteam => service.Provider == "steam";
    public bool IsItch => service.Provider == "itch";
    public string? LocalPersona { get; }
    public bool HasLocal => LocalPersona is not null;
    public bool CanLogin => !IsBusy && !IsLoggedIn;
    public bool CanLogout => !IsBusy && IsLoggedIn;

    [ObservableProperty] private bool isLoggedIn;
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool needsCode;
    [ObservableProperty] private string authorizationCode = "";
    [ObservableProperty] private string clientId = "";
    private CancellationTokenSource? loginCts;

    internal void CancelLogin() => loginCts?.Cancel();

    public ProviderAccountViewModel(IAccountService service, string displayName, string iconKind, string loginKey)
        : this(service, displayName, iconKind, loginKey, service.Provider == "itch" ? LoadClientId() : "") { }

    internal ProviderAccountViewModel(IAccountService service, string displayName, string iconKind, string loginKey, string clientId)
    {
        this.service = service;
        DisplayName = displayName;
        IconKind = Enum.Parse<MaterialIconKind>(iconKind);
        LoginKey = loginKey;
        this.clientId = clientId;
        LocalPersona = IsSteam ? SteamAccountService.DetectLocal()?.Username : null;
        service.AccountChanged += OnAccountChanged;
        UpdateAccount();
    }

    internal static string LoadClientId()
    {
        try { return File.ReadAllText(ClientIdPath).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    partial void OnClientIdChanged(string value)
    {
        if (!IsItch) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClientIdPath)!);
            string temp = Path.Combine(Path.GetDirectoryName(ClientIdPath)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try { File.WriteAllText(temp, value); File.Move(temp, ClientIdPath, true); }
            finally { File.Delete(temp); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusMessage = LocalizationService.Instance["Account.Error"]; }
    }

    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanLogin)); OnPropertyChanged(nameof(CanLogout)); }
    partial void OnIsLoggedInChanged(bool value) { OnPropertyChanged(nameof(CanLogin)); OnPropertyChanged(nameof(CanLogout)); }
    private void OnAccountChanged() => Dispatcher.UIThread.Post(UpdateAccount);
    private void UpdateAccount()
    {
        IsLoggedIn = service.Current is not null;
        Username = service.Current?.Username ?? "";
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsItch && string.IsNullOrWhiteSpace(ClientId))
        { StatusMessage = LocalizationService.Instance["Account.ItchNoClient"]; return; }
        IsBusy = true;
        StatusMessage = LocalizationService.Instance["Account.Waiting"];
        loginCts = new CancellationTokenSource();
        try
        {
            if (IsItch)
            {
                service.AccountChanged -= OnAccountChanged;
                service = new ItchAccountService(ClientId.Trim());
                service.AccountChanged += OnAccountChanged;
            }
            await service.LoginAsync(loginCts.Token);
            UpdateAccount();
            StatusMessage = LocalizationService.Instance["Account.Success"];
        }
        catch (OperationCanceledException) { StatusMessage = LocalizationService.Instance["Account.Cancelled"]; }
        catch (Exception) { StatusMessage = LocalizationService.Instance["Account.Error"]; }
        finally { NeedsCode = false; IsBusy = false; }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        IsBusy = true;
        try
        {
            await service.LogoutAsync();
            UpdateAccount();
            StatusMessage = LocalizationService.Instance["Account.LoggedOut"];
        }
        catch (OperationCanceledException) { StatusMessage = LocalizationService.Instance["Account.Cancelled"]; }
        catch (Exception) { StatusMessage = LocalizationService.Instance["Account.Error"]; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void SubmitCode()
    {
        try
        {
            ((EpicAccountService)service).SubmitCode(AuthorizationCode);
            AuthorizationCode = "";
            NeedsCode = false;
            StatusMessage = LocalizationService.Instance["Account.Waiting"];
        }
        catch (Exception) { StatusMessage = LocalizationService.Instance["Account.Error"]; }
    }

    [RelayCommand]
    private async Task UseLocalAsync()
    {
        IsBusy = true;
        try
        {
            var account = await ((SteamAccountService)service).AdoptLocalAsync();
            UpdateAccount();
            StatusMessage = LocalizationService.Instance[account is null ? "Account.SteamNoLocal" : "Account.Success"];
        }
        catch (OperationCanceledException) { StatusMessage = LocalizationService.Instance["Account.Cancelled"]; }
        catch (Exception) { StatusMessage = LocalizationService.Instance["Account.Error"]; }
        finally { IsBusy = false; }
    }
}
