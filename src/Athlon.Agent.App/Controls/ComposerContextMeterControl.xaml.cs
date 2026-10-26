using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Controls;

public partial class ComposerContextMeterControl : UserControl
{
    public static readonly DependencyProperty IsFlyoutOpenProperty =
        DependencyProperty.Register(
            nameof(IsFlyoutOpen),
            typeof(bool),
            typeof(ComposerContextMeterControl),
            new PropertyMetadata(false, OnIsFlyoutOpenChanged));

    private FrameworkElement? _composerHost;

    public ComposerContextMeterControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => DetachComposerHost();
    }

    public bool IsFlyoutOpen
    {
        get => (bool)GetValue(IsFlyoutOpenProperty);
        set => SetValue(IsFlyoutOpenProperty, value);
    }

    private void MeterButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsFlyoutOpen)
        {
            IsFlyoutOpen = false;
            return;
        }

        AttachToComposer();
        IsFlyoutOpen = true;
    }

    private void CloseFlyout_OnClick(object sender, RoutedEventArgs e) =>
        IsFlyoutOpen = false;

    private void UsageTrack_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is ContextOccupancyViewModel occupancy && e.NewSize.Width > 1)
        {
            occupancy.SetTrackWidth(e.NewSize.Width);
        }
    }

    private static void OnIsFlyoutOpenChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is ComposerContextMeterControl meter && args.NewValue is false)
        {
            meter.DetachComposerHost();
        }
    }

    private void AttachToComposer()
    {
        DetachComposerHost();
        var host = FindComposerHost();
        UsagePopup.Placement = PlacementMode.Top;
        UsagePopup.HorizontalOffset = 0;
        UsagePopup.VerticalOffset = -8;
        if (host == null)
        {
            UsagePopup.PlacementTarget = MeterButton;
            UsageCard.Width = 360;
            return;
        }

        _composerHost = host;
        UsagePopup.PlacementTarget = host;
        if (host.ActualWidth > 1)
        {
            UsageCard.Width = host.ActualWidth;
        }

        host.SizeChanged += ComposerHost_OnSizeChanged;
    }

    private void ComposerHost_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_composerHost != null)
        {
            UsageCard.Width = _composerHost.ActualWidth;
        }
    }

    private void DetachComposerHost()
    {
        if (_composerHost != null)
        {
            _composerHost.SizeChanged -= ComposerHost_OnSizeChanged;
            _composerHost = null;
        }
    }

    private Border? FindComposerHost()
    {
        DependencyObject? current = VisualTreeHelper.GetParent(this);
        while (current != null)
        {
            if (current is Border border)
            {
                return border;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
