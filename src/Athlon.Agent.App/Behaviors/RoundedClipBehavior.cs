using System.Windows;
using System.Windows.Media;
using Microsoft.Xaml.Behaviors;

namespace Athlon.Agent.App.Behaviors;

/// <summary>Clips a framework element to a rounded rectangle (supports per-corner radii).</summary>
public sealed class RoundedClipBehavior : Behavior<FrameworkElement>
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius),
        typeof(double),
        typeof(RoundedClipBehavior),
        new PropertyMetadata(double.NaN, OnClipChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(CornerRadius),
        typeof(RoundedClipBehavior),
        new PropertyMetadata(new CornerRadius(12), OnClipChanged));

    /// <summary>Uniform radius. When set (not NaN), overrides <see cref="CornerRadius"/>.</summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.SizeChanged += OnSizeChanged;
        UpdateClip();
    }

    protected override void OnDetaching()
    {
        AssociatedObject.SizeChanged -= OnSizeChanged;
        AssociatedObject.Clip = null;
        base.OnDetaching();
    }

    private static void OnClipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RoundedClipBehavior behavior)
            behavior.UpdateClip();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateClip();

    private void UpdateClip()
    {
        if (AssociatedObject is null)
            return;

        var width = AssociatedObject.ActualWidth;
        var height = AssociatedObject.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var corners = double.IsNaN(Radius)
            ? CornerRadius
            : new CornerRadius(Math.Max(0, Radius));

        AssociatedObject.Clip = CreateRoundedRectGeometry(width, height, corners);
    }

    private static Geometry CreateRoundedRectGeometry(double width, double height, CornerRadius r)
    {
        var tl = Math.Max(0, Math.Min(r.TopLeft, Math.Min(width, height) / 2));
        var tr = Math.Max(0, Math.Min(r.TopRight, Math.Min(width, height) / 2));
        var br = Math.Max(0, Math.Min(r.BottomRight, Math.Min(width, height) / 2));
        var bl = Math.Max(0, Math.Min(r.BottomLeft, Math.Min(width, height) / 2));

        if (tl == tr && tr == br && br == bl)
            return new RectangleGeometry(new Rect(0, 0, width, height), tl, tl);

        var figure = new PathFigure { StartPoint = new Point(tl, 0), IsClosed = true };
        figure.Segments.Add(new LineSegment(new Point(width - tr, 0), true));
        if (tr > 0)
            figure.Segments.Add(new ArcSegment(new Point(width, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true));
        else
            figure.Segments.Add(new LineSegment(new Point(width, 0), true));

        figure.Segments.Add(new LineSegment(new Point(width, height - br), true));
        if (br > 0)
            figure.Segments.Add(new ArcSegment(new Point(width - br, height), new Size(br, br), 0, false, SweepDirection.Clockwise, true));
        else
            figure.Segments.Add(new LineSegment(new Point(width, height), true));

        figure.Segments.Add(new LineSegment(new Point(bl, height), true));
        if (bl > 0)
            figure.Segments.Add(new ArcSegment(new Point(0, height - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true));
        else
            figure.Segments.Add(new LineSegment(new Point(0, height), true));

        figure.Segments.Add(new LineSegment(new Point(0, tl), true));
        if (tl > 0)
            figure.Segments.Add(new ArcSegment(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true));

        var path = new PathGeometry();
        path.Figures.Add(figure);
        path.Freeze();
        return path;
    }
}
