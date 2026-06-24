namespace Athlon.Agent.App.Services;

internal interface IMainWindowLayoutHost
{
    void OnNavigationSidebarDragCompleted();

    void OnEditorPaneDragCompleted();

    void OnComposerDragCompleted();

    void OnContextSidebarDragCompleted();
}
