using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Athlon.Agent.App.Animations;
using Athlon.Agent.App.Behaviors;
using Athlon.Agent.App.ViewModels;
using Microsoft.Xaml.Behaviors;
using UiLayoutConstraints = Athlon.Agent.App.UiLayoutConstraints;

namespace Athlon.Agent.App.Services;

/// <summary>Navigation sidebar width, animation and drag.</summary>
public sealed partial class MainWindowLayoutBinder
{
    public void ApplyNavigationSidebar(ContextSidebarLayoutChangedEventArgs? args = null)
    {
        if (args?.Animate == true)
        {
            AnimateNavigationSidebar();
            return;
        }

        ApplyNavigationSidebarImmediate();
    }

    public void ApplyNavigationSidebarImmediate()
    {
        if (elements.NavigationSidebarColumn is null)
        {
            return;
        }

        StopNavigationSidebarAnimation();
        ClearNavigationSidebarPropertyAnimations();

        if (viewModel.IsNavigationSidebarVisible)
        {
            ApplyNavigationSidebarOpenedLayout();
        }
        else
        {
            ApplyNavigationSidebarClosedLayout();
        }
    }

    public void AnimateNavigationSidebar()
    {
        if (elements.NavigationSidebarColumn is null || elements.NavigationSidebarPanel is null)
        {
            ApplyNavigationSidebarImmediate();
            return;
        }

        StopNavigationSidebarAnimation();
        ClearNavigationSidebarPropertyAnimations();

        var generation = ++_navigationSidebarAnimationGeneration;
        var opening = viewModel.IsNavigationSidebarVisible;
        var fromWidth = opening
            ? 0
            : Math.Max(GetCurrentNavigationSidebarWidth(), viewModel.NavigationSidebarWidth);
        var toWidth = opening ? viewModel.NavigationSidebarWidth : 0;

        elements.NavigationSidebarColumn.MinWidth = 0;
        elements.NavigationSidebarColumn.MaxWidth = double.PositiveInfinity;
        elements.NavigationSidebarColumn.Width = new GridLength(fromWidth);

        if (elements.NavigationSidebarCollapsedRail is not null)
        {
            elements.NavigationSidebarCollapsedRail.Visibility = Visibility.Collapsed;
        }

        if (elements.NavigationSidebarSplitter is not null)
        {
            elements.NavigationSidebarSplitter.Visibility = Visibility.Visible;
            elements.NavigationSidebarSplitter.IsEnabled = false;
        }

        elements.NavigationSidebarPanel.Visibility = Visibility.Visible;
        elements.NavigationSidebarPanel.Opacity = opening ? 0 : 1;

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

        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        storyboard.Children.Add(widthAnimation);
        storyboard.Children.Add(opacityAnimation);

        Storyboard.SetTarget(widthAnimation, elements.NavigationSidebarColumn);
        Storyboard.SetTargetProperty(widthAnimation, new PropertyPath(ColumnDefinition.WidthProperty));

        Storyboard.SetTarget(opacityAnimation, elements.NavigationSidebarPanel);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        storyboard.Completed += (_, _) =>
        {
            if (generation != _navigationSidebarAnimationGeneration)
            {
                return;
            }

            _navigationSidebarStoryboard = null;
            ClearNavigationSidebarPropertyAnimations();
            if (viewModel.IsNavigationSidebarVisible)
            {
                ApplyNavigationSidebarOpenedLayout();
            }
            else
            {
                ApplyNavigationSidebarClosedLayout();
            }
        };

        _navigationSidebarStoryboard = storyboard;
        storyboard.Begin();
    }

    private void ApplyNavigationSidebarOpenedLayout()
    {
        if (elements.NavigationSidebarColumn is null)
        {
            return;
        }

        ClearNavigationSidebarPropertyAnimations();

        elements.NavigationSidebarColumn.MinWidth = UiLayoutConstraints.NavigationSidebarMinWidth;
        elements.NavigationSidebarColumn.MaxWidth = UiLayoutConstraints.NavigationSidebarMaxWidth;
        elements.NavigationSidebarColumn.Width = new GridLength(viewModel.NavigationSidebarWidth);
        if (elements.NavigationSidebarPanel is not null)
        {
            elements.NavigationSidebarPanel.Visibility = Visibility.Visible;
            elements.NavigationSidebarPanel.Opacity = 1;
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

    private void ApplyNavigationSidebarClosedLayout()
    {
        if (elements.NavigationSidebarColumn is null)
        {
            return;
        }

        ClearNavigationSidebarPropertyAnimations();

        elements.NavigationSidebarColumn.MinWidth = 0;
        elements.NavigationSidebarColumn.MaxWidth = double.PositiveInfinity;
        elements.NavigationSidebarColumn.Width = new GridLength(0);
        if (elements.NavigationSidebarPanel is not null)
        {
            elements.NavigationSidebarPanel.Visibility = Visibility.Collapsed;
            elements.NavigationSidebarPanel.Opacity = 0;
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

    private void StopNavigationSidebarAnimation()
    {
        if (_navigationSidebarStoryboard is null)
        {
            return;
        }

        _navigationSidebarAnimationGeneration++;
        _navigationSidebarStoryboard.Stop();
        _navigationSidebarStoryboard = null;
        ClearNavigationSidebarPropertyAnimations();
    }

    private void ClearNavigationSidebarPropertyAnimations()
    {
        elements.NavigationSidebarColumn?.BeginAnimation(ColumnDefinition.WidthProperty, null);
        elements.NavigationSidebarPanel?.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private double GetCurrentNavigationSidebarWidth()
    {
        if (elements.NavigationSidebarColumn is null)
        {
            return 0;
        }

        var actual = elements.NavigationSidebarColumn.ActualWidth;
        if (actual > 0)
        {
            return actual;
        }

        return elements.NavigationSidebarColumn.Width.IsAbsolute
            ? elements.NavigationSidebarColumn.Width.Value
            : 0;
    }

    public void OnNavigationSidebarDragCompleted()
    {
        if (elements.NavigationSidebarColumn is null)
        {
            return;
        }

        ClearNavigationSidebarPropertyAnimations();

        var width = elements.NavigationSidebarColumn.ActualWidth;
        if (width >= UiLayoutConstraints.NavigationSidebarMinWidth)
        {
            viewModel.UpdateNavigationSidebarWidth(width);
        }

        if (viewModel.IsNavigationSidebarVisible)
        {
            ApplyNavigationSidebarOpenedLayout();
        }
    }
}
