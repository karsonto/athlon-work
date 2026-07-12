using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Athlon.Agent.App.Animations;
using Athlon.Agent.App.ViewModels;
using UiLayoutConstraints = Athlon.Agent.App.UiLayoutConstraints;

namespace Athlon.Agent.App.Services;

public sealed class MainWindowLayoutBinder(MainShellViewModel viewModel, MainWindowLayoutElements elements)
{
    private const double SidebarAnimationDurationMs = 200;
    private const double ContextSidebarEdgeGutterWidth = 12;

    private Storyboard? _contextSidebarStoryboard;

    public void BindChatSurface(IChatLayoutSurface chatSurface)
    {
        elements.EditorPaneColumn = chatSurface.ChatLayoutElements.EditorPaneColumn;
        elements.EditorPaneHost = chatSurface.ChatLayoutElements.EditorPaneHost;
        elements.EditorChatSplitter = chatSurface.ChatLayoutElements.EditorChatSplitter;
        elements.ComposerRow = chatSurface.ChatLayoutElements.ComposerRow;
    }

    public void ApplyAll()
    {
        ApplyNavigationSidebar();
        ApplyContextSidebarImmediate();
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

    public void ApplyContextSidebar(ContextSidebarLayoutChangedEventArgs? args = null)
    {
        if (args?.Animate == true)
        {
            AnimateContextSidebar();
            return;
        }

        ApplyContextSidebarImmediate();
    }

    public void ApplyContextSidebarImmediate()
    {
        StopContextSidebarAnimation();

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
            elements.ContextSidebarPanel.Opacity = 1;
            elements.ContextSidebarSplitter.Visibility = Visibility.Visible;
            elements.ContextSidebarSplitter.IsEnabled = true;
            if (elements.ContextSidebarCollapsedRail is not null)
            {
                elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
            }

            viewModel.SetContextSidebarEdgeGutterWidth(ContextSidebarEdgeGutterWidth);
        }
        else
        {
            elements.ContextSidebarColumn.MinWidth = 0;
            elements.ContextSidebarColumn.MaxWidth = double.PositiveInfinity;
            elements.ContextSidebarColumn.Width = new GridLength(0);
            elements.ContextSidebarPanel.Visibility = Visibility.Collapsed;
            elements.ContextSidebarPanel.Opacity = 0;
            elements.ContextSidebarSplitter.Visibility = Visibility.Collapsed;
            elements.ContextSidebarSplitter.IsEnabled = false;
            if (elements.ContextSidebarCollapsedRail is not null)
            {
                elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
            }

            viewModel.SetContextSidebarEdgeGutterWidth(0);
        }
    }

