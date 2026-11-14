using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Accounts;
using Phaket.Services;

namespace Phaket.ViewModels;

// Epic sign-in dialog: embedded native WebView captures the launcher redirect
// (https://localhost/launcher/authorized?code=...) automatically; when no webview
// engine is available it falls back to system browser + manual paste.
// The authorization code is a secret: it is never logged, only handed to the
// account service for the one-time token exchange.
public partial class EpicAuthViewModel : ViewModelBase
{
    private readonly Action<string> submitCode;
    private readonly Action close;

    public string BrowserUrl { get; }
    public bool CodeSubmitted { get; private set; }

    [ObservableProperty] private bool isWebViewMode = true;
    [ObservableProperty] private string webViewStatus = "";
    [ObservableProperty] private string authorizationInput = "";
    [ObservableProperty] private string status = "";

    public EpicAuthViewModel(string browserUrl, Action<string> submitCode, Action close)
    {
        BrowserUrl = browserUrl;
        this.submitCode = submitCode;
        this.close = close;
        WebViewStatus = LocalizationService.Instance["Account.EpicAuthHint"];
    }

    [RelayCommand] private void Cancel() => close();

    [RelayCommand]
    private void OpenBrowser()
    {
        try { Process.Start(new ProcessStartInfo(BrowserUrl) { UseShellExecute = true }); }
        catch (Exception) { Status = LocalizationService.Instance["Account.Error"]; }
    }

    [RelayCommand] private void Confirm() => TrySubmit(AuthorizationInput);

    public void FallbackToManual()
    {
        IsWebViewMode = false;
        Status = LocalizationService.Instance["Account.EpicAuthFallbackHint"];
    }

    public void SetWebViewAuthCode(string code) => TrySubmit(code);

    private void TrySubmit(string input)
    {
        try
        {
            var code = EpicAccountService.ExtractAuthorizationCode(input);
            CodeSubmitted = true;
            submitCode(code);
            close();
        }
        catch (Exception)
        {
            Status = LocalizationService.Instance["Account.EpicAuthBadCode"];
        }
    }

    // The launcher redirect carries the code in the query string; intercept it
    // before the webview tries (and fails) to connect to localhost.
    public static bool TryCodeFromNavigation(Uri? uri, out string code)
    {
        code = "";
        if (uri is null || !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || Uri.UnescapeDataString(kv[0]) != "code") continue;
            try { code = EpicAccountService.ExtractAuthorizationCode(Uri.UnescapeDataString(kv[1])); return true; }
            catch (Exception) { return false; }
        }
        return false;
    }

    // InvokeScript returns a JSON-encoded string; the /id/api/redirect endpoint
    // answers with a JSON body containing authorizationCode. Unwrap one layer.
    public static bool TryCodeFromScriptResult(string? scriptResult, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(scriptResult)) return false;
        var body = scriptResult.Trim();
        try
        {
            var unwrapped = JsonSerializer.Deserialize<string>(body);
            if (unwrapped is not null) body = unwrapped.Trim();
        }
        catch (JsonException) { /* raw body, not a JSON-encoded string */ }
        try { code = EpicAccountService.ExtractAuthorizationCode(body); return true; }
        catch (Exception) { return false; }
    }
}
