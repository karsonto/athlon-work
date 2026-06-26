using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Core.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Controls;

public partial class WebChatView : UserControl
{
    private readonly ChatHtmlBuilder _htmlBuilder = new();
    private Task? _initTask;
    private bool _initialized;
    private bool _documentReady;
    private bool _loggedCanRenderBlock;
    private int _navigationGeneration;
    private int _renderGeneration;
    private IReadOnlyList<ChatMessageViewModel> _pendingMessages = Array.Empty<ChatMessageViewModel>();
    private bool _pendingShowToolCalls;
    private bool _needsRender;
    private bool _renderRetryScheduled;
    private bool _renderInProgress;
    private bool _renderQueuedWhileInProgress;

    public WebChatView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnSizeChanged;
        LayoutUpdated += OnLayoutUpdated;
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
    }

    public event EventHandler<string>? InitializationFailed;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        if (ChatWebView.CoreWebView2 is not null)
        {
            ChatWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        ApplyThemeBackground();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        _ = RunRenderPipelineSafeAsync(_renderGeneration);

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _ = RunRenderPipelineSafeAsync(_renderGeneration);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_needsRender && CanRender())
        {
            _ = RunRenderPipelineSafeAsync(_renderGeneration);
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_needsRender && CanRender())
        {
            _ = RunRenderPipelineSafeAsync(_renderGeneration);
        }
    }

    private bool CanRender() =>
        IsVisible && ActualWidth >= 1 && ActualHeight >= 1;

    public async Task LoadMessagesAsync(IReadOnlyList<ChatMessageViewModel> messages, bool showToolCalls = false)
    {
        _pendingMessages = messages;
        _pendingShowToolCalls = showToolCalls;
        _needsRender = true;
        var generation = Interlocked.Increment(ref _renderGeneration);
        await RunRenderPipelineSafeAsync(generation).ConfigureAwait(true);

        if (_needsRender && generation == _renderGeneration)
        {
            ScheduleRenderRetry();
        }
    }

    public Task ApplyAssistantMarkdownAsync(ChatMessageViewModel message) =>
        ExecuteScriptWhenReadyAsync($"handleEvent({ChatEventSerializer.SerializeStaticAssistantHtml(message)});");

    public Task ApplyToolResultMarkdownAsync(ChatMessageViewModel message) =>
        ExecuteScriptWhenReadyAsync($"handleEvent({ChatEventSerializer.SerializeToolResultMarkdown(message)});");

    public Task DispatchUserMessageAsync(ChatMessageViewModel message) =>
        ExecuteScriptWhenReadyAsync($"handleEvent({ChatEventSerializer.SerializeUserMessage(message)});");

    public Task DispatchEventAsync(AgentStreamEvent streamEvent) =>
        ExecuteScriptWhenReadyAsync(_htmlBuilder.BuildDispatchScript(streamEvent));

    private void ScheduleRenderRetry()
    {
        if (_renderRetryScheduled)
        {
            return;
        }

        _renderRetryScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _renderRetryScheduled = false;
            if (_needsRender)
            {
                _ = RunRenderPipelineSafeAsync(_renderGeneration);
            }
        });
    }

    private async Task RunRenderPipelineSafeAsync(int expectedGeneration)
    {
        try
        {
            await EnsureInitializedAndRenderAsync(expectedGeneration).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView render pipeline failed: {ex}");
            ReportInitializationFailure($"聊天渲染失败：{ex.Message}");
        }
    }

    private async Task EnsureInitializedAndRenderAsync(int expectedGeneration)
    {
        await EnsureReadyAsync().ConfigureAwait(true);
        if (!_needsRender || expectedGeneration != _renderGeneration)
        {
            return;
        }

        if (!CanRender())
        {
            if (!_loggedCanRenderBlock)
            {
                _loggedCanRenderBlock = true;
                App.StartupTrace(
                    $"WebChatView CanRender=false (visible={IsVisible}, width={ActualWidth:0.##}, height={ActualHeight:0.##})");
            }

            return;
        }

        _loggedCanRenderBlock = false;

        if (_renderInProgress)
        {
            _renderQueuedWhileInProgress = true;
            return;
        }

        _renderInProgress = true;
        try
        {
            var navigated = await NavigateHtmlAsync(
                _htmlBuilder.BuildDocumentHtml(_pendingMessages, _pendingShowToolCalls, ResolveSsoDisplayName()),
                expectedGeneration).ConfigureAwait(true);
            if (!navigated || expectedGeneration != _renderGeneration)
            {
                return;
            }

            _needsRender = false;
            App.StartupTrace($"WebChatView rendered {_pendingMessages.Count} messages");
        }
        finally
        {
            _renderInProgress = false;
            if (_needsRender && _renderQueuedWhileInProgress)
            {
                _renderQueuedWhileInProgress = false;
                ScheduleRenderRetry();
            }
            else
            {
                _renderQueuedWhileInProgress = false;
            }
        }
    }

    private async Task EnsureReadyAsync()
    {
        if (_initialized)
        {
            return;
        }

        if (_initTask is { IsFaulted: true } or { IsCanceled: true })
        {
            _initTask = null;
        }

        _initTask ??= InitializeWebViewAsync();
        await _initTask.ConfigureAwait(true);
    }

    private async Task InitializeWebViewAsync()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            await WebView2Initializer.EnsureCoreWebView2Async(ChatWebView).ConfigureAwait(true);
            ChatWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            ChatWebView.CoreWebView2.Settings.IsScriptEnabled = true;
            ChatWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            ChatWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ChatWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            var assetsDir = ChatMarkdownAssets.AssetsDirectory;
            if (Directory.Exists(assetsDir))
            {
                ChatWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    ChatMarkdownAssets.VirtualHost,
                    assetsDir,
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            ApplyThemeBackground();
            _initialized = true;
            App.StartupTrace("WebChatView initialization completed");
        }
        catch (Exception ex)
        {
            _initTask = null;
            App.StartupTrace($"WebChatView initialization failed: {ex}");
            ReportInitializationFailure($"聊天渲染初始化失败：{ex.Message}");
            throw;
        }
    }

    private void ReportInitializationFailure(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportInitializationFailure(message));
            return;
        }

        InitializationFailed?.Invoke(this, message);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "copy", StringComparison.Ordinal))
            {
                return;
            }

            var text = root.TryGetProperty("text", out var textElement)
                ? textElement.GetString()
                : null;
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
            }
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView copy message failed: {ex.Message}");
        }
    }

    private void ApplyThemeBackground()
    {
        var chatBg = AppThemeManager.Current.Chrome.ChatBackgroundTop;
        ChatWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            chatBg.A,
            chatBg.R,
            chatBg.G,
            chatBg.B);
        Background = new SolidColorBrush(chatBg);
    }

    private async Task ExecuteScriptWhenReadyAsync(string script)
    {
        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            var documentReady = await WaitForDocumentReadyAsync().ConfigureAwait(true);
            if (!documentReady)
            {
                App.StartupTrace($"WebChatView ExecuteScript skipped: document not ready ({script.Length} chars)");
                return;
            }

            await ChatWebView.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView ExecuteScript failed ({script.Length} chars): {ex.Message}");
        }
    }

    private async Task<bool> WaitForDocumentReadyAsync()
    {
        if (_documentReady)
        {
            return true;
        }

        var generation = _navigationGeneration;
        var deadline = Environment.TickCount64 + 5000;
        while (!_documentReady && generation == _navigationGeneration && Environment.TickCount64 < deadline)
        {
            await Task.Delay(16).ConfigureAwait(true);
        }

        if (!_documentReady && generation == _navigationGeneration)
        {
            App.StartupTrace("WebChatView WaitForDocumentReady timed out after 5s");
            return false;
        }

        return _documentReady;
    }

    private async Task<bool> NavigateHtmlAsync(string html, int expectedGeneration)
    {
        await EnsureReadyAsync().ConfigureAwait(true);
        if (!CanRender())
        {
            _needsRender = true;
            return false;
        }

        if (expectedGeneration != _renderGeneration)
        {
            return false;
        }

        var generation = ++_navigationGeneration;
        _documentReady = false;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            ChatWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            if (generation == _navigationGeneration)
            {
                _documentReady = e.IsSuccess;
                if (!e.IsSuccess)
                {
                    App.StartupTrace($"WebChatView navigation failed: {e.WebErrorStatus}");
                }
            }

            tcs.TrySetResult(e.IsSuccess);
        }

        try
        {
            ChatWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            ChatWebView.NavigateToString(html);
            var success = await tcs.Task.ConfigureAwait(true);
            return success && generation == _navigationGeneration && expectedGeneration == _renderGeneration;
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView NavigateToString failed: {ex}");
            return false;
        }
    }

    public Task ScrollToBottomAsync() =>
        ExecuteScriptWhenReadyAsync("scrollToBottom();");

    private static string? ResolveSsoDisplayName()
    {
        if (Application.Current is not App { Services: { } services })
        {
            return null;
        }

        return services.GetService<ICurrentSsoUserContext>()?.DisplayName;
    }
}
