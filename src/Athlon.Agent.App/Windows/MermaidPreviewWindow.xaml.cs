using System.IO;
using System.Windows;
using System.Windows.Input;
using Athlon.Agent.App.Services;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Windows;

public partial class MermaidPreviewWindow : Window
{
    private MermaidPreviewWindow(IReadOnlyList<string> diagrams)
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadPreviewAsync(diagrams);
    }

    public static void Show(string markdown, Window? owner)
    {
        var diagrams = MermaidMarkdownExtractor.ExtractDiagrams(markdown);
        if (diagrams.Count == 0)
        {
            MessageBox.Show(
                owner,
                "未找到 ```mermaid 代码块。",
                "无法预览",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!MermaidPreviewHtmlBuilder.IsBundledRuntimeAvailable())
        {
            MessageBox.Show(
                owner,
                $"缺少离线 Mermaid 资源：{MermaidPreviewHtmlBuilder.MermaidScriptPath}",
                "无法预览",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var window = new MermaidPreviewWindow(diagrams) { Owner = owner };
        window.ShowDialog();
    }

    private async Task LoadPreviewAsync(IReadOnlyList<string> diagrams)
    {
        try
        {
            await PreviewWebView.EnsureCoreWebView2Async();
            var core = PreviewWebView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 未初始化。");

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            var assetsDir = MermaidPreviewHtmlBuilder.AssetsDirectory;
            if (!Directory.Exists(assetsDir))
            {
                throw new DirectoryNotFoundException($"资源目录不存在：{assetsDir}");
            }

            core.SetVirtualHostNameToFolderMapping(
                MermaidPreviewHtmlBuilder.VirtualHostName,
                assetsDir,
                CoreWebView2HostResourceAccessKind.Allow);

            var html = MermaidPreviewHtmlBuilder.BuildDocument(diagrams);
            core.NavigateToString(html);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法加载 Mermaid 预览：{exception.Message}",
                "预览失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
