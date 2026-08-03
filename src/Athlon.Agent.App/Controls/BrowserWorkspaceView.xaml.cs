using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.Browser;
using Athlon.Agent.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Controls;

public partial class BrowserWorkspaceView : UserControl
{
    private BrowserWorkspaceTabViewModel? _tab;
    private bool _webViewReady;
    private bool _ariaScriptInstalled;
    private BrowserWebViewRegistry? _registry;

    public BrowserWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _registry ??= TryResolveRegistry();
        await EnsureWebViewAsync().ConfigureAwait(true);
        TryRegisterWebView();
        TryNavigateInitial();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachTab(_tab);
        if (_tab is not null)
        {
            _registry?.Unregister(_tab.Id);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_tab is not null)
        {
            _registry?.Unregister(_tab.Id);
        }

        DetachTab(_tab);
        _tab = e.NewValue as BrowserWorkspaceTabViewModel;
        AttachTab(_tab);
        TryRegisterWebView();
        TryNavigateInitial();
    }

    private void AttachTab(BrowserWorkspaceTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        tab.NavigateRequested += OnNavigateRequested;
        tab.ChromeActionRequested += OnChromeActionRequested;
    }

    private void DetachTab(BrowserWorkspaceTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        tab.NavigateRequested -= OnNavigateRequested;
        tab.ChromeActionRequested -= OnChromeActionRequested;
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady)
        {
            return;
        }

        try
        {
            await WebView2Initializer.EnsureCoreWebView2Async(BrowserWebView).ConfigureAwait(true);
            BrowserWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            BrowserWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            BrowserWebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (_tab is not null)
                {
                    _tab.IsLoading = true;
                    if (!string.IsNullOrWhiteSpace(args.Uri))
                    {
                        _tab.AddressText = args.Uri;
                    }
                }
            };
            BrowserWebView.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                if (_tab is null || BrowserWebView.CoreWebView2 is null)
                {
                    return;
                }

                _tab.IsLoading = false;
                _tab.NotifyNavigationState(
                    BrowserWebView.CoreWebView2.CanGoBack,
                    BrowserWebView.CoreWebView2.CanGoForward,
                    BrowserWebView.CoreWebView2.Source);
                if (!string.IsNullOrWhiteSpace(BrowserWebView.CoreWebView2.DocumentTitle))
                {
                    _tab.Title = TruncateTitle(BrowserWebView.CoreWebView2.DocumentTitle);
                }

                // Document-created hook covers most navigations; re-inject if the page wiped globals.
                try
                {
                    await EnsureAriaHostScriptOnCurrentDocumentAsync().ConfigureAwait(true);
                }
                catch
                {
                    // Best-effort; ExecuteAriaAsync also injects on demand.
                }
            };
            await EnsureAriaHostScriptAsync().ConfigureAwait(true);
            _webViewReady = true;
            TryRegisterWebView();
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Browser WebView2 init failed: {ex.Message}");
        }
    }

    private async Task EnsureAriaHostScriptAsync()
    {
        if (_ariaScriptInstalled || BrowserWebView.CoreWebView2 is null)
        {
            return;
        }

        var script = BrowserAutomationHost.TryLoadAriaHostScript();
        if (string.IsNullOrWhiteSpace(script))
        {
            App.StartupTrace("Browser ARIA host script missing");
            return;
        }

        await BrowserWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script)
            .ConfigureAwait(true);
        await EnsureAriaHostScriptOnCurrentDocumentAsync().ConfigureAwait(true);
        _ariaScriptInstalled = true;
    }

    private async Task EnsureAriaHostScriptOnCurrentDocumentAsync()
    {
        if (BrowserWebView.CoreWebView2 is null)
        {
            return;
        }

        var script = BrowserAutomationHost.TryLoadAriaHostScript();
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        try
        {
            var present = await BrowserWebView.CoreWebView2.ExecuteScriptAsync(
                    "(function(){return !!(window.__athlonAria && window.__athlonAria.__version==='2' && typeof window.__athlonAria.invoke==='function');})()")
                .ConfigureAwait(true);
            if (string.Equals(present, "true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await BrowserWebView.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch
        {
            // Current document may not be ready; document-created hook covers navigations.
        }
    }

    private void TryRegisterWebView()
    {
        if (_tab is null || !_webViewReady || BrowserWebView.CoreWebView2 is null)
        {
            return;
        }

        _registry ??= TryResolveRegistry();
        _registry?.Register(_tab.Id, BrowserWebView.CoreWebView2);
    }

    private static BrowserWebViewRegistry? TryResolveRegistry()
    {
        try
        {
            if (Application.Current is App app)
            {
                return app.Services?.GetService<BrowserWebViewRegistry>();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private void TryNavigateInitial()
    {
        if (_tab is null || !_webViewReady)
        {
            return;
        }

        var url = BrowserWorkspaceTabViewModel.NormalizeUrl(_tab.AddressText);
        if (!string.IsNullOrWhiteSpace(url))
        {
            NavigateTo(url);
        }
    }

    private void OnNavigateRequested(object? sender, string url) => NavigateTo(url);

    private void OnChromeActionRequested(object? sender, BrowserChromeAction action)
    {
        if (BrowserWebView.CoreWebView2 is null)
        {
            return;
        }

        switch (action)
        {
            case BrowserChromeAction.Back when BrowserWebView.CoreWebView2.CanGoBack:
                BrowserWebView.CoreWebView2.GoBack();
                break;
            case BrowserChromeAction.Forward when BrowserWebView.CoreWebView2.CanGoForward:
                BrowserWebView.CoreWebView2.GoForward();
                break;
            case BrowserChromeAction.Reload:
                BrowserWebView.CoreWebView2.Reload();
                break;
        }
    }

    private void NavigateTo(string url)
    {
        if (!_webViewReady || BrowserWebView.CoreWebView2 is null || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            BrowserWebView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Browser navigate failed: {ex.Message}");
        }
    }

    private void AddressBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _tab is not null)
        {
            _tab.NavigateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static string TruncateTitle(string title)
    {
        const int max = 24;
        return title.Length <= max ? title : title[..(max - 1)] + "…";
    }
}
