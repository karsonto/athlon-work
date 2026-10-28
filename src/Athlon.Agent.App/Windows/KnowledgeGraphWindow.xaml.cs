using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.App.Windows;

public partial class KnowledgeGraphWindow : Window
{
    private readonly KnowledgeModuleGraph _graph;

    private KnowledgeGraphWindow(KnowledgeModuleGraph graph, string moduleName)
    {
        _graph = graph;
        InitializeComponent();
        Title = Strings.Format("Knowledge_GraphWindowTitle", moduleName);
        TitleText.Text = Title;
        Loaded += OnLoaded;
    }

    public static void Show(KnowledgeModuleGraph graph, string moduleName, Window? owner)
    {
        var window = new KnowledgeGraphWindow(graph, moduleName);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        window.ShowDialog();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView2Initializer.EnsureCoreWebView2Async(GraphWebView).ConfigureAwait(true);
            var core = GraphWebView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 未初始化。");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigateToString(KnowledgeGraphHtml.Build(_graph));
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                Strings.Get("Knowledge_TitleDialog"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
