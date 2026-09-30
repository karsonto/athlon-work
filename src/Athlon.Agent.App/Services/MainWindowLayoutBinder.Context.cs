using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Athlon.Agent.App.Animations;
using Athlon.Agent.App.Behaviors;
using Athlon.Agent.App.ViewModels;
using Microsoft.Xaml.Behaviors;
using UiLayoutConstraints = Athlon.Agent.App.UiLayoutConstraints;

namespace Athlon.Agent.App.Services;

/// <summary>Context sidebar, workspace maximize and shared-edge layout.</summary>
public sealed partial class MainWindowLayoutBinder
{
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
            elements.ContextSidebarPanel.Margin = new Thickness(0);
            elements.ContextSidebarSplitter.Visibility = Visibility.Visible;
            elements.ContextSidebarSplitter.IsEnabled = false;
            SetMainWorkspaceCardSharedEdge(flushToSidebar: true);
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
        // Flush against the main card / editor; splitter sits on the leading edge with ZIndex.
        elements.ContextSidebarPanel.Margin = new Thickness(0);
        elements.ContextSidebarSplitter.Visibility = Visibility.Visible;
        elements.ContextSidebarSplitter.IsEnabled = true;
        elements.ContextSidebarSplitter.IsHitTestVisible = true;
        if (elements.ContextSidebarCollapsedRail is not null)
        {
            elements.ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
        }

        SetMainWorkspaceCardSharedEdge(flushToSidebar: true);
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

        SetMainWorkspaceCardSharedEdge(flushToSidebar: true);
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

        SetMainWorkspaceCardSharedEdge(flushToSidebar: false);
        viewModel.SetContextSidebarEdgeGutterWidth(0);
    }

    private void SetMainWorkspaceCardCornersUniform()
    {
        if (elements.MainWorkspaceCardInner is null)
        {
            return;
        }

        var corners = new CornerRadius(AppLayoutMetrics.MainWorkspaceCardCornerRadiusValue);
        elements.MainWorkspaceCardInner.CornerRadius = corners;

        foreach (var behavior in Interaction.GetBehaviors(elements.MainWorkspaceCardInner))
        {
            if (behavior is RoundedClipBehavior clip)
            {
                clip.CornerRadius = corners;
                break;
            }
        }
    }

    private void SetMainWorkspaceCardSharedEdge(bool flushToSidebar)
    {
        if (elements.MainWorkspaceCardInner is null)
        {
            return;
        }

        SetMainWorkspaceCardCornersUniform();
        // Drop the right border when the files pane is open so the shared edge is a single line, not a gutter.
        elements.MainWorkspaceCardInner.BorderThickness = flushToSidebar
            ? new Thickness(1, 1, 0, 1)
            : new Thickness(1);
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
