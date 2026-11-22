using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Phaket.Core.Platforms;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

// PAK-102: the password / Steam Guard dialog was extracted from the library install so
// the mod provision shows the same one. These tests pin the extraction's behaviour:
// an answer, a cancel and a timeout, never a silent empty value.
public sealed class SteamPromptViewModelTests
{
    [Fact]
    public async Task PasswordPromptReturnsTheAnswerAndCloses()
    {
        var vm = new SteamPromptViewModel();
        var request = vm.RequestAsync(DepotPrompt.Password, TestContext.Current.CancellationToken);
        Assert.True(vm.CredentialOpen);
        Assert.True(vm.HasDialog);
        vm.Password = "secret";
        vm.ConfirmCredentialCommand.Execute(null);
        Assert.Equal("secret", await request);
        Assert.False(vm.CredentialOpen);
        Assert.False(vm.HasDialog);
        Assert.Equal("", vm.Password);
    }

    [Fact]
    public async Task GuardPromptReturnsTheCodeAndCloses()
    {
        var vm = new SteamPromptViewModel();
        var request = vm.RequestAsync(DepotPrompt.SteamGuard, TestContext.Current.CancellationToken);
        Assert.True(vm.TwoFactorOpen);
        vm.TwoFactorCode = "ABC12";
        vm.ConfirmTwoFactorCommand.Execute(null);
        Assert.Equal("ABC12", await request);
        Assert.False(vm.TwoFactorOpen);
    }

    [Fact]
    public async Task CancelStopsTheWait()
    {
        var vm = new SteamPromptViewModel();
        var request = vm.RequestAsync(DepotPrompt.Password, TestContext.Current.CancellationToken);
        vm.CancelDialogCommand.Execute(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.False(vm.HasDialog);
    }

    [Fact]
    public async Task TimeoutStopsTheWait()
    {
        var vm = new SteamPromptViewModel();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            vm.RequestAsync(DepotPrompt.SteamGuard, TestContext.Current.CancellationToken, timeout: TimeSpan.FromMilliseconds(20)));
        Assert.False(vm.HasDialog);
    }

    // N4: the shared instance serves the catalog install and the modpack install at once.
    // A second prompt waits for the first answer; an answer reaches only its own request.
    [Fact]
    public async Task OverlappingPromptsWaitAndAnswersDoNotCross()
    {
        var vm = new SteamPromptViewModel();
        var first = vm.RequestAsync(DepotPrompt.Password, TestContext.Current.CancellationToken);
        var second = vm.RequestAsync(DepotPrompt.SteamGuard, TestContext.Current.CancellationToken);

        // The Guard prompt must not open while the password dialog owns the instance.
        Assert.True(vm.CredentialOpen);
        Assert.False(vm.TwoFactorOpen);

        vm.Password = "first-secret";
        vm.ConfirmCredentialCommand.Execute(null);
        Assert.Equal("first-secret", await first);

        await WaitUntil(() => vm.TwoFactorOpen);
        Assert.False(vm.CredentialOpen);
        vm.TwoFactorCode = "22222";
        vm.ConfirmTwoFactorCommand.Execute(null);
        Assert.Equal("22222", await second);
        Assert.False(vm.HasDialog);
    }

    [Fact]
    public async Task CancellingTheFirstPromptReleasesTheSecond()
    {
        using var cts = new CancellationTokenSource();
        var vm = new SteamPromptViewModel();
        var first = vm.RequestAsync(DepotPrompt.Password, cts.Token);
        var second = vm.RequestAsync(DepotPrompt.SteamGuard, TestContext.Current.CancellationToken);
        Assert.True(vm.CredentialOpen);
        Assert.False(vm.TwoFactorOpen);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        await WaitUntil(() => vm.TwoFactorOpen);
        vm.TwoFactorCode = "33333";
        vm.ConfirmTwoFactorCommand.Execute(null);
        Assert.Equal("33333", await second);
        Assert.False(vm.HasDialog);
    }

    // N17: the window overlay must take keyboard focus and Escape must cancel.
    [AvaloniaFact]
    public async Task DialogTakesFocusAndEscapeCancels()
    {
        var vm = new SteamPromptViewModel();
        var request = vm.RequestAsync(DepotPrompt.Password, TestContext.Current.CancellationToken);
        var dialog = new SteamPromptDialog { DataContext = vm };
        var window = new Window { Width = 480, Height = 260, Content = dialog };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var box = dialog.FindControl<TextBox>("PasswordBox");
            Assert.NotNull(box);
            Assert.True(box!.IsFocused);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(vm.HasDialog);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        finally { window.Close(); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "the queued prompt did not open");
    }
}