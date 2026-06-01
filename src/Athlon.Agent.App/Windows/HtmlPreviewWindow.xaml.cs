using System.Windows;
using System.Windows.Input;
using Athlon.Agent.App.Services;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Windows;

public partial class HtmlPreviewWindow : Window
{
    private HtmlPreviewWindow(string rawHtml)
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                await PreviewWebView.EnsureCoreWebView2Async();
                PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                PreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                PreviewWebView.NavigateToString(MarkdownHtmlRenderer.BuildPreviewDocument(rawHtml));
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
        };
    }

    public static void Show(string rawHtml, Window? owner)
    {
        var window = new HtmlPreviewWindow(rawHtml)
        {
            Owner = owner
        };
        window.ShowDialog();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
