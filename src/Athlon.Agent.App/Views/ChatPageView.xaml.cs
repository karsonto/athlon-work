using System.Windows.Controls;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Services;

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
}
