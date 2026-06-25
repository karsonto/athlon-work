using System.Windows;
using System.Windows.Input;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Windows;

public partial class HtmlPreviewWindow : Window
{
    private readonly string _rawHtml;

    private HtmlPreviewWindow(string rawHtml)
    {
        _rawHtml = rawHtml;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static void Show(string rawHtml, Window? owner)
    {
        var window = new HtmlPreviewWindow(rawHtml)
        {
            Owner = owner
        };
        window.ShowDialog();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged += OnThemeChanged;
        await LoadPreviewAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        AppThemeManager.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e) =>
        _ = LoadPreviewAsync();

    private async Task LoadPreviewAsync()
    {
        try
        {
            var environment = await WebView2ServiceAccess.GetRequiredEnvironmentAsync().ConfigureAwait(true);
            await PreviewWebView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            PreviewWebView.NavigateToString(MarkdownHtmlRenderer.BuildPreviewDocument(_rawHtml));
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法加载 HTML 预览：{exception.Message}",
                "预览失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
