using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.ViewModels;
using UiLayoutConstraints = Athlon.Agent.App.UiLayoutConstraints;

namespace Athlon.Agent.App.Services;

public sealed class MainWindowLayoutBinder(MainWindowViewModel viewModel, MainWindowLayoutElements elements)
{
    public void ApplyAll()
    {
        ApplyNavigationSidebar();
        ApplyContextSidebar();
        ApplyEditorPane();
        ApplyComposer();
    }

    public void ApplyNavigationSidebar()
    {
        if (elements.NavigationSidebarColumn is null)
        {
            return;
        }

        elements.NavigationSidebarColumn.MinWidth = UiLayoutConstraints.NavigationSidebarMinWidth;
        elements.NavigationSidebarColumn.MaxWidth = UiLayoutConstraints.NavigationSidebarMaxWidth;
        elements.NavigationSidebarColumn.Width = new GridLength(viewModel.NavigationSidebarWidth);
    }

    public void OnNavigationSidebarDragCompleted()
    {
        if (elements.NavigationSidebarColumn is null)
        {
            return;
        }

        var width = elements.NavigationSidebarColumn.ActualWidth;
        if (width >= UiLayoutConstraints.NavigationSidebarMinWidth)
        {
            viewModel.UpdateNavigationSidebarWidth(width);
        }
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
            return;
        }

        elements.EditorPaneColumn.MinWidth = UiLayoutConstraints.EditorPaneMinWidth;
        elements.EditorPaneColumn.MaxWidth = UiLayoutConstraints.EditorPaneMaxWidth;
        elements.EditorPaneColumn.Width = new GridLength(viewModel.EditorPaneWidth);
        elements.EditorPaneHost.Visibility = Visibility.Visible;
        elements.EditorChatSplitter.Visibility = Visibility.Visible;
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

        elements.ComposerRow.MinHeight = UiLayoutConstraints.ComposerMinHeight;
        elements.ComposerRow.MaxHeight = UiLayoutConstraints.ComposerMaxHeight;
        elements.ComposerRow.Height = new GridLength(viewModel.ComposerHeight);
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

    public void ApplyContextSidebar()
    {
        if (elements.ContextSidebarColumn is null || elements.ContextSidebarPanel is null || elements.ContextSidebarSplitter is null)
        {
            return;
        }

        if (viewModel.IsContextSidebarVisible)
        {
            elements.ContextSidebarColumn.MinWidth = UiLayoutConstraints.ContextSidebarMinWidth;
            elements.ContextSidebarColumn.MaxWidth = UiLayoutConstraints.ContextSidebarMaxWidth;
            elements.ContextSidebarColumn.Width = new GridLength(viewModel.ContextSidebarWidth);
            elements.ContextSidebarPanel.Visibility = Visibility.Visible;
            elements.ContextSidebarSplitter.Visibility = Visibility.Visible;
            if (elements.ContextSidebarCollapsedRail is not null)
            {
                elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            elements.ContextSidebarColumn.MinWidth = 0;
            elements.ContextSidebarColumn.MaxWidth = double.PositiveInfinity;
            elements.ContextSidebarColumn.Width = new GridLength(0);
            elements.ContextSidebarPanel.Visibility = Visibility.Collapsed;
            elements.ContextSidebarSplitter.Visibility = Visibility.Collapsed;
            if (elements.ContextSidebarCollapsedRail is not null)
            {
                elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
            }
        }
    }

    public void OnContextSidebarDragCompleted()
    {
        if (!viewModel.IsContextSidebarVisible || elements.ContextSidebarColumn is null)
        {
            return;
        }

        var width = elements.ContextSidebarColumn.ActualWidth;
        if (width < UiLayoutConstraints.ContextSidebarCollapseDragThreshold)
        {
            viewModel.SetContextSidebarVisible(false);
            _ = viewModel.PersistUiLayoutForSidebarAsync();
            return;
        }

        if (width >= UiLayoutConstraints.ContextSidebarMinWidth)
        {
            viewModel.UpdateContextSidebarWidth(width);
        }
    }
}

public sealed class MainWindowLayoutElements
{
    public ColumnDefinition? NavigationSidebarColumn { get; init; }
    public ColumnDefinition? EditorPaneColumn { get; init; }
    public ColumnDefinition? ContextSidebarColumn { get; init; }
    public RowDefinition? ComposerRow { get; init; }
    public FrameworkElement? EditorPaneHost { get; init; }
    public FrameworkElement? EditorChatSplitter { get; init; }
    public FrameworkElement? ContextSidebarPanel { get; init; }
    public FrameworkElement? ContextSidebarSplitter { get; init; }
    public FrameworkElement? ContextSidebarCollapsedRail { get; init; }
}
