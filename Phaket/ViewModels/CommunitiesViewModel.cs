using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;

namespace Phaket.ViewModels;

public sealed record CommunityAccessModeOption(string Value, string Label);

public sealed class CommunityItemViewModel : ViewModelBase
{
    public CommunityItemViewModel(CommunitySummary model) => Model = model;

    public CommunitySummary Model { get; private set; }
    public string Slug => Model.Slug;
    public string Name => Model.Name;
    public string Description => Model.Description;
    public string AccessModeText => LocalizationService.Instance[Model.AccessMode switch
    {
        CommunityAccessModes.Public => "Communities.AccessPublic",
        CommunityAccessModes.Password => "Communities.AccessPassword",
        CommunityAccessModes.InviteCode => "Communities.AccessInviteCode",
        CommunityAccessModes.PasswordOrInviteCode => "Communities.AccessPasswordOrInviteCode",
        _ => "Communities.AccessUnknown"
    }];
    public string MemberCountText => string.Format(LocalizationService.Instance["Communities.MemberCount"], Model.MemberCount);
    public bool IsMember => Model.IsMember;
    public bool CanJoin => !IsMember && CommunityAccessModes.IsKnown(Model.AccessMode);
    public bool HasPackage => !string.IsNullOrWhiteSpace(Model.PackageSlug);
    public string PackageName => Package?.Name ?? Model.PackageSlug ?? LocalizationService.Instance["Communities.NoPackage"];
    public bool HasChannels => Channels.Count > 0;
    public bool HasPackageStatus => HasPackage;
    public string ProfileCompatibilityText => LocalizationService.Instance[ProfileStatusKey];
    public ObservableCollection<CommunityChannel> Channels { get; } = [];
    public ModpackDefinition? Package { get; private set; }
    private string ProfileStatusKey => !HasPackage ? "Communities.NoPackage" : packageUnavailable
        ? "Communities.PackageUnavailable" : profileMatches == true ? "Communities.ProfileMatch" : profileMatches == false
            ? "Communities.ProfileMismatch" : "Communities.ProfileChecking";
    private bool packageUnavailable;
    private bool? profileMatches;

    public void SetSummary(CommunitySummary model)
    {
        Model = model;
        RefreshLocalized();
    }

    public void SetChannels(IEnumerable<CommunityChannel> channels)
    {
        Channels.Clear();
        foreach (var channel in channels) Channels.Add(channel);
        OnPropertyChanged(nameof(HasChannels));
    }

    public void SetPackage(ModpackDefinition? package, bool? profileMatch, bool unavailable = false)
    {
        Package = package;
        profileMatches = profileMatch;
        packageUnavailable = unavailable;
        OnPropertyChanged(nameof(PackageName));
        OnPropertyChanged(nameof(ProfileCompatibilityText));
        OnPropertyChanged(nameof(HasPackageStatus));
    }

    public void RefreshLocalized()
    {
        OnPropertyChanged(nameof(AccessModeText));
        OnPropertyChanged(nameof(MemberCountText));
        OnPropertyChanged(nameof(IsMember));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(HasPackage));
        OnPropertyChanged(nameof(PackageName));
        OnPropertyChanged(nameof(ProfileCompatibilityText));
    }
}

public sealed class CommunityMessageViewModel(CommunityMessage message)
{
    public long Id => message.Id;
    public string Author => string.IsNullOrWhiteSpace(message.AuthorNickname)
        ? LocalizationService.Instance["Chat.UnknownAuthor"] : message.AuthorNickname;
    public string KeyShort => string.IsNullOrWhiteSpace(message.AuthorKey)
        ? LocalizationService.Instance["Chat.UnknownKey"]
        : message.AuthorKey.Length <= 12 ? message.AuthorKey : message.AuthorKey[..6] + "…" + message.AuthorKey[^4..];
    public string Content => message.Content;
    public string Timestamp => message.CreatedAt.ToLocalTime().ToString("t");
}

