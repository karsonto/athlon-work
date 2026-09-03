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
        if (string.IsNullOrWhiteSpace(text)
            || text is "https://" or "http://" or "file://")
        {
            return string.Empty;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)
            && IsAllowedBrowserScheme(absolute.Scheme))
        {
            return absolute.AbsoluteUri;
        }

        if (TryCreateFileUriFromLocalPath(text, out var fileUri))
        {
            return fileUri;
        }

        if (text.Contains(' ', StringComparison.Ordinal))
        {
            return "https://www.bing.com/search?q=" + Uri.EscapeDataString(text);
        }

        return "https://" + text.TrimStart('/');
    }

    private static bool IsAllowedBrowserScheme(string scheme) =>
        scheme is not null
        && (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps absolute local paths (e.g. C:\page.html) to file:// URIs so they are not prefixed with https://.
    /// </summary>
    private static bool TryCreateFileUriFromLocalPath(string text, out string fileUri)
    {
        fileUri = string.Empty;
        if (text.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        var looksLikeWindowsPath = text.Length >= 3
            && char.IsLetter(text[0])
            && text[1] == ':'
            && (text[2] is '\\' or '/');
        var looksLikeUnc = text.StartsWith(@"\\", StringComparison.Ordinal)
            || text.StartsWith("//", StringComparison.Ordinal);
        var looksLikeUnixAbsolute = text.StartsWith('/')
            && text.Length > 1
            && text[1] != '/';
        if (!looksLikeWindowsPath && !looksLikeUnc && !looksLikeUnixAbsolute)
        {
            return false;
        }

        try
        {
            var uri = new Uri(text, UriKind.Absolute);
            if (!uri.IsFile)
            {
                return false;
            }

            fileUri = uri.AbsoluteUri;
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }
}

public enum BrowserChromeAction
{
    Back,
    Forward,
    Reload,
}
