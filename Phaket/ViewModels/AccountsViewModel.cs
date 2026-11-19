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

    // Steam sign-in for installs (PAK-60): status, one-time sign-in dialog, deletion.
    public SteamSigninDialogViewModel SteamSignin { get; }
    public DeviceIdentityViewModel DeviceIdentity { get; }
    // The signed profile publication client the profiles page publishes with; created once per window.
    public IProfilePublishService ProfilePublisher { get; }

    public AccountsViewModel() : this(new AccountServices()) { }

    public AccountsViewModel(AccountServices accounts)
    {
        string clientId = ProviderAccountViewModel.LoadClientId();
        // Epic sign-in runs in an embedded WebView dialog (with a manual
        // fallback) instead of a bare system-browser tab.
        epicService = accounts.Epic;
        SteamSignin = new SteamSigninDialogViewModel(accounts.SteamCredentials);
        var identityServices = DeviceIdentityServices.Current is { } configured && ReferenceEquals(configured.KeyStore, accounts.DeviceKeys)
            ? configured : new DeviceIdentityServices(accounts.DeviceKeys, new DeviceKeySigner(accounts.DeviceKeys));
        var signer = DeviceIdentityServices.Current is null ? new DeviceKeySigner(accounts.DeviceKeys, registerBeforeSigning: false) : identityServices.Signer;
        ProfilePublisher = identityServices.CreateProfilePublisher(CatalogSettings.Load());
        DeviceIdentity = new DeviceIdentityViewModel(accounts.DeviceKeys, signer,
            ct => identityServices.RegisterDeviceAsync(CatalogSettings.Load(), ct));
        _ = SteamSignin.RefreshCommand.ExecuteAsync(null);
        accounts.OpenEpicAuth += url => Dispatcher.UIThread.Post(() => OpenEpicAuth(url));
        Providers =
        [
            new(new DiscordAccountService(), "Discord", nameof(MaterialIconKind.Chat), "Account.Login"),
            new(accounts.Steam, "Steam", nameof(MaterialIconKind.Steam), "Account.LoginSteam"),
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

public partial class DeviceIdentityViewModel : ViewModelBase
{
    private readonly DeviceKeyStore keyStore;
    private readonly Func<CancellationToken, Task> registerDevice;
    [ObservableProperty] private string nick = "";
    [ObservableProperty] private string fingerprint = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isOnline;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isLoaded;

    public Task Initialization { get; }
    public bool HasFingerprint => !string.IsNullOrEmpty(Fingerprint);
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    public bool CanSave => IsLoaded && !IsBusy;
    public bool CanRegister => IsLoaded && !IsBusy;

    public DeviceIdentityViewModel(DeviceKeyStore keyStore, ISignedRequestSigner signer,
        Func<CancellationToken, Task> registerDevice)
    {
        this.keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        ArgumentNullException.ThrowIfNull(signer);
        this.registerDevice = registerDevice ?? throw new ArgumentNullException(nameof(registerDevice));
        IsOnline = keyStore.IsOnline;
        keyStore.RegistrationStateChanged += OnRegistrationStateChanged;
        Initialization = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            DevicePublicIdentity identity = await keyStore.GetPublicIdentityAsync();
            Nick = identity.Nick;
            Fingerprint = identity.Fingerprint;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Device identity is unavailable: {0}", ex);
            StatusMessage = Loc("Identity.Error.KeyStore");
        }
        finally
        {
            IsLoaded = true;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanRegister));
        }
    }

    [RelayCommand]
    private async Task SaveNickAsync()
    {
        if (!CanSave) return;
        string value = Nick.Trim();
        if (!DeviceKeyStore.IsValidNick(value))
        {
            StatusMessage = Loc("Identity.Error.Nick");
            return;
        }
        try
        {
            await keyStore.SaveNickAsync(value);
            Nick = value;
            StatusMessage = Loc("Identity.Saved");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Device nick could not be saved: {0}", ex);
            StatusMessage = Loc("Identity.Error.Save");
        }
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (!CanRegister) return;
        IsBusy = true;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRegister));
        try
        {
            await registerDevice(CancellationToken.None);
            StatusMessage = Loc("Identity.Registered");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Device registration failed: {0}", ex);
            keyStore.SetOnline(false);
            StatusMessage = Loc("Identity.Error.Offline");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanRegister));
        }
    }

    private void OnRegistrationStateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => IsOnline = keyStore.IsOnline);

    partial void OnNickChanged(string value) => OnPropertyChanged(nameof(CanSave));
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRegister));
    }
    partial void OnIsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRegister));
    }
    partial void OnFingerprintChanged(string value) => OnPropertyChanged(nameof(HasFingerprint));

    private static string Loc(string key) => LocalizationService.Instance[key];
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
