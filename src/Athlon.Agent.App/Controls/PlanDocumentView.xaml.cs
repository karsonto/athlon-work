using System.IO;
using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core.Harness;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Controls;

public partial class PlanDocumentView : UserControl
{
    public static readonly DependencyProperty PlanProperty = DependencyProperty.Register(
        nameof(Plan),
        typeof(SessionPlan),
        typeof(PlanDocumentView),
        new PropertyMetadata(null, OnPlanChanged));

    private bool _hostMapped;
    private string? _lastRenderedStamp;

    public PlanDocumentView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public SessionPlan? Plan
    {
        get => (SessionPlan?)GetValue(PlanProperty);
        set => SetValue(PlanProperty, value);
    }

    private static void OnPlanChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PlanDocumentView view)
        {
            _ = view.RenderAsync();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged += OnThemeChanged;
        _ = RenderAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        AppThemeManager.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _lastRenderedStamp = null;
        _ = RenderAsync();
    }

    private async Task RenderAsync()
    {
        var plan = Plan;
        if (!IsLoaded || plan is null || !plan.HasContent)
        {
            return;
        }

        var stamp = $"{plan.UpdatedAt}|{plan.Title}|{plan.Overview}|{plan.Body.Length}|{AppThemeManager.CurrentKind}";
        if (string.Equals(stamp, _lastRenderedStamp, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await WebView2Initializer.EnsureCoreWebView2Async(PlanWebView).ConfigureAwait(true);
            var core = PlanWebView.CoreWebView2;
            if (core is null)
            {
                return;
            }

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            if (!_hostMapped && Directory.Exists(MermaidPreviewHtmlBuilder.AssetsDirectory))
            {
                core.SetVirtualHostNameToFolderMapping(
                    MermaidPreviewHtmlBuilder.VirtualHostName,
                    MermaidPreviewHtmlBuilder.AssetsDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);
                _hostMapped = true;
            }

            var html = PlanDocumentHtmlBuilder.BuildDocument(plan.Title, plan.Overview, plan.Body);
            core.NavigateToString(html);
            _lastRenderedStamp = stamp;
        }
        catch
        {
            // Preview is best-effort; chat + confirm still work.
        }
    }
}
