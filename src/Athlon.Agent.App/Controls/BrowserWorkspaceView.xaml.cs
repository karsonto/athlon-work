using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Controls;

public partial class BrowserWorkspaceView : UserControl
{
    private BrowserWorkspaceTabViewModel? _tab;
    private bool _webViewReady;

    public BrowserWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await EnsureWebViewAsync().ConfigureAwait(true);
        TryNavigateInitial();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachTab(_tab);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachTab(_tab);
        _tab = e.NewValue as BrowserWorkspaceTabViewModel;
        AttachTab(_tab);
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
            BrowserWebView.CoreWebView2.NavigationCompleted += (_, _) =>
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
            };
            _webViewReady = true;
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Browser WebView2 init failed: {ex.Message}");
        }
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
