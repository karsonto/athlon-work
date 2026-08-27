using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Views;

public partial class SkillHubPageView : UserControl
{
    private SkillHubViewModel? _hub;
    private bool _initialized;
    private Task? _initTask;

    public SkillHubPageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookHub();
        HookHub(ResolveHub(e.NewValue));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
        HookHub(ResolveHub(DataContext));
        ApplyThemeBackground();
        _ = EnsureInitializedAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        // Keep hub hooked: ContentControl can Unload/Load while the WebView is still
        // interactive; clearing _hub here silently drops add/manage messages.
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        ApplyThemeBackground();
        _ = ApplyThemeStylesAsync();
    }

    private SkillHubViewModel? ResolveHub(object? dataContext)
    {
        if (dataContext is SkillHubViewModel hub)
        {
            return hub;
        }

        if (dataContext is MainShellViewModel shell)
        {
            return shell.SkillHubVm;
        }

        return null;
    }

    private void HookHub(SkillHubViewModel? hub)
    {
        if (ReferenceEquals(_hub, hub))
        {
            return;
        }

        UnhookHub();
        _hub = hub;
        if (_hub is not null)
        {
            _hub.CatalogJsonReady += OnCatalogJsonReady;
        }
    }

    private void UnhookHub()
    {
        if (_hub is null)
        {
            return;
        }

        _hub.CatalogJsonReady -= OnCatalogJsonReady;
        _hub = null;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            await ApplyThemeStylesAsync().ConfigureAwait(true);
            if (_hub is not null)
            {
                await _hub.RefreshAsync().ConfigureAwait(true);
            }

            return;
        }

        _initTask ??= InitializeAsync();
        await _initTask.ConfigureAwait(true);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await WebView2Initializer.EnsureCoreWebView2Async(SkillHubWebView).ConfigureAwait(true);
            var core = SkillHubWebView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 is not ready.");

            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsScriptEnabled = true;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;

            var assetsDir = SkillHubAssets.AssetsDirectory;
            if (!Directory.Exists(assetsDir))
            {
                throw new DirectoryNotFoundException($"Skill Hub assets not found: {assetsDir}");
            }

            core.SetVirtualHostNameToFolderMapping(
                SkillHubAssets.VirtualHost,
                assetsDir,
                CoreWebView2HostResourceAccessKind.Allow);
            ApplyThemeBackground();
            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    return;
                }

                try
                {
                    await ApplyThemeStylesAsync().ConfigureAwait(true);
                    if (_hub is not null)
                    {
                        await _hub.RefreshAsync().ConfigureAwait(true);
                    }
                }
                catch (Exception ex)
                {
                    App.StartupTrace($"SkillHub refresh after navigate failed: {ex.Message}");
                }
            };
            core.Navigate(SkillHubAssets.EntryUrl);
            _initialized = true;
        }
        catch (Exception ex)
        {
            App.StartupTrace($"SkillHub WebView init failed: {ex.Message}");
        }
    }

    private void ApplyThemeBackground()
    {
        var bg = AppThemeManager.Current.Chrome.ChatBackgroundTop;
        SkillHubWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(bg.A, bg.R, bg.G, bg.B);
        Background = new SolidColorBrush(bg);
    }

    private async Task ApplyThemeStylesAsync()
    {
        if (SkillHubWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await SkillHubWebView.EnsureCoreWebView2Async().ConfigureAwait(true);
            var tokensB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(SkillHubThemeStyles.GetThemeTokenStyles()));
            var payload = JsonSerializer.Serialize(new { type = "theme", tokensB64 });
            SkillHubWebView.CoreWebView2?.PostWebMessageAsJson(payload);

            // Also apply via script in case the page missed the postMessage race.
            await SkillHubWebView.CoreWebView2!
                .ExecuteScriptAsync(SkillHubThemeStyles.BuildThemeUpdateScript())
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"SkillHub ApplyThemeStyles failed: {ex.Message}");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Always prefer WebMessageAsJson — JS posts objects via chrome.webview.postMessage({...}).
        // TryGetWebMessageAsString throws (or mis-reads) for non-string payloads.
        string json;
        try
        {
            json = UnwrapWebMessageJson(e.WebMessageAsJson);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"SkillHub read message failed: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var hub = _hub ?? ResolveHub(DataContext);
                if (hub is null)
                {
                    App.StartupTrace("SkillHub message dropped: hub is null.");
                    await PostInstallFailureFromMessageAsync(json, "Skill Hub is not ready.").ConfigureAwait(true);
                    return;
                }

                HookHub(hub);

                // Apply theme first when the page signals ready.
                if (json.Contains("\"ready\"", StringComparison.Ordinal))
                {
                    await ApplyThemeStylesAsync().ConfigureAwait(true);
                }

                await hub.HandleWebMessageAsync(json).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                App.StartupTrace($"SkillHub message failed: {ex.Message}");
                await PostInstallFailureFromMessageAsync(json, ex.Message).ConfigureAwait(true);
            }
        });
    }

    private void OnCatalogJsonReady(object? sender, string json) =>
        _ = Dispatcher.InvokeAsync(() => PostJsonToWebViewAsync(json));

    private async Task PostJsonToWebViewAsync(string json)
    {
        if (SkillHubWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await SkillHubWebView.EnsureCoreWebView2Async().ConfigureAwait(true);
            SkillHubWebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"SkillHub post failed: {ex.Message}");
        }
    }

    private async Task PostInstallFailureFromMessageAsync(string requestJson, string error)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)
                || !string.Equals(typeEl.GetString(), "add", StringComparison.Ordinal))
            {
                return;
            }

            var id = SkillHubViewModel.ReadWireId(root);
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var payload = JsonSerializer.Serialize(new
            {
                type = "installResult",
                id,
                ok = false,
                error
            });
            await PostJsonToWebViewAsync(payload).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"SkillHub install-failure post failed: {ex.Message}");
        }
    }

    /// <summary>
    /// If the web content posted a stringified JSON payload, <see cref="CoreWebView2WebMessageReceivedEventArgs.WebMessageAsJson"/>
    /// is a JSON string literal — unwrap it so <see cref="JsonDocument.Parse"/> yields an object.
    /// </summary>
    internal static string UnwrapWebMessageJson(string webMessageAsJson)
    {
        if (string.IsNullOrWhiteSpace(webMessageAsJson))
        {
            return webMessageAsJson;
        }

        try
        {
            using var doc = JsonDocument.Parse(webMessageAsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                return doc.RootElement.GetString() ?? webMessageAsJson;
            }
        }
        catch (JsonException)
        {
            // return raw
        }

        return webMessageAsJson;
    }
}
