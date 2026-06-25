using Athlon.Agent.App.Controls;

namespace Athlon.Agent.App.Services;

public interface IChatLayoutSurface
{
    WebChatView ChatWebView { get; }

    ComposerInputControl ComposerInput { get; }

    MainWindowLayoutElements ChatLayoutElements { get; }
}
