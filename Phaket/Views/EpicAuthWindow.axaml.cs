using Avalonia.Controls;
using Avalonia.Threading;
using Phaket.ViewModels;

namespace Phaket.Views;

// Epic sign-in window. The native WebView (WebView2 on Windows, WebKitGTK on
// Linux) is created lazily; if no engine is available the VM falls back to
// system browser + manual paste. Two interception paths, same as the proven
// SUSModder flow: the localhost redirect URL, and the JSON body of
// /id/api/redirect read via InvokeScript. The code is never logged.
public partial class EpicAuthWindow : Window
{
    private EpicAuthViewModel? vm;

    public EpicAuthWindow()
    {
        InitializeComponent();
    }

    public EpicAuthWindow(EpicAuthViewModel viewModel) : this()
    {
        vm = viewModel;
        DataContext = vm;
        if (vm.IsWebViewMode) InitializeWebView(vm.BrowserUrl);
    }

    private void InitializeWebView(string url)
    {
        try
        {
            var host = this.FindControl<Grid>("WebViewHost");
            if (host is null) { vm?.FallbackToManual(); return; }
            var webView = new NativeWebView();
            Grid.SetRow(webView, 0);
            host.Children.Add(webView);

            webView.NavigationStarted += (_, e) =>
            {
                if (EpicAuthViewModel.TryCodeFromNavigation(e.Request, out var code))
                    Dispatcher.UIThread.Post(() => vm?.SetWebViewAuthCode(code));
            };

            webView.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) return;
                var current = webView.Source?.ToString() ?? "";
                if (!current.Contains("/id/api/redirect", StringComparison.OrdinalIgnoreCase)
                    && !current.Contains("localhost/launcher/authorized", StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    var body = await webView.InvokeScript(
                        "(document.querySelector('pre')?.textContent || document.body?.textContent || document.documentElement?.textContent || '').trim()");
                    if (EpicAuthViewModel.TryCodeFromScriptResult(body, out var code))
                        Dispatcher.UIThread.Post(() => vm?.SetWebViewAuthCode(code));
                }
                catch
                {
                    // JSON body unreadable in this engine: the NavigationStarted
                    // path or the manual fallback still completes the login.
                }
            };

            // Navigation only works once the platform adapter exists.
            webView.AdapterCreated += (_, _) => webView.Source = new Uri(url);

            // No engine on this machine (e.g. missing WebKitGTK): the adapter
            // never arrives — fall back instead of showing an empty window.
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (vm is { IsWebViewMode: true, CodeSubmitted: false } && webView.Source is null)
                    vm.FallbackToManual();
            }), TaskScheduler.Default);
        }
        catch
        {
            vm?.FallbackToManual();
        }
    }
}
