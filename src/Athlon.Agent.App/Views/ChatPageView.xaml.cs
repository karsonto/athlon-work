using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Views;

public partial class ChatPageView : UserControl, IChatLayoutSurface
{
    public ChatPageView()
    {
        InitializeComponent();
    }

    WebChatView IChatLayoutSurface.ChatWebView => ChatWebView;

    ComposerInputControl IChatLayoutSurface.ComposerInput => ComposerInput;

    public MainWindowLayoutElements ChatLayoutElements => new()
    {
        EditorPaneColumn = EditorPaneColumn,
        EditorPaneHost = EditorPaneHost,
        EditorChatSplitter = EditorChatSplitter,
        ComposerRow = ComposerRow
    };

    private void ComposerInputWrapper_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ComposerInputWrapper_OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainShellViewModel shell
            || e.Data.GetData(DataFormats.FileDrop) is not string[] files
            || files.Length == 0)
        {
            return;
        }

        e.Handled = true;
        await shell.AddPendingFromFilePathsAsync(files).ConfigureAwait(true);
    }
}
