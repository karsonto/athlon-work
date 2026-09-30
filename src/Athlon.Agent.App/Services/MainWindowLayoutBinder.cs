using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Athlon.Agent.App.Animations;
using Athlon.Agent.App.Behaviors;
using Athlon.Agent.App.ViewModels;
using Microsoft.Xaml.Behaviors;
using UiLayoutConstraints = Athlon.Agent.App.UiLayoutConstraints;

namespace Athlon.Agent.App.Services;

public sealed partial class MainWindowLayoutBinder(MainShellViewModel viewModel, MainWindowLayoutElements elements)
{
    private const double SidebarAnimationDurationMs = 200;
    // Keep chat flush to the context sidebar; splitter overlays the shared edge.
    private const double ContextSidebarEdgeGutterWidth = 0;

    private Storyboard? _contextSidebarStoryboard;
    private int _contextSidebarAnimationGeneration;
    private Storyboard? _navigationSidebarStoryboard;
    private int _navigationSidebarAnimationGeneration;

    public void BindChatSurface(IChatLayoutSurface chatSurface)
    {
        elements.EditorPaneColumn = chatSurface.ChatLayoutElements.EditorPaneColumn;
        elements.EditorPaneHost = chatSurface.ChatLayoutElements.EditorPaneHost;
        elements.EditorChatSplitter = chatSurface.ChatLayoutElements.EditorChatSplitter;
        elements.ComposerRow = chatSurface.ChatLayoutElements.ComposerRow;
    }

    public void ApplyAll()
    {
        ApplyNavigationSidebarImmediate();
        ApplyContextSidebarImmediate();
        ApplyEditorPane();
        ApplyComposer();
    }


    public void ApplyEditorPane()
    {
        if (elements.EditorPaneColumn is null || elements.EditorPaneHost is null || elements.EditorChatSplitter is null)
        {
            return;
        }

        if (!viewModel.HasOpenEditorTabs)
        {
            elements.EditorPaneColumn.MinWidth = 0;
            elements.EditorPaneColumn.MaxWidth = double.PositiveInfinity;
            elements.EditorPaneColumn.Width = new GridLength(0);
            elements.EditorPaneHost.Visibility = Visibility.Collapsed;
            elements.EditorChatSplitter.Visibility = Visibility.Collapsed;
            elements.EditorChatSplitter.IsEnabled = false;
            return;
        }

        elements.EditorPaneColumn.MinWidth = UiLayoutConstraints.EditorPaneMinWidth;
        elements.EditorPaneColumn.MaxWidth = UiLayoutConstraints.EditorPaneMaxWidth;
        elements.EditorPaneColumn.Width = new GridLength(viewModel.EditorPaneWidth);
        elements.EditorPaneHost.Visibility = Visibility.Visible;
        elements.EditorChatSplitter.Visibility = Visibility.Visible;
        elements.EditorChatSplitter.IsEnabled = true;
        elements.EditorChatSplitter.IsHitTestVisible = true;
    }

    public void OnEditorPaneDragCompleted()
    {
        if (elements.EditorPaneColumn is null || !viewModel.HasOpenEditorTabs)
        {
            return;
        }

        var width = elements.EditorPaneColumn.ActualWidth;
        if (width >= UiLayoutConstraints.EditorPaneMinWidth)
        {
            viewModel.UpdateEditorPaneWidth(width);
        }
    }

    public void ApplyComposer()
    {
        if (elements.ComposerRow is null)
        {
            return;
        }

        // Content-driven height: grow with typed text instead of a fixed drag size.
        elements.ComposerRow.MinHeight = 0;
        elements.ComposerRow.MaxHeight = UiLayoutConstraints.ComposerMaxHeight;
        elements.ComposerRow.Height = GridLength.Auto;
    }

    public void OnComposerDragCompleted()
    {
        if (elements.ComposerRow is null)
        {
            return;
        }

        var height = elements.ComposerRow.ActualHeight;
        if (height >= UiLayoutConstraints.ComposerMinHeight)
        {
            viewModel.UpdateComposerHeight(height);
        }
    }

}

public sealed class MainWindowLayoutElements
{
    public ColumnDefinition? NavigationSidebarColumn { get; init; }
    public ColumnDefinition? MainContentColumn { get; init; }
    public ColumnDefinition? EditorPaneColumn { get; set; }
    public ColumnDefinition? ContextSidebarColumn { get; init; }
    public RowDefinition? ComposerRow { get; set; }
    public FrameworkElement? EditorPaneHost { get; set; }
    public FrameworkElement? EditorChatSplitter { get; set; }
    public FrameworkElement? NavigationSidebarPanel { get; init; }
    public FrameworkElement? NavigationSidebarSplitter { get; init; }
    public FrameworkElement? NavigationSidebarCollapsedRail { get; init; }
    public FrameworkElement? ContextSidebarPanel { get; init; }
    public FrameworkElement? ContextSidebarSplitter { get; init; }
    public FrameworkElement? ContextSidebarCollapsedRail { get; init; }
    public Border? MainWorkspaceCardInner { get; init; }
}