public partial class CommunitiesViewModel : ViewModelBase
{
    private readonly ICommunitiesApiClient api;
    private readonly IModpackApiClient packages;
    private readonly string appDataRoot;
    private readonly TimeSpan chatPollInterval;
    private CancellationTokenSource? searchCancellation;
    private CancellationTokenSource? selectedLoadCancellation;
    private CancellationTokenSource? channelPollingCancellation;
    private Task? refreshTask;
    private long chatCursor;
    private int selectionGeneration;
    private int consecutivePollFailures;
    private bool hasLoaded;

    // Review Ś5: a server that keeps failing must not be polled forever.
    internal const int MaxConsecutivePollFailures = 5;

    public CommunitiesViewModel(GameCatalogItem? game, string appDataRoot, ICommunitiesApiClient api,
        IModpackApiClient packages, TimeSpan? chatPollInterval = null)
    {
        Game = game;
        this.appDataRoot = appDataRoot;
        this.api = api;
        this.packages = packages;
        this.chatPollInterval = chatPollInterval ?? TimeSpan.FromSeconds(4);
        IsDemoMode = api.IsDemoMode;
        Communities.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCommunities));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowEmptyState));
        };
        LocalizationService.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(ModeNotice));
            OnPropertyChanged(nameof(OfflineNotice));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(ChatTitle));
            OnPropertyChanged(nameof(JoinDialogTitle));
            foreach (var community in Communities) community.RefreshLocalized();
            SelectedCommunity?.RefreshLocalized();
            RefreshAccessModes();
        };
        RefreshAccessModes();
    }

    public GameCatalogItem? Game { get; private set; }
    public ObservableCollection<CommunityItemViewModel> Communities { get; } = [];
    public ObservableCollection<CommunityChannel> ChatChannels { get; } = [];
    public ObservableCollection<CommunityMessageViewModel> ChatMessages { get; } = [];
    public ObservableCollection<CommunityAccessModeOption> AccessModes { get; } = [];

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isOffline;
    [ObservableProperty] private bool isDemoMode;
    [ObservableProperty] private bool isCompactLayout;
    [ObservableProperty] private bool isCommunityDetailsSelected;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isCreateDialogOpen;
    [ObservableProperty] private bool isJoinDialogOpen;
    [ObservableProperty] private bool isJoinCodeDialogOpen;
    [ObservableProperty] private bool isChatOpen;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string communityError = "";
    [ObservableProperty] private string joinError = "";
    [ObservableProperty] private string newCommunityName = "";
    [ObservableProperty] private string newCommunityDescription = "";
    [ObservableProperty] private string newAccessMode = CommunityAccessModes.Public;
    [ObservableProperty] private string newPassword = "";
    [ObservableProperty] private string newInviteCode = "";
    [ObservableProperty] private string newPackageSlug = "";
    [ObservableProperty] private CommunityAccessModeOption? selectedAccessMode;
    [ObservableProperty] private CommunityItemViewModel? selectedCommunity;
    [ObservableProperty] private CommunityItemViewModel? joinTarget;
    [ObservableProperty] private string joinPassword = "";
    [ObservableProperty] private string joinInviteCode = "";
    [ObservableProperty] private string joinCommunitySlug = "";
    [ObservableProperty] private CommunityItemViewModel? chatCommunity;
    [ObservableProperty] private CommunityChannel? selectedChatChannel;
    [ObservableProperty] private string chatDraft = "";
    [ObservableProperty] private string chatStatus = "";
    [ObservableProperty] private bool isSendingMessage;

    public string Title => LocalizationService.Instance["Communities.Title"];
    public string Subtitle => LocalizationService.Instance["Communities.Subtitle"];
    public string ModeNotice => IsDemoMode ? LocalizationService.Instance["Communities.DemoMode"] : "";
    public string OfflineNotice => IsOffline ? LocalizationService.Instance["Communities.Offline"] : "";
    public string EmptyTitle => LocalizationService.Instance[SearchText.Length == 0 ? "Communities.EmptyTitle" : "Communities.NoResultsTitle"];
    public string EmptyMessage => LocalizationService.Instance[SearchText.Length == 0 ? "Communities.EmptyMessage" : "Communities.NoResultsMessage"];
    public string ChatTitle => ChatCommunity is null ? LocalizationService.Instance["Chat.Title"] :
        string.Format(LocalizationService.Instance["Chat.TitleForCommunity"], ChatCommunity.Name);
    public string JoinDialogTitle => string.Format(LocalizationService.Instance["Communities.JoinTitle"], JoinTarget?.Name ?? "");
    public string ChatDemoNotice => IsDemoMode ? LocalizationService.Instance["Chat.DemoMode"] : "";
    public bool HasCommunities => Communities.Count > 0;
    public bool IsEmpty => !HasCommunities && !IsLoading;
    public bool ShowEmptyState => IsEmpty && ShowCommunityList;
    public bool ShowCommunityList => !IsCompactLayout || !IsCommunityDetailsSelected;
    public bool ShowCommunityDetails => HasSelectedCommunity && (!IsCompactLayout || IsCommunityDetailsSelected);
    public bool IsNotLoading => !IsLoading;
    public bool IsNotBusy => !IsBusy;
    public bool HasNoChatMessages => !HasChatMessages;
    public bool IsNotChatSending => !IsSendingMessage;
    public double ListPaneWidth => IsCompactLayout ? 320 : 420;
    public double DetailPaneWidth => IsCompactLayout ? 320 : 520;
    public bool HasSelectedCommunity => SelectedCommunity is not null;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasCommunityError => !string.IsNullOrWhiteSpace(CommunityError);
    public bool HasJoinError => !string.IsNullOrWhiteSpace(JoinError);
    public bool HasChatStatus => !string.IsNullOrWhiteSpace(ChatStatus);
    // Ś5: only a channel that gave up entirely offers the explicit reconnect; a single failed poll retries itself.
    public bool IsChatConnectionLost => ChatStatus == LocalizationService.Instance["Chat.ConnectionLost"];
    public bool IsPasswordAccess => NewAccessMode is CommunityAccessModes.Password or CommunityAccessModes.PasswordOrInviteCode;
    public bool IsInviteCodeAccess => NewAccessMode is CommunityAccessModes.InviteCode or CommunityAccessModes.PasswordOrInviteCode;
    public bool JoinNeedsPassword => JoinTarget?.Model.AccessMode is CommunityAccessModes.Password or CommunityAccessModes.PasswordOrInviteCode;
    public bool JoinNeedsCode => JoinTarget?.Model.AccessMode is CommunityAccessModes.InviteCode or CommunityAccessModes.PasswordOrInviteCode;
    public bool CanSendMessage => IsChatOpen && ChatCommunity?.IsMember == true && SelectedChatChannel is not null &&
        !IsSendingMessage && !string.IsNullOrWhiteSpace(ChatDraft) && ChatDraft.Length <= 2000;
    public bool HasChatMessages => ChatMessages.Count > 0;
    public bool HasChatChannels => ChatChannels.Count > 0;
    public string ChatEmptyMessage => ChatCommunity is null || !ChatCommunity.IsMember
        ? LocalizationService.Instance["Chat.NoJoinedCommunity"] : LocalizationService.Instance["Chat.EmptyMessages"];

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        searchCancellation = new CancellationTokenSource();
        _ = SearchAfterTypingAsync(searchCancellation.Token);
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsNotLoading));
    }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));
    partial void OnIsCompactLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(ListPaneWidth));
        OnPropertyChanged(nameof(DetailPaneWidth));
        OnPropertyChanged(nameof(ShowCommunityList));
        OnPropertyChanged(nameof(ShowCommunityDetails));
        OnPropertyChanged(nameof(ShowEmptyState));
    }
    partial void OnIsCommunityDetailsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCommunityList));
        OnPropertyChanged(nameof(ShowCommunityDetails));
        OnPropertyChanged(nameof(ShowEmptyState));
    }
    partial void OnIsOfflineChanged(bool value) => OnPropertyChanged(nameof(OfflineNotice));
    partial void OnIsDemoModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeNotice));
        OnPropertyChanged(nameof(ChatDemoNotice));
    }
    partial void OnIsSendingMessageChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSendMessage));
        OnPropertyChanged(nameof(IsNotChatSending));
    }
    partial void OnSelectedAccessModeChanged(CommunityAccessModeOption? value)
    {
        if (value is not null && NewAccessMode != value.Value) NewAccessMode = value.Value;
    }
    partial void OnChatDraftChanged(string value) => OnPropertyChanged(nameof(CanSendMessage));
    partial void OnChatCommunityChanged(CommunityItemViewModel? value)
    {
        OnPropertyChanged(nameof(ChatTitle));
        OnPropertyChanged(nameof(ChatEmptyMessage));
        OnPropertyChanged(nameof(JoinDialogTitle));
    }
    partial void OnSelectedCommunityChanged(CommunityItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedCommunity));
        OnPropertyChanged(nameof(ShowCommunityDetails));
        if (value is not null) _ = LoadSelectedDetailsAsync(value);
    }
    partial void OnSelectedChatChannelChanged(CommunityChannel? value)
    {
        OnPropertyChanged(nameof(CanSendMessage));
        if (IsChatOpen && value is not null && ChatCommunity is not null) StartChannelPolling(ChatCommunity, value);
    }
    partial void OnIsChatOpenChanged(bool value)
    {
        if (!value) StopChannelPolling();
        OnPropertyChanged(nameof(CanSendMessage));
    }
    partial void OnNewAccessModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsPasswordAccess));
        OnPropertyChanged(nameof(IsInviteCodeAccess));
    }
    partial void OnJoinTargetChanged(CommunityItemViewModel? value)
    {
        OnPropertyChanged(nameof(JoinNeedsPassword));
        OnPropertyChanged(nameof(JoinNeedsCode));
        OnPropertyChanged(nameof(JoinDialogTitle));
    }
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
    partial void OnCommunityErrorChanged(string value) => OnPropertyChanged(nameof(HasCommunityError));
    partial void OnJoinErrorChanged(string value) => OnPropertyChanged(nameof(HasJoinError));
    partial void OnChatStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasChatStatus));
        OnPropertyChanged(nameof(IsChatConnectionLost));
    }

    public void SetGame(GameCatalogItem? game)
    {
        Game = game;
        OnPropertyChanged(nameof(Game));
        if (SelectedCommunity is not null) _ = LoadSelectedDetailsAsync(SelectedCommunity);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading && refreshTask is not null)
        {
            await refreshTask;
            return;
        }
        refreshTask = RefreshCoreAsync(SearchText, cancellationToken);
        await refreshTask;
    }

    private async Task RefreshCoreAsync(string query, CancellationToken cancellationToken)
    {
        IsLoading = true;
        StatusMessage = "";
        try
        {
            var page = await api.ListAsync(query, cancellationToken: cancellationToken);
            var visible = page.Communities.Where(community => community.AccessMode != CommunityAccessModes.InviteCode || community.IsMember)
                .Where(community => Matches(community, query)).ToArray();
            var selectedSlug = SelectedCommunity?.Slug;
            Communities.Clear();
            foreach (var community in visible) Communities.Add(new CommunityItemViewModel(community));
            IsOffline = page.IsOffline;
            hasLoaded = true;
            OnPropertyChanged(nameof(HasCommunities));
            var selected = Communities.FirstOrDefault(community => community.Slug == selectedSlug) ?? Communities.FirstOrDefault();
            SelectedCommunity = selected;
            if (page.IsOffline) StatusMessage = OfflineNotice;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            IsOffline = true;
            hasLoaded = true;
            StatusMessage = ErrorMessage(ex, "Communities.LoadFailed");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(HasCommunities));
        }
    }

    private async Task SearchAfterTypingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void SelectCommunityList() => IsCommunityDetailsSelected = false;

    [RelayCommand]
    private void SelectCommunityDetails()
    {
        if (SelectedCommunity is not null) IsCommunityDetailsSelected = true;
    }

    [RelayCommand]
    private void OpenCreateDialog()
    {
        NewCommunityName = "";
        NewCommunityDescription = "";
        NewPassword = "";
        NewInviteCode = "";
        NewPackageSlug = "";
        NewAccessMode = CommunityAccessModes.Public;
        RefreshAccessModes();
        CommunityError = "";
        IsCreateDialogOpen = true;
    }

    [RelayCommand]
    private void OpenJoinByCodeDialog()
    {
        JoinCommunitySlug = "";
        JoinInviteCode = "";
        JoinError = "";
        IsJoinCodeDialogOpen = true;
    }

    [RelayCommand]
    private void RequestJoin()
    {
        if (SelectedCommunity is not { CanJoin: true } community) return;
        JoinTarget = community;
        JoinPassword = "";
        JoinInviteCode = "";
        JoinError = "";
        IsJoinDialogOpen = true;
    }

    [RelayCommand]
    private async Task CreateCommunityAsync()
    {
        CommunityError = "";
        if (Communities.Any(community => community.Name.Equals(NewCommunityName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            CommunityError = LocalizationService.Instance["Communities.NameExists"];
            return;
        }
        try
        {
            var request = CommunitiesApiClient.ValidateCreateRequest(new(NewCommunityName, NewCommunityDescription,
                NewAccessMode, NewPassword, NewInviteCode, NewPackageSlug));
            IsBusy = true;
            var created = await api.CreateAsync(request);
            var item = Upsert(created);
            SelectedCommunity = item;
            IsCreateDialogOpen = false;
            StatusMessage = LocalizationService.Instance["Communities.CreateSuccess"];
        }
        catch (ArgumentException ex)
        {
            CommunityError = ValidationMessage(ex, "Communities.CreateInvalid");
        }
        catch (Exception ex)
        {
            CommunityError = ErrorMessage(ex, "Communities.CreateFailed");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task JoinSelectedAsync()
    {
        if (JoinTarget is null) return;
        JoinError = "";
        try
        {
            CommunitiesApiClient.ValidateJoinRequest(JoinTarget.Model.AccessMode, JoinPassword, JoinInviteCode);
            IsBusy = true;
            var joined = await api.JoinAsync(JoinTarget.Slug, JoinTarget.Model.AccessMode, JoinPassword, JoinInviteCode);
            var item = Upsert(joined with { IsMember = true });
            SelectedCommunity = item;
            IsJoinDialogOpen = false;
            StatusMessage = LocalizationService.Instance["Communities.JoinSuccess"];
        }
        catch (ArgumentException ex) { JoinError = ValidationMessage(ex, "Communities.JoinCredentialsRequired"); }
        catch (Exception ex) { JoinError = ErrorMessage(ex, "Communities.JoinFailed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task JoinByCodeAsync()
    {
        JoinError = "";
        var slug = JoinCommunitySlug.Trim();
        try
        {
            if (!IsValidCommunitySlug(slug)) throw new ArgumentException("Invalid community slug.");
            CommunitiesApiClient.ValidateJoinRequest(CommunityAccessModes.InviteCode, null, JoinInviteCode);
            IsBusy = true;
            var joined = await api.JoinAsync(slug, CommunityAccessModes.InviteCode, null, JoinInviteCode);
            var item = Upsert(joined with { IsMember = true });
            SelectedCommunity = item;
            IsJoinCodeDialogOpen = false;
            StatusMessage = LocalizationService.Instance["Communities.JoinSuccess"];
        }
        catch (ArgumentException ex) { JoinError = ValidationMessage(ex, "Communities.JoinCodeInvalid"); }
        catch (Exception ex) { JoinError = ErrorMessage(ex, "Communities.JoinFailed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LeaveSelectedAsync()
    {
        if (SelectedCommunity is not { IsMember: true } community) return;
        StatusMessage = "";
        try
        {
            IsBusy = true;
            await api.LeaveAsync(community.Slug);
            var updated = community.Model with { IsMember = false, MemberCount = Math.Max(0, community.Model.MemberCount - 1) };
            Communities.Remove(community);
            if (updated.AccessMode != CommunityAccessModes.InviteCode) Communities.Add(new CommunityItemViewModel(updated));
            SelectedCommunity = Communities.FirstOrDefault();
            StatusMessage = LocalizationService.Instance["Communities.LeaveSuccess"];
        }
        catch (Exception ex) { StatusMessage = ErrorMessage(ex, "Communities.LeaveFailed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void CloseCommunityDialog()
    {
        IsCreateDialogOpen = IsJoinDialogOpen = IsJoinCodeDialogOpen = false;
        CommunityError = JoinError = "";
    }

    [RelayCommand]
    private async Task OpenChatAsync(string? slug = null)
    {
        if (!hasLoaded) await RefreshAsync();
        IsChatOpen = true;
        ChatStatus = "";
        SelectedChatChannel = null;
        ChatMessages.Clear();
        ChatChannels.Clear();
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(HasNoChatMessages));
        OnPropertyChanged(nameof(HasChatChannels));
        var community = slug is null ? null : Communities.FirstOrDefault(item => item.Slug == slug && item.IsMember);
        community ??= SelectedCommunity is { IsMember: true } selected ? selected : Communities.FirstOrDefault(item => item.IsMember);
        ChatCommunity = community;
        if (community is null)
        {
            ChatStatus = LocalizationService.Instance["Chat.NoJoinedCommunity"];
            OnPropertyChanged(nameof(ChatEmptyMessage));
            return;
        }

        try
        {
            var details = await api.GetAsync(community.Slug);
            if (details is null) { ChatStatus = LocalizationService.Instance["Chat.CommunityUnavailable"]; return; }
            foreach (var channel in details.Channels) ChatChannels.Add(channel);
            SelectedChatChannel = ChatChannels.FirstOrDefault();
            if (ChatChannels.Count == 0) ChatStatus = LocalizationService.Instance["Chat.NoChannels"];
        }
        catch (Exception ex) { ChatStatus = ErrorMessage(ex, "Chat.LoadFailed"); }
        OnPropertyChanged(nameof(HasChatChannels));
        OnPropertyChanged(nameof(ChatEmptyMessage));
    }

    [RelayCommand]
    private void CloseChat()
    {
        StopChannelPolling();
        IsChatOpen = false;
        SelectedChatChannel = null;
        ChatStatus = "";
        ChatDraft = "";
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (ChatCommunity is null || SelectedChatChannel is null || !CanSendMessage) return;
        var text = ChatDraft;
        ChatStatus = "";
        try
        {
            IsSendingMessage = true;
            var sent = await api.SendMessageAsync(ChatCommunity.Slug, SelectedChatChannel.Id, text);
            ChatDraft = "";
            if (sent is not null) AppendMessages([sent], ChatCommunity.Slug, SelectedChatChannel.Id);
            else await PollChannelOnceAsync(ChatCommunity.Slug, SelectedChatChannel.Id, channelPollingCancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException ex) { ChatStatus = ValidationMessage(ex, "Chat.MessageInvalid"); }
        catch (Exception ex) { ChatStatus = ErrorMessage(ex, "Chat.SendFailed"); }
        finally { IsSendingMessage = false; }
    }

    private void StartChannelPolling(CommunityItemViewModel community, CommunityChannel channel)
    {
        StopChannelPolling();
        consecutivePollFailures = 0;
        chatCursor = 0;
        ChatMessages.Clear();
        ChatStatus = "";
        channelPollingCancellation = new CancellationTokenSource();
        _ = PollChannelAsync(community.Slug, channel.Id, channelPollingCancellation.Token);
    }

    private async Task PollChannelAsync(string slug, string channelId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsChatOpen)
        {
            try
            {
                await PollChannelOnceAsync(slug, channelId, cancellationToken);
                consecutivePollFailures = 0;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (IsChatOpen && SelectedChatChannel?.Id == channelId) ChatStatus = "";
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (++consecutivePollFailures >= MaxConsecutivePollFailures) break;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (IsChatOpen && SelectedChatChannel?.Id == channelId) ChatStatus = ErrorMessage(ex, "Chat.PollFailed");
                });
            }

            try { await Task.Delay(chatPollInterval, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }

        if (consecutivePollFailures >= MaxConsecutivePollFailures)
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsChatOpen && SelectedChatChannel?.Id == channelId)
                    ChatStatus = LocalizationService.Instance["Chat.ConnectionLost"];
            });
    }

    // Ś5: after the loop gave up, the user can reconnect to the same channel without reopening the chat.
    [RelayCommand]
    private void RetryChatPolling()
    {
        if (IsChatOpen && ChatCommunity is { } community && SelectedChatChannel is { } channel)
            StartChannelPolling(community, channel);
    }

    private async Task PollChannelOnceAsync(string slug, string channelId, CancellationToken cancellationToken)
    {
        var after = chatCursor;
        var messages = await api.GetMessagesAsync(slug, channelId, after, 50, cancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() => AppendMessages(messages, slug, channelId));
    }

    private void AppendMessages(IEnumerable<CommunityMessage> messages, string slug, string channelId)
    {
        if (!IsChatOpen || ChatCommunity?.Slug != slug || SelectedChatChannel?.Id != channelId) return;
        foreach (var message in messages.OrderBy(message => message.Id))
        {
            if (message.Id <= chatCursor || ChatMessages.Any(existing => existing.Id == message.Id)) continue;
            ChatMessages.Add(new CommunityMessageViewModel(message));
            chatCursor = message.Id;
        }
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(HasNoChatMessages));
    }

    private void StopChannelPolling()
    {
        channelPollingCancellation?.Cancel();
        channelPollingCancellation?.Dispose();
        channelPollingCancellation = null;
    }

    private async Task LoadSelectedDetailsAsync(CommunityItemViewModel item)
    {
        var generation = ++selectionGeneration;
        selectedLoadCancellation?.Cancel();
        selectedLoadCancellation?.Dispose();
        selectedLoadCancellation = new CancellationTokenSource();
        var cancellationToken = selectedLoadCancellation.Token;
        item.SetChannels([]);
        item.SetPackage(null, null);
        try
        {
            if (item.IsMember)
            {
                var details = await api.GetAsync(item.Slug, cancellationToken);
                if (generation != selectionGeneration || cancellationToken.IsCancellationRequested) return;
                item.SetChannels(details?.Channels ?? []);
            }
            if (!item.HasPackage) return;
            var package = await packages.GetAsync(item.Model.PackageSlug!, cancellationToken);
            if (generation != selectionGeneration || cancellationToken.IsCancellationRequested) return;
            if (package is null)
            {
                item.SetPackage(null, null, unavailable: true);
                return;
            }
            item.SetPackage(package, HasMatchingProfile(package));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation != selectionGeneration) return;
            if (item.HasPackage) item.SetPackage(null, null, unavailable: true);
            if (ex is CommunityApiException) StatusMessage = ErrorMessage(ex, "Communities.DetailsFailed");
        }
    }

    private bool? HasMatchingProfile(ModpackDefinition pack)
    {
        var path = Path.Combine(appDataRoot, "profiles.json");
        if (!File.Exists(path)) return false;
        try
        {
            // ProfileStore's constructor seeds missing games and writes; read its JSON without triggering that behavior.
            var data = JsonSerializer.Deserialize<Dictionary<string, GameProfileData>>(File.ReadAllText(path));
            if (data is null || !data.TryGetValue(pack.GameId, out var gameData)) return false;
            var items = pack.Items.Where(item => item.Enabled).ToArray();
            return items.Length > 0 && gameData.Profiles.Any(profile =>
                profile.Name.Equals(pack.Name, StringComparison.OrdinalIgnoreCase) && items.All(item => profile.Mods.Any(mod =>
                    mod.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) &&
                    mod.Version.Equals(item.Version, StringComparison.OrdinalIgnoreCase))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private CommunityItemViewModel Upsert(CommunitySummary summary)
    {
        var existing = Communities.FirstOrDefault(community => community.Slug == summary.Slug);
        if (existing is not null)
        {
            // W2: a join reply can come back as a placeholder (name = slug, no description). It may only
            // change membership, never overwrite the real name, description or package of a known group.
            existing.SetSummary(IsPlaceholder(summary)
                ? existing.Model with
                {
                    IsMember = summary.IsMember,
                    MemberCount = Math.Max(existing.Model.MemberCount, summary.MemberCount)
                }
                : summary);
            return existing;
        }
        var created = new CommunityItemViewModel(summary);
        Communities.Insert(0, created);
        OnPropertyChanged(nameof(HasCommunities));
        return created;
    }

    private static bool IsPlaceholder(CommunitySummary summary) => !summary.IsLocalDemo &&
        string.Equals(summary.Name, summary.Slug, StringComparison.Ordinal) && summary.Description.Length == 0;

    private void RefreshAccessModes()
    {
        var selected = NewAccessMode;
        AccessModes.Clear();
        var loc = LocalizationService.Instance;
        AccessModes.Add(new(CommunityAccessModes.Public, loc["Communities.AccessPublic"]));
        AccessModes.Add(new(CommunityAccessModes.Password, loc["Communities.AccessPassword"]));
        AccessModes.Add(new(CommunityAccessModes.InviteCode, loc["Communities.AccessInviteCode"]));
        AccessModes.Add(new(CommunityAccessModes.PasswordOrInviteCode, loc["Communities.AccessPasswordOrInviteCode"]));
        SelectedAccessMode = AccessModes.FirstOrDefault(option => option.Value == selected) ?? AccessModes[0];
    }

    private static bool Matches(CommunitySummary community, string query) => string.IsNullOrWhiteSpace(query) ||
        community.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || community.Description.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidCommunitySlug(string slug) => slug.Length is > 0 and <= 80 &&
        slug.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private string ErrorMessage(Exception exception, string fallbackKey)
    {
        var key = exception switch
        {
            // N5: every 409 used to read as “already a member”; the server tells which one it actually is.
            CommunityApiException { ErrorCode: "DEVICE_REPLAY" } => "Communities.DeviceReplay",
            CommunityApiException { ErrorCode: "COMMUNITY_SLUG_TAKEN" } => "Communities.SlugTaken",
            CommunityApiException { ErrorCode: "JOIN_DENIED" } => "Communities.JoinDenied",
            CommunityApiException { StatusCode: HttpStatusCode.NotFound } => "Communities.NotFound",
            CommunityApiException { StatusCode: HttpStatusCode.Conflict } => "Communities.AlreadyMember",
            CommunityApiException { StatusCode: HttpStatusCode.TooManyRequests } => "Communities.RateLimited",
            CommunityApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "Communities.DeviceRequired",
            HttpRequestException or TaskCanceledException or IOException => "Communities.Offline",
            _ => fallbackKey
        };
        return LocalizationService.Instance[key];
    }

    private string ValidationMessage(ArgumentException exception, string fallbackKey)
    {
        var key = exception.Message.Contains("8–128", StringComparison.Ordinal) || exception.Message.Contains("8-128", StringComparison.Ordinal)
            ? "Communities.SecretLength"
            : exception.Message.Contains("password or invite code", StringComparison.OrdinalIgnoreCase)
                ? "Communities.SecretRequired" : fallbackKey;
        return LocalizationService.Instance[key];
    }
}
