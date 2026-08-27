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
        UnhookHub();
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
        string json;
        try
        {
            json = e.TryGetWebMessageAsString();
        }
        catch (InvalidOperationException)
        {
            json = e.WebMessageAsJson;
        }

        if (string.IsNullOrWhiteSpace(json) || _hub is null)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                // Apply theme first when the page signals ready.
                if (json.Contains("\"ready\"", StringComparison.Ordinal))
                {
                    await ApplyThemeStylesAsync().ConfigureAwait(true);
                }

                await _hub.HandleWebMessageAsync(json).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                App.StartupTrace($"SkillHub message failed: {ex.Message}");
            }
        });
    }

    private void OnCatalogJsonReady(object? sender, string json)
    {
        _ = Dispatcher.InvokeAsync(async () =>
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
        });
    }
}
