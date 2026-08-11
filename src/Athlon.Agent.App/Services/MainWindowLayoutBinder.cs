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
    private int _contextSidebarAnimationGeneration;

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

        if (viewModel.IsNavigationSidebarVisible)
        {
            elements.NavigationSidebarColumn.MinWidth = UiLayoutConstraints.NavigationSidebarMinWidth;
            elements.NavigationSidebarColumn.MaxWidth = UiLayoutConstraints.NavigationSidebarMaxWidth;
            elements.NavigationSidebarColumn.Width = new GridLength(viewModel.NavigationSidebarWidth);
            if (elements.NavigationSidebarPanel is not null)
            {
                elements.NavigationSidebarPanel.Visibility = Visibility.Visible;
            }

            if (elements.NavigationSidebarSplitter is not null)
            {
                elements.NavigationSidebarSplitter.Visibility = Visibility.Visible;
                elements.NavigationSidebarSplitter.IsEnabled = true;
            }

            if (elements.NavigationSidebarCollapsedRail is not null)
            {
                elements.NavigationSidebarCollapsedRail.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            elements.NavigationSidebarColumn.MinWidth = 0;
            elements.NavigationSidebarColumn.MaxWidth = double.PositiveInfinity;
            elements.NavigationSidebarColumn.Width = new GridLength(0);
            if (elements.NavigationSidebarPanel is not null)
            {
                elements.NavigationSidebarPanel.Visibility = Visibility.Collapsed;
            }

            if (elements.NavigationSidebarSplitter is not null)
            {
                elements.NavigationSidebarSplitter.Visibility = Visibility.Collapsed;
                elements.NavigationSidebarSplitter.IsEnabled = false;
            }

            if (elements.NavigationSidebarCollapsedRail is not null)
            {
                elements.NavigationSidebarCollapsedRail.Visibility = Visibility.Visible;
            }
        }
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
        ClearContextSidebarPropertyAnimations();

        if (elements.ContextSidebarColumn is null || elements.ContextSidebarPanel is null || elements.ContextSidebarSplitter is null)
        {
            return;
        }

        if (viewModel.IsContextSidebarVisible)
        {
            ApplyContextSidebarOpenedLayout();
        }
        else
        {
            ApplyContextSidebarClosedLayout();
        }
    }

    public void AnimateContextSidebar()
    {
        if (elements.ContextSidebarColumn is null || elements.ContextSidebarPanel is null || elements.ContextSidebarSplitter is null)
        {
            return;
        }

        if (viewModel.IsWorkspaceMaximized)
        {
            ApplyContextSidebarImmediate();
            return;
        }

        StopContextSidebarAnimation();
        ClearContextSidebarPropertyAnimations();

        var generation = ++_contextSidebarAnimationGeneration;
        var opening = viewModel.IsContextSidebarVisible;
        var fromWidth = opening
            ? 0
            : Math.Max(GetCurrentSidebarWidth(), viewModel.ContextSidebarWidth);
        var toWidth = opening ? viewModel.ContextSidebarWidth : 0;
        var fromGutter = opening ? 0 : ContextSidebarEdgeGutterWidth;

        elements.ContextSidebarColumn.MinWidth = 0;
        elements.ContextSidebarColumn.MaxWidth = double.PositiveInfinity;
        elements.ContextSidebarColumn.Width = new GridLength(fromWidth);

        if (elements.ContextSidebarCollapsedRail is not null)
        {
            elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
        }

        if (opening)
        {
            elements.ContextSidebarPanel.Visibility = Visibility.Visible;
            elements.ContextSidebarPanel.Opacity = 0;
            elements.ContextSidebarSplitter.Visibility = Visibility.Visible;
            elements.ContextSidebarSplitter.IsEnabled = false;
        }
        else
        {
            elements.ContextSidebarPanel.Visibility = Visibility.Visible;
            elements.ContextSidebarPanel.Opacity = 1;
            elements.ContextSidebarSplitter.Visibility = Visibility.Visible;
            elements.ContextSidebarSplitter.IsEnabled = false;
        }

        viewModel.SetContextSidebarEdgeGutterWidth(fromGutter);

        var widthAnimation = new GridLengthAnimation
        {
            From = new GridLength(fromWidth),
            To = new GridLength(toWidth),
            Duration = TimeSpan.FromMilliseconds(SidebarAnimationDurationMs),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = opening
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var opacityAnimation = new DoubleAnimation
        {
            From = opening ? 0 : 1,
            To = opening ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(SidebarAnimationDurationMs),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = opening
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        widthAnimation.CurrentTimeInvalidated += (_, _) =>
            SyncEdgeGutterToSidebarWidth(opening, fromWidth);

        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        storyboard.Children.Add(widthAnimation);
        storyboard.Children.Add(opacityAnimation);

        Storyboard.SetTarget(widthAnimation, elements.ContextSidebarColumn);
        Storyboard.SetTargetProperty(widthAnimation, new PropertyPath(ColumnDefinition.WidthProperty));

        Storyboard.SetTarget(opacityAnimation, elements.ContextSidebarPanel);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        storyboard.Completed += (_, _) =>
        {
            if (generation != _contextSidebarAnimationGeneration)
            {
                return;
            }

            _contextSidebarStoryboard = null;
            ClearContextSidebarPropertyAnimations();
            if (viewModel.IsContextSidebarVisible)
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
        if (viewModel.IsWorkspaceMaximized)
        {
            ApplyWorkspaceMaximizedLayout();
            return;
        }

        if (!viewModel.IsContextSidebarVisible || elements.ContextSidebarColumn is null)
        {
            return;
        }

        ClearContextSidebarPropertyAnimations();

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

        ApplyContextSidebarOpenedLayout();
    }

    private void ApplyContextSidebarOpenedLayout()
    {
        if (elements.ContextSidebarColumn is null || elements.ContextSidebarPanel is null || elements.ContextSidebarSplitter is null)
        {
            return;
        }

        if (viewModel.IsWorkspaceMaximized)
        {
            ApplyWorkspaceMaximizedLayout();
            return;
        }

        ClearContextSidebarPropertyAnimations();
        RestoreMainContentColumn();

        elements.ContextSidebarColumn.MinWidth = UiLayoutConstraints.ContextSidebarMinWidth;
        elements.ContextSidebarColumn.MaxWidth = UiLayoutConstraints.ContextSidebarMaxWidth;
        elements.ContextSidebarColumn.Width = new GridLength(viewModel.ContextSidebarWidth);
        elements.ContextSidebarPanel.Visibility = Visibility.Visible;
        elements.ContextSidebarPanel.Opacity = 1;
        // Leave a grip strip so the leading splitter is never covered by the panel.
        elements.ContextSidebarPanel.Margin = new Thickness(12, 0, 0, 0);
        elements.ContextSidebarSplitter.Visibility = Visibility.Visible;
        elements.ContextSidebarSplitter.IsEnabled = true;
        elements.ContextSidebarSplitter.IsHitTestVisible = true;
        if (elements.ContextSidebarCollapsedRail is not null)
        {
            elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
        }

        viewModel.SetContextSidebarEdgeGutterWidth(ContextSidebarEdgeGutterWidth);
    }

    private void ApplyWorkspaceMaximizedLayout()
    {
        if (elements.ContextSidebarColumn is null || elements.ContextSidebarPanel is null || elements.ContextSidebarSplitter is null)
        {
            return;
        }

        ClearContextSidebarPropertyAnimations();
        CollapseMainContentColumn();

        elements.ContextSidebarColumn.MinWidth = 0;
        elements.ContextSidebarColumn.MaxWidth = double.PositiveInfinity;
        elements.ContextSidebarColumn.Width = new GridLength(1, GridUnitType.Star);
        elements.ContextSidebarPanel.Visibility = Visibility.Visible;
        elements.ContextSidebarPanel.Opacity = 1;
        elements.ContextSidebarPanel.Margin = new Thickness(0);
        elements.ContextSidebarSplitter.Visibility = Visibility.Collapsed;
        elements.ContextSidebarSplitter.IsEnabled = false;
        elements.ContextSidebarSplitter.IsHitTestVisible = false;
        if (elements.ContextSidebarCollapsedRail is not null)
        {
            elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
        }

        viewModel.SetContextSidebarEdgeGutterWidth(0);
    }

    private void RestoreMainContentColumn()
    {
        if (elements.MainContentColumn is null)
        {
            return;
        }

        elements.MainContentColumn.MinWidth = 0;
        elements.MainContentColumn.MaxWidth = double.PositiveInfinity;
        elements.MainContentColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void CollapseMainContentColumn()
    {
        if (elements.MainContentColumn is null)
        {
            return;
        }

        elements.MainContentColumn.MinWidth = 0;
        elements.MainContentColumn.MaxWidth = double.PositiveInfinity;
        elements.MainContentColumn.Width = new GridLength(0);
    }

    private void ApplyContextSidebarClosedLayout()
    {
        if (elements.ContextSidebarColumn is null || elements.ContextSidebarPanel is null || elements.ContextSidebarSplitter is null)
        {
            return;
        }

        ClearContextSidebarPropertyAnimations();
        RestoreMainContentColumn();

        elements.ContextSidebarColumn.MinWidth = 0;
        elements.ContextSidebarColumn.MaxWidth = double.PositiveInfinity;
        elements.ContextSidebarColumn.Width = new GridLength(0);
        elements.ContextSidebarPanel.Visibility = Visibility.Collapsed;
        elements.ContextSidebarPanel.Opacity = 0;
        elements.ContextSidebarPanel.Margin = new Thickness(0);
        elements.ContextSidebarSplitter.Visibility = Visibility.Collapsed;
        elements.ContextSidebarSplitter.IsEnabled = false;
        if (elements.ContextSidebarCollapsedRail is not null)
        {
            elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
        }

        viewModel.SetContextSidebarEdgeGutterWidth(0);
    }

    private void StopContextSidebarAnimation()
    {
        if (_contextSidebarStoryboard is null)
        {
            return;
        }

        _contextSidebarAnimationGeneration++;
        _contextSidebarStoryboard.Stop();
        _contextSidebarStoryboard = null;
        ClearContextSidebarPropertyAnimations();
    }

    private void ClearContextSidebarPropertyAnimations()
    {
        // Animating ColumnDefinition.Width holds a clock that blocks GridSplitter until cleared.
        elements.ContextSidebarColumn?.BeginAnimation(ColumnDefinition.WidthProperty, null);
        elements.ContextSidebarPanel?.BeginAnimation(UIElement.OpacityProperty, null);
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
}
