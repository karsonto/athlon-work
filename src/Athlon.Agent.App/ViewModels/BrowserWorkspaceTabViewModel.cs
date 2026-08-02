using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class BrowserWorkspaceTabViewModel : WorkspaceTabViewModel
{
    public BrowserWorkspaceTabViewModel(string title, string? initialUrl = null)
        : base(Guid.NewGuid(), WorkspaceTabKind.Browser, title)
    {
        AddressText = string.IsNullOrWhiteSpace(initialUrl) ? "https://" : initialUrl.Trim();
        CurrentUrl = AddressText;
    }

    [ObservableProperty]
    private string addressText = "https://";

    [ObservableProperty]
    private string currentUrl = "https://";

    [ObservableProperty]
    private bool canGoBack;

    [ObservableProperty]
    private bool canGoForward;

    [ObservableProperty]
    private bool isLoading;

    /// <summary>Raised when the view should navigate the WebView2 control.</summary>
    public event EventHandler<string>? NavigateRequested;

    /// <summary>Raised when the view should invoke browser chrome actions.</summary>
    public event EventHandler<BrowserChromeAction>? ChromeActionRequested;

    [RelayCommand]
    private void Navigate()
    {
        var url = NormalizeUrl(AddressText);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        AddressText = url;
        CurrentUrl = url;
        NavigateRequested?.Invoke(this, url);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => ChromeActionRequested?.Invoke(this, BrowserChromeAction.Back);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => ChromeActionRequested?.Invoke(this, BrowserChromeAction.Forward);

    [RelayCommand]
    private void Reload() => ChromeActionRequested?.Invoke(this, BrowserChromeAction.Reload);

    public void NotifyNavigationState(bool canBack, bool canForward, string? sourceUrl)
    {
        CanGoBack = canBack;
        CanGoForward = canForward;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            CurrentUrl = sourceUrl;
            AddressText = sourceUrl;
        }
    }

    public static string NormalizeUrl(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || text is "https://" or "http://")
        {
            return string.Empty;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.AbsoluteUri;
        }

        if (text.Contains(' ', StringComparison.Ordinal))
        {
            return "https://www.bing.com/search?q=" + Uri.EscapeDataString(text);
        }

        return "https://" + text.TrimStart('/');
    }
}

public enum BrowserChromeAction
{
    Back,
    Forward,
    Reload,
}
