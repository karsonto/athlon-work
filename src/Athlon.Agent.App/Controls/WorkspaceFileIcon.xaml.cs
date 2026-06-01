using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Controls;

public partial class WorkspaceFileIcon : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(WorkspaceFileIconKind),
        typeof(WorkspaceFileIcon),
        new PropertyMetadata(WorkspaceFileIconKind.File, OnKindChanged));

    public WorkspaceFileIconKind Kind
    {
        get => (WorkspaceFileIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public WorkspaceFileIcon()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyKind(Kind);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AppThemeManager.ThemeChanged += OnThemeChanged;

    private void OnUnloaded(object sender, RoutedEventArgs e) => AppThemeManager.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyKind(Kind);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WorkspaceFileIcon icon)
        {
            icon.ApplyKind((WorkspaceFileIconKind)e.NewValue);
        }
    }

    private void ApplyKind(WorkspaceFileIconKind kind)
    {
        var spec = WorkspaceFileIconCatalog.Get(kind);
        IconPath.Data = spec.Geometry;
        IconPath.Opacity = spec.Opacity;
        if (spec.UseStroke)
        {
            IconPath.Fill = Brushes.Transparent;
            IconPath.Stroke = spec.Fill;
            IconPath.StrokeThickness = spec.StrokeThickness;
            IconPath.StrokeStartLineCap = PenLineCap.Round;
            IconPath.StrokeEndLineCap = PenLineCap.Round;
        }
        else
        {
            IconPath.Fill = spec.Fill;
            IconPath.Stroke = null;
            IconPath.StrokeThickness = 0;
        }

        if (string.IsNullOrEmpty(spec.Badge))
        {
            BadgeText.Visibility = Visibility.Collapsed;
            IconViewbox.Visibility = Visibility.Visible;
            IconViewbox.Opacity = 1;
            return;
        }

        BadgeText.Text = spec.Badge;
        BadgeText.Foreground = spec.Fill;
        BadgeText.FontSize = spec.Badge.Length > 2 ? 5.5 : 7.5;
        BadgeText.Visibility = Visibility.Visible;
        IconViewbox.Visibility = spec.HideGeometry ? Visibility.Collapsed : Visibility.Visible;
        IconViewbox.Opacity = spec.HideGeometry ? 1 : 0.35;
    }

    private static class WorkspaceFileIconCatalog
    {
        private static readonly Geometry FolderGeometry = ParseGeometry(
            "M2,5 L2,14 L14,14 L14,7 L9,7 L7,5 Z");

        private static readonly Geometry DocumentGeometry = ParseGeometry(
            "M4,2 L4,14 L12,14 L12,6 L8,6 L6,4 L4,4 Z M6,4 L6,6 L8,6");

        private static readonly Geometry GitGeometry = ParseGeometry(
            "M8,2 C5.2,2 3,4.2 3,7 C3,9.8 5.2,12 8,12 C10.8,12 13,9.8 13,7 C13,4.2 10.8,2 8,2 Z M8,4.5 L9.8,6.3 L7.5,8.6 L6.2,7.3 L8,5.5 Z");

        private static readonly Geometry MsBuildGeometry = ParseGeometry(
            "M3,12 L6,4 L9,12 M11,4 L14,12 M10.5,10 L11.5,10");

        public static IconSpec Get(WorkspaceFileIconKind kind)
        {
            var color = AppThemeManager.Current.FileIcons.Resolve(kind);
            var fill = AppThemeColor.ToFrozenBrush(color);
            return kind switch
            {
                WorkspaceFileIconKind.Placeholder => new IconSpec(DocumentGeometry, fill, 0.7),
                WorkspaceFileIconKind.Folder => new IconSpec(FolderGeometry, fill),
                WorkspaceFileIconKind.CSharp => new IconSpec(DocumentGeometry, fill, badge: "C#"),
                WorkspaceFileIconKind.Project => new IconSpec(DocumentGeometry, fill, badge: "P"),
                WorkspaceFileIconKind.Solution => new IconSpec(DocumentGeometry, fill, badge: "S"),
                WorkspaceFileIconKind.Markdown => new IconSpec(DocumentGeometry, fill, badge: "M"),
                WorkspaceFileIconKind.Json => new IconSpec(DocumentGeometry, fill, badge: "{}"),
                WorkspaceFileIconKind.Xml => new IconSpec(DocumentGeometry, fill, badge: "</>"),
                WorkspaceFileIconKind.Html => new IconSpec(DocumentGeometry, fill, badge: "<>"),
                WorkspaceFileIconKind.Css => new IconSpec(DocumentGeometry, fill, badge: "#"),
                WorkspaceFileIconKind.JavaScript => new IconSpec(DocumentGeometry, fill, badge: "JS"),
                WorkspaceFileIconKind.TypeScript => new IconSpec(DocumentGeometry, fill, badge: "TS"),
                WorkspaceFileIconKind.Python => new IconSpec(DocumentGeometry, fill, badge: "Py"),
                WorkspaceFileIconKind.Shell => new IconSpec(DocumentGeometry, fill, badge: "▶"),
                WorkspaceFileIconKind.PowerShell => new IconSpec(DocumentGeometry, fill, badge: ">_"),
                WorkspaceFileIconKind.Git => new IconSpec(GitGeometry, fill),
                WorkspaceFileIconKind.Yaml => new IconSpec(DocumentGeometry, fill, badge: "Y"),
                WorkspaceFileIconKind.Docker => new IconSpec(DocumentGeometry, fill, badge: "D"),
                WorkspaceFileIconKind.Image => new IconSpec(DocumentGeometry, fill, badge: "▣"),
                WorkspaceFileIconKind.MsBuild => new IconSpec(MsBuildGeometry, fill, useStroke: true, strokeThickness: 1.15),
                WorkspaceFileIconKind.Config => new IconSpec(DocumentGeometry, fill, badge: "cfg"),
                _ => new IconSpec(DocumentGeometry, fill),
            };
        }

        private static Geometry ParseGeometry(string data) => Geometry.Parse(data);

        public sealed class IconSpec
        {
            public IconSpec(
                Geometry geometry,
                Brush fill,
                double opacity = 1,
                string? badge = null,
                bool hideGeometry = false,
                bool useStroke = false,
                double strokeThickness = 1)
            {
                Geometry = geometry;
                Fill = fill;
                Opacity = opacity;
                Badge = badge;
                HideGeometry = hideGeometry;
                UseStroke = useStroke;
                StrokeThickness = strokeThickness;
            }

            public Geometry Geometry { get; }
            public Brush Fill { get; }
            public double Opacity { get; }
            public string? Badge { get; }
            public bool HideGeometry { get; }
            public bool UseStroke { get; }
            public double StrokeThickness { get; }
        }
    }
}
