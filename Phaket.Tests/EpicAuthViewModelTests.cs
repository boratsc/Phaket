using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

// Epic sign-in dialog: redirect/JSON interception logic and the manual
// fallback. The code is a secret — tests use a synthetic one, never a real one.
public class EpicAuthViewModelTests
{
    private const string Code = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    [Fact]
    public void Navigation_LocalhostRedirect_YieldsCode()
    {
        Assert.True(EpicAuthViewModel.TryCodeFromNavigation(
            new Uri($"https://localhost/launcher/authorized?code={Code}"), out var code));
        Assert.Equal(Code, code);
    }

    [Theory]
    [InlineData("https://evil.example/launcher/authorized?code=" + Code)]
    [InlineData("https://localhost/launcher/authorized")]
    [InlineData("https://localhost/launcher/authorized?code=tooshort")]
    public void Navigation_NonRedirectOrBadCode_Rejected(string url)
    {
        Assert.False(EpicAuthViewModel.TryCodeFromNavigation(new Uri(url), out _));
        Assert.False(EpicAuthViewModel.TryCodeFromNavigation(null, out _));
    }

    [Fact]
    public void ScriptResult_JsonBody_YieldsCode()
    {
        var body = $"{{\"warning\":\"secret\",\"redirectUrl\":\"https://localhost/launcher/authorized?code={Code}\",\"authorizationCode\":\"{Code}\",\"exchangeCode\":null,\"sid\":null}}";
        Assert.True(EpicAuthViewModel.TryCodeFromScriptResult(body, out var code));
        Assert.Equal(Code, code);
    }

    [Fact]
    public void ScriptResult_JsonEncodedString_Unwrapped()
    {
        // InvokeScript returns the JS string JSON-encoded (one extra layer).
        var body = $"{{\"authorizationCode\":\"{Code}\"}}";
        var encoded = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.True(EpicAuthViewModel.TryCodeFromScriptResult(encoded, out var code));
        Assert.Equal(Code, code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"sid\":\"abc\"}")]
    public void ScriptResult_Garbage_Rejected(string body)
    {
        Assert.False(EpicAuthViewModel.TryCodeFromScriptResult(body, out _));
    }

    [Fact]
    public void Confirm_ValidCode_SubmitsAndCloses()
    {
        string? submitted = null;
        var closed = false;
        var vm = new EpicAuthViewModel("https://example.test/login", c => submitted = c, () => closed = true);
        vm.AuthorizationInput = Code;
        vm.ConfirmCommand.Execute(null);
        Assert.Equal(Code, submitted);
        Assert.True(closed);
        Assert.True(vm.CodeSubmitted);
    }

    [Fact]
    public void Confirm_InvalidCode_ShowsErrorAndStaysOpen()
    {
        var closed = false;
        var vm = new EpicAuthViewModel("https://example.test/login", _ => { }, () => closed = true);
        vm.AuthorizationInput = "garbage";
        vm.ConfirmCommand.Execute(null);
        Assert.False(closed);
        Assert.False(vm.CodeSubmitted);
        Assert.NotEmpty(vm.Status);
    }

    [Fact]
    public void Fallback_SwitchesToManualMode()
    {
        var vm = new EpicAuthViewModel("https://example.test/login", _ => { }, () => { });
        Assert.True(vm.IsWebViewMode);
        vm.FallbackToManual();
        Assert.False(vm.IsWebViewMode);
        Assert.NotEmpty(vm.Status);
    }

    [AvaloniaFact]
    public void FallbackWindow_RendersWithoutWebViewEngine()
    {
        var vm = new EpicAuthViewModel("https://example.test/login", _ => { }, () => { });
        vm.FallbackToManual();
        var window = new EpicAuthWindow(vm) { Width = 900, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory("/tmp/opencode/phaket-render");
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        frame.Save("/tmp/opencode/phaket-render/16-epic-auth-fallback.png");
        window.Close();
    }
}