    public void AnimateContextSidebar()
    {
        if (elements.ContextSidebarColumn is null || elements.ContextSidebarPanel is null || elements.ContextSidebarSplitter is null)
        {
            return;
        }

        StopContextSidebarAnimation();

        var opening = viewModel.IsContextSidebarVisible;
        var fromWidth = opening ? 0 : GetCurrentSidebarWidth();
        var toWidth = opening ? viewModel.ContextSidebarWidth : 0;
        var fromGutter = opening ? 0 : ContextSidebarEdgeGutterWidth;
        var toGutter = opening ? ContextSidebarEdgeGutterWidth : 0;

        elements.ContextSidebarColumn.MinWidth = 0;
        elements.ContextSidebarColumn.MaxWidth = double.PositiveInfinity;
        elements.ContextSidebarColumn.Width = new GridLength(fromWidth);

        if (opening)
        {
            elements.ContextSidebarPanel.Visibility = Visibility.Visible;
            elements.ContextSidebarPanel.Opacity = 0;
            elements.ContextSidebarSplitter.Visibility = Visibility.Visible;
            elements.ContextSidebarSplitter.IsEnabled = false;
        }
        else
        {
            elements.ContextSidebarPanel.Opacity = 1;
            elements.ContextSidebarSplitter.IsEnabled = false;
        }

        if (elements.ContextSidebarCollapsedRail is not null)
        {
            elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
        }

        viewModel.SetContextSidebarEdgeGutterWidth(fromGutter);

        var widthAnimation = new GridLengthAnimation
        {
            From = new GridLength(fromWidth),
            To = new GridLength(toWidth),
            Duration = TimeSpan.FromMilliseconds(SidebarAnimationDurationMs),
            EasingFunction = opening
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var opacityAnimation = new DoubleAnimation
        {
            From = opening ? 0 : 1,
            To = opening ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(SidebarAnimationDurationMs),
            EasingFunction = opening
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        widthAnimation.CurrentTimeInvalidated += (_, _) =>
            SyncEdgeGutterToSidebarWidth(opening, fromWidth);

        var storyboard = new Storyboard();
        storyboard.Children.Add(widthAnimation);
        storyboard.Children.Add(opacityAnimation);

        Storyboard.SetTarget(widthAnimation, elements.ContextSidebarColumn);
        Storyboard.SetTargetProperty(widthAnimation, new PropertyPath(ColumnDefinition.WidthProperty));

        Storyboard.SetTarget(opacityAnimation, elements.ContextSidebarPanel);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        storyboard.Completed += (_, _) =>
        {
            _contextSidebarStoryboard = null;
            if (opening)
            {
                ApplyContextSidebarOpenedLayout();
            }
            else
            {
                ApplyContextSidebarClosedLayout();
            }
        };

        _contextSidebarStoryboard = storyboard;
        storyboard.Begin();
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
            viewModel.SetContextSidebarVisible(false, animate: true);
            _ = viewModel.PersistUiLayoutForSidebarAsync();
            return;
        }

        if (width >= UiLayoutConstraints.ContextSidebarMinWidth)
        {
            viewModel.UpdateContextSidebarWidth(width);
        }
    }

    private void ApplyContextSidebarOpenedLayout()
    {
        if (elements.ContextSidebarColumn is null || elements.ContextSidebarPanel is null || elements.ContextSidebarSplitter is null)
        {
            return;
        }

        elements.ContextSidebarColumn.MinWidth = UiLayoutConstraints.ContextSidebarMinWidth;
        elements.ContextSidebarColumn.MaxWidth = UiLayoutConstraints.ContextSidebarMaxWidth;
        elements.ContextSidebarColumn.Width = new GridLength(viewModel.ContextSidebarWidth);
        elements.ContextSidebarPanel.Visibility = Visibility.Visible;
        elements.ContextSidebarPanel.Opacity = 1;
        elements.ContextSidebarSplitter.Visibility = Visibility.Visible;
        elements.ContextSidebarSplitter.IsEnabled = true;
        viewModel.SetContextSidebarEdgeGutterWidth(ContextSidebarEdgeGutterWidth);
    }

    private void ApplyContextSidebarClosedLayout()
    {
        if (elements.ContextSidebarColumn is null || elements.ContextSidebarPanel is null || elements.ContextSidebarSplitter is null)
        {
            return;
        }

        elements.ContextSidebarColumn.MinWidth = 0;
        elements.ContextSidebarColumn.MaxWidth = double.PositiveInfinity;
        elements.ContextSidebarColumn.Width = new GridLength(0);
        elements.ContextSidebarPanel.Visibility = Visibility.Collapsed;
        elements.ContextSidebarPanel.Opacity = 0;
        elements.ContextSidebarSplitter.Visibility = Visibility.Collapsed;
        elements.ContextSidebarSplitter.IsEnabled = false;
        viewModel.SetContextSidebarEdgeGutterWidth(0);
    }

    private void StopContextSidebarAnimation()
    {
        if (_contextSidebarStoryboard is null)
        {
            return;
        }

        _contextSidebarStoryboard.Stop();
        _contextSidebarStoryboard = null;
    }

    private void SyncEdgeGutterToSidebarWidth(bool opening, double fromWidth)
    {
        var width = GetCurrentSidebarWidth();
        if (opening)
        {
            var target = viewModel.ContextSidebarWidth;
            var progress = target <= 0 ? 1 : Math.Clamp(width / target, 0, 1);
            viewModel.SetContextSidebarEdgeGutterWidth(ContextSidebarEdgeGutterWidth * progress);
            return;
        }

        var progressClosed = fromWidth <= 0 ? 0 : Math.Clamp(width / fromWidth, 0, 1);
        viewModel.SetContextSidebarEdgeGutterWidth(ContextSidebarEdgeGutterWidth * progressClosed);
    }

    private double GetCurrentSidebarWidth()
    {
        if (elements.ContextSidebarColumn is null)
        {
            return 0;
        }

        var width = elements.ContextSidebarColumn.Width;
        if (width.IsAbsolute)
        {
            return width.Value;
        }

        return elements.ContextSidebarColumn.ActualWidth;
    }
}

public sealed class MainWindowLayoutElements
{
    public ColumnDefinition? NavigationSidebarColumn { get; init; }
    public ColumnDefinition? EditorPaneColumn { get; set; }
    public ColumnDefinition? ContextSidebarColumn { get; init; }
    public RowDefinition? ComposerRow { get; set; }
    public FrameworkElement? EditorPaneHost { get; set; }
    public FrameworkElement? EditorChatSplitter { get; set; }
    public FrameworkElement? ContextSidebarPanel { get; init; }
    public FrameworkElement? ContextSidebarSplitter { get; init; }
    public FrameworkElement? ContextSidebarCollapsedRail { get; init; }
}
