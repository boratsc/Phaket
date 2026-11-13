using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Accounts;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class AccountsViewModel : ViewModelBase
{
    private readonly IAccountService service;

    [ObservableProperty] private bool isLoggedIn;
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string userId = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isBusy;

    public bool CanLogin => !IsBusy;
    public bool CanLogout => !IsBusy && IsLoggedIn;

    public AccountsViewModel() : this(new DiscordAccountService()) { }

    public AccountsViewModel(IAccountService service)
    {
        this.service = service;
        service.AccountChanged += OnAccountChanged;
        UpdateAccount();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLogin));
        OnPropertyChanged(nameof(CanLogout));
    }

    partial void OnIsLoggedInChanged(bool value) => OnPropertyChanged(nameof(CanLogout));

    private void OnAccountChanged() => Dispatcher.UIThread.Post(UpdateAccount);

    private void UpdateAccount()
    {
        var account = service.Current;
        IsLoggedIn = account is not null;
        Username = account?.Username ?? "";
        UserId = account?.UserId ?? "";
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        IsBusy = true;
        StatusMessage = LocalizationService.Instance["Account.Waiting"];
        try
        {
            await service.LoginAsync();
            UpdateAccount();
            StatusMessage = LocalizationService.Instance["Account.Success"];
        }
        catch (OperationCanceledException)
        {
            StatusMessage = LocalizationService.Instance["Account.Cancelled"];
        }
        catch (Exception)
        {
            StatusMessage = LocalizationService.Instance["Account.Error"];
        }
        finally { IsBusy = false; }
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
        catch (OperationCanceledException)
        {
            StatusMessage = LocalizationService.Instance["Account.Cancelled"];
        }
        catch (Exception)
        {
            StatusMessage = LocalizationService.Instance["Account.Error"];
        }
        finally { IsBusy = false; }
    }
}
