using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Navigation;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.App.Views;
using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.App;

public partial class MainWindow : Window, IMainWindowLayoutHost
{
    private readonly MainShellViewModel _viewModel;
    private readonly ClipboardImageAttachmentReader _clipboardImageReader;
    private readonly MainWindowLayoutBinder _layoutBinder;
    private readonly MainWindowShutdownCoordinator _shutdownCoordinator;
    private readonly PageViewFactory _pageViewFactory;
    private readonly Services.ComputerUse.ComputerUseOverlayRegistry _computerUseOverlayRegistry;
    private readonly IRuntimeDiagnosticEventSink _runtimeDiagnosticEventSink;
    private readonly IAgentRunContextAccessor _runContextAccessor;
    private bool _shutdownInProgress;
    private readonly PropertyChangedEventHandler _viewModelPropertyChangedHandler;
    private readonly EventHandler<ContextSidebarLayoutChangedEventArgs> _contextSidebarLayoutChangedHandler;
    private readonly EventHandler<ContextSidebarLayoutChangedEventArgs> _navigationSidebarLayoutChangedHandler;
    private readonly RoutedEventHandler _loadedHandler;
    private readonly CancelEventHandler _closingHandler;
    private Windows.ComputerUseOverlayWindow? _computerUseOverlayWindow;
    private Windows.WorkspaceComposerOverlayWindow? _workspaceComposerOverlayWindow;
    private bool _workspaceComposerPositionPending;
    private WindowState? _preComputerUseWindowState;

    private const double WorkspaceComposerMaxWidth = 784;
    private const double WorkspaceComposerSideMargin = 12;
    private const double WorkspaceComposerBottomMargin = 8;

    public MainWindow(
        MainShellViewModel viewModel,
        ClipboardImageAttachmentReader clipboardImageReader,
        PageViewFactory pageViewFactory,
        MainWindowShutdownCoordinator shutdownCoordinator,
        Services.ComputerUse.ComputerUseOverlayRegistry computerUseOverlayRegistry,
        IAgentRunContextAccessor runContextAccessor,
        IRuntimeDiagnosticEventSink runtimeDiagnosticEventSink)
    {
        App.StartupTrace("MainWindow constructor entered");
        InitializeComponent();
        App.StartupTrace("MainWindow InitializeComponent completed");
        Behaviors.MaximizedWindowWorkArea.Attach(this);
        _viewModel = viewModel;
        _clipboardImageReader = clipboardImageReader;
        _pageViewFactory = pageViewFactory;
        _shutdownCoordinator = shutdownCoordinator;
        _computerUseOverlayRegistry = computerUseOverlayRegistry;
        _runContextAccessor = runContextAccessor;
        _runtimeDiagnosticEventSink = runtimeDiagnosticEventSink;
        _layoutBinder = new MainWindowLayoutBinder(_viewModel, new MainWindowLayoutElements
        {
            NavigationSidebarColumn = NavigationSidebarColumn,
            MainContentColumn = MainContentColumn,
            NavigationSidebarPanel = NavigationSidebarPanel,
            NavigationSidebarSplitter = NavigationSidebarSplitter,
            NavigationSidebarCollapsedRail = NavigationSidebarCollapsedRail,
            ContextSidebarColumn = ContextSidebarColumn,
            ContextSidebarPanel = ContextSidebarPanel,
            ContextSidebarSplitter = ContextSidebarSplitter,
            ContextSidebarCollapsedRail = ContextSidebarCollapsedRail,
            MainWorkspaceCardInner = MainWorkspaceCardInner
        });
        DataContext = _viewModel;
        _viewModelPropertyChangedHandler = OnViewModelPropertyChanged;
        _contextSidebarLayoutChangedHandler = (_, args) =>
            ExecuteOnUiThread(() =>
            {
                _layoutBinder.ApplyContextSidebar(args);
                ScheduleWorkspaceComposerPosition();
            });
        _navigationSidebarLayoutChangedHandler = (_, args) =>
            ExecuteOnUiThread(() => _layoutBinder.ApplyNavigationSidebar(args));
        _loadedHandler = OnMainWindowLoaded;
        _closingHandler = OnMainWindowClosing;
        _viewModel.ContextSidebarLayoutChanged += _contextSidebarLayoutChangedHandler;
        _viewModel.NavigationSidebarLayoutChanged += _navigationSidebarLayoutChangedHandler;
        _viewModel.PropertyChanged += _viewModelPropertyChangedHandler;
        Loaded += _loadedHandler;
        Closing += _closingHandler;
        Closed += OnMainWindowClosed;
        LocationChanged += OnMainWindowBoundsChanged;
        SizeChanged += OnMainWindowBoundsChanged;
        StateChanged += OnMainWindowStateChanged;
        DpiChanged += OnMainWindowDpiChanged;
        ContextSidebarPanel.LayoutUpdated += OnContextSidebarLayoutUpdated;
        App.StartupTrace("MainWindow DataContext assigned");
    }

    private void ExecuteOnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.InvokeAsync(action);
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        _pageViewFactory.Preload(AppPage.Chat);
        if (_viewModel.CurrentPageView is ChatPageView chatPage)
        {
            _layoutBinder.BindChatSurface(chatPage);
            ((IChatLayoutSurface)chatPage).ComposerInput.ClipboardImageReader = _clipboardImageReader;
            _layoutBinder.ApplyAll();
            ChatWebView.InitializationFailed += OnChatWebViewInitializationFailed;
            ChatWebView.ScriptExecutionFailed += OnChatWebViewScriptExecutionFailed;
            _viewModel.AttachChatView(ChatWebView);
            RegisterChatScrollService(chatPage);
        }
        else
        {
            _layoutBinder.ApplyAll();
        }

        SyncWorkspaceComposerOverlay();
        _viewModel.StartUpdatePolling();
        App.StartupTrace("MainWindow page host ready");
    }

    private WebChatView ChatWebView =>
        _viewModel.CurrentPageView is ChatPageView chatPage
            ? ((IChatLayoutSurface)chatPage).ChatWebView
            : throw new InvalidOperationException("Chat page is not loaded.");

    private void OnChatWebViewInitializationFailed(object? sender, string message)
    {
        _viewModel.ShowShellToast(message, ShellToastKind.Error);

        var context = _runContextAccessor.Current;
        var sessionId = context?.SessionId;
        var runId = context?.RunId;

        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: sessionId,
            runId: runId,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.UiWebview,
            phase: RuntimeDiagnosticPhase.Initialize,
            eventType: "ui.webview_init_failed",
            severity: RuntimeDiagnosticSeverity.Error,
            errorCode: RuntimeDiagnosticErrorCodes.UiWebviewInitFailed,
            message: message);
        _ = _runtimeDiagnosticEventSink.EnqueueAsync(evt, CancellationToken.None);
    }

    private void OnChatWebViewScriptExecutionFailed(object? sender, string message)
    {
        var context = _runContextAccessor.Current;
        var sessionId = context?.SessionId;
        var runId = context?.RunId;

        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: sessionId,
            runId: runId,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.UiWebview,
            phase: RuntimeDiagnosticPhase.Invoke,
            eventType: "ui.webview_script_failed",
            severity: RuntimeDiagnosticSeverity.Error,
            errorCode: RuntimeDiagnosticErrorCodes.UiWebviewScriptFailed,
            message: message);
        _ = _runtimeDiagnosticEventSink.EnqueueAsync(evt, CancellationToken.None);
    }

    private void RegisterChatScrollService(ChatPageView chatPage)
    {
        var webChat = ((IChatLayoutSurface)chatPage).ChatWebView;
        if (Application.Current is not App { Services: { } services })
        {
            return;
        }

        var chatScrollService = services.GetService<IChatScrollService>();
        chatScrollService?.Register(
            () => _ = webChat.ScrollToBottomAsync(),
            () => _ = webChat.ScrollToBottomImmediateAsync());
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.StopUpdatePolling();
        CloseWorkspaceComposerOverlay();
        CloseComputerUseOverlay(restoreMainWindow: false);
        if (_viewModel.CurrentPageView is ChatPageView chatPage)
        {
            ((IChatLayoutSurface)chatPage).ChatWebView.InitializationFailed -= OnChatWebViewInitializationFailed;
            ((IChatLayoutSurface)chatPage).ChatWebView.ScriptExecutionFailed -= OnChatWebViewScriptExecutionFailed;
        }
        _viewModel.ContextSidebarLayoutChanged -= _contextSidebarLayoutChangedHandler;
        _viewModel.NavigationSidebarLayoutChanged -= _navigationSidebarLayoutChangedHandler;
        _viewModel.PropertyChanged -= _viewModelPropertyChangedHandler;
        Loaded -= _loadedHandler;
        Closing -= _closingHandler;
        LocationChanged -= OnMainWindowBoundsChanged;
        SizeChanged -= OnMainWindowBoundsChanged;
        StateChanged -= OnMainWindowStateChanged;
        DpiChanged -= OnMainWindowDpiChanged;
        ContextSidebarPanel.LayoutUpdated -= OnContextSidebarLayoutUpdated;
    }

    internal void ShowShutdownOverlay() =>
        ShutdownOverlay.Visibility = Visibility.Visible;

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownInProgress)
        {
            return;
        }

        if (!await _shutdownCoordinator.TryCloseAsync(this, _viewModel, e).ConfigureAwait(true))
        {
            return;
        }

        _shutdownInProgress = true;
        CloseWorkspaceComposerOverlay();
        Application.Current.Shutdown();
    }

    void IMainWindowLayoutHost.OnNavigationSidebarDragCompleted() =>
        _layoutBinder.OnNavigationSidebarDragCompleted();

    void IMainWindowLayoutHost.OnEditorPaneDragCompleted() =>
        _layoutBinder.OnEditorPaneDragCompleted();

    void IMainWindowLayoutHost.OnComposerDragCompleted() =>
        _layoutBinder.OnComposerDragCompleted();

    void IMainWindowLayoutHost.OnContextSidebarDragCompleted() =>
        _layoutBinder.OnContextSidebarDragCompleted();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainShellViewModel.CurrentPageView))
        {
            if (_viewModel.CurrentPageView is ChatPageView chatPage)
            {
                _layoutBinder.BindChatSurface(chatPage);
                ((IChatLayoutSurface)chatPage).ComposerInput.ClipboardImageReader = _clipboardImageReader;
                _layoutBinder.ApplyEditorPane();
                _layoutBinder.ApplyComposer();
                ((IChatLayoutSurface)chatPage).ChatWebView.InitializationFailed += OnChatWebViewInitializationFailed;
                ((IChatLayoutSurface)chatPage).ChatWebView.ScriptExecutionFailed += OnChatWebViewScriptExecutionFailed;
                _viewModel.AttachChatView(((IChatLayoutSurface)chatPage).ChatWebView);
                RegisterChatScrollService(chatPage);
            }
        }

        if (e.PropertyName == nameof(MainShellViewModel.HasChatMessages))
        {
            ExecuteOnUiThread(() => _layoutBinder.ApplyContextSidebarImmediate());
        }

        if (e.PropertyName == nameof(MainShellViewModel.NavigationSidebarWidth))
        {
            ExecuteOnUiThread(_layoutBinder.ApplyNavigationSidebarImmediate);
        }

        if (e.PropertyName == nameof(MainShellViewModel.HasOpenEditorTabs))
        {
            ExecuteOnUiThread(_layoutBinder.ApplyEditorPane);
        }

        if (e.PropertyName == nameof(MainShellViewModel.IsWorkspaceMaximized))
        {
            ExecuteOnUiThread(() =>
            {
                _layoutBinder.ApplyContextSidebarImmediate();
                SyncWorkspaceComposerOverlay(focusComposer: _viewModel.IsWorkspaceMaximized);
            });
        }

        if (e.PropertyName == nameof(MainShellViewModel.IsComputerUseOverlayActive))
        {
            ExecuteOnUiThread(() =>
            {
                SyncWorkspaceComposerOverlay();
                SyncComputerUseOverlay();
            });
        }

        // Stay on the Computer Use overlay after a turn completes so the user can keep typing.
        // Overlay closes only when the user dismisses it (or the shell ends CU mode).
        if (e.PropertyName == nameof(MainShellViewModel.IsBusy)
            && _viewModel.IsComputerUseOverlayActive
            && !_viewModel.IsBusy)
        {
            ExecuteOnUiThread(() => _computerUseOverlayWindow?.FocusComposer());
        }
    }

    private void OnMainWindowBoundsChanged(object? sender, EventArgs e) =>
        ScheduleWorkspaceComposerPosition();

    private void OnMainWindowStateChanged(object? sender, EventArgs e) =>
        SyncWorkspaceComposerOverlay();

    private void OnMainWindowDpiChanged(object sender, DpiChangedEventArgs e) =>
        ScheduleWorkspaceComposerPosition();

    private void OnContextSidebarLayoutUpdated(object? sender, EventArgs e) =>
        ScheduleWorkspaceComposerPosition();

    private void SyncWorkspaceComposerOverlay(bool focusComposer = false)
    {
        var shouldShow = IsLoaded
            && !_shutdownInProgress
            && WindowState != WindowState.Minimized
            && _viewModel.IsWorkspaceMaximized
            && _viewModel.IsContextSidebarVisible
            && _viewModel.WorkspacePane.CanMaximizeActiveTab
            && !_viewModel.IsComputerUseOverlayActive;

        if (!shouldShow)
        {
            CloseWorkspaceComposerOverlay();
            return;
        }

        if (_workspaceComposerOverlayWindow is not { IsLoaded: true })
        {
            var overlay = new Windows.WorkspaceComposerOverlayWindow(_viewModel, _clipboardImageReader)
            {
                Owner = this
            };
            overlay.SizeChanged += OnWorkspaceComposerSizeChanged;
            overlay.Closed += OnWorkspaceComposerClosed;
            _workspaceComposerOverlayWindow = overlay;
            overlay.Show();
            focusComposer = true;
        }

        ScheduleWorkspaceComposerPosition();
        if (focusComposer)
        {
            _workspaceComposerOverlayWindow.FocusComposer();
        }
    }

    private void ScheduleWorkspaceComposerPosition()
    {
        if (_workspaceComposerPositionPending
            || _workspaceComposerOverlayWindow is not { IsLoaded: true })
        {
            return;
        }

        _workspaceComposerPositionPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _workspaceComposerPositionPending = false;
            PositionWorkspaceComposerOverlay();
        }, DispatcherPriority.Render);
    }

    private void PositionWorkspaceComposerOverlay()
    {
        if (_workspaceComposerOverlayWindow is not { IsLoaded: true } overlay
            || !ContextSidebarPanel.IsLoaded
            || ContextSidebarPanel.ActualWidth <= 0
            || ContextSidebarPanel.ActualHeight <= 0
            || overlay.ActualHeight <= 0)
        {
            return;
        }

        var topLeftPixels = ContextSidebarPanel.PointToScreen(new Point(0, 0));
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(topLeftPixels);

        var availableWidth = Math.Max(0, ContextSidebarPanel.ActualWidth - (WorkspaceComposerSideMargin * 2));
        var targetWidth = Math.Min(WorkspaceComposerMaxWidth, availableWidth);
        if (targetWidth <= 0)
        {
            return;
        }

        overlay.Width = targetWidth;
        overlay.Left = topLeft.X + ((ContextSidebarPanel.ActualWidth - targetWidth) / 2);
        overlay.Top = topLeft.Y
            + ContextSidebarPanel.ActualHeight
            - overlay.ActualHeight
            - WorkspaceComposerBottomMargin;
        overlay.Opacity = 1;
    }

    private void OnWorkspaceComposerSizeChanged(object sender, SizeChangedEventArgs e) =>
        ScheduleWorkspaceComposerPosition();

    private void OnWorkspaceComposerClosed(object? sender, EventArgs e)
    {
        if (_workspaceComposerOverlayWindow is not { } overlay)
        {
            return;
        }

        overlay.SizeChanged -= OnWorkspaceComposerSizeChanged;
        overlay.Closed -= OnWorkspaceComposerClosed;
        _workspaceComposerOverlayWindow = null;
    }

    private void CloseWorkspaceComposerOverlay()
    {
        if (_workspaceComposerOverlayWindow is not { } overlay)
        {
            return;
        }

        overlay.SizeChanged -= OnWorkspaceComposerSizeChanged;
        overlay.Closed -= OnWorkspaceComposerClosed;
        _workspaceComposerOverlayWindow = null;
        overlay.CloseFromOwner();
    }

    private void SyncComputerUseOverlay()
    {
        if (_viewModel.IsComputerUseOverlayActive)
        {
            ShowComputerUseOverlay();
            return;
        }

        CloseComputerUseOverlay(restoreMainWindow: false);
    }

    private void ShowComputerUseOverlay()
    {
        if (_computerUseOverlayWindow is { IsLoaded: true })
        {
            _computerUseOverlayWindow.Activate();
            _computerUseOverlayWindow.FocusComposer();
            return;
        }

        // Remember Normal/Maximized so ending CU restores the pre-minimize state.
        _preComputerUseWindowState = WindowState == WindowState.Minimized
            ? WindowState.Normal
            : WindowState;
        WindowState = WindowState.Minimized;
        var overlay = new Windows.ComputerUseOverlayWindow(_viewModel);
        overlay.PromptSubmitted += OnComputerUsePromptSubmitted;
        overlay.Closed += OnComputerUseOverlayClosed;
        _computerUseOverlayWindow = overlay;
        _computerUseOverlayRegistry.Register(overlay);
        overlay.Show();
        overlay.FocusComposer();
    }

    private async void OnComputerUsePromptSubmitted(object? sender, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        await _viewModel.SendComputerUseAsync(prompt).ConfigureAwait(true);
    }

    private void OnComputerUseOverlayClosed(object? sender, EventArgs e)
    {
        if (_computerUseOverlayWindow is not null)
        {
            _computerUseOverlayRegistry.Unregister(_computerUseOverlayWindow);
            _computerUseOverlayWindow.PromptSubmitted -= OnComputerUsePromptSubmitted;
            _computerUseOverlayWindow.Closed -= OnComputerUseOverlayClosed;
            _computerUseOverlayWindow = null;
        }

        _viewModel.EndComputerUseOverlay();
        if (_viewModel.IsBusy && _viewModel.StopCommand.CanExecute(null))
        {
            _viewModel.StopCommand.Execute(null);
        }

        RestoreMainWindowAfterComputerUse();
    }

    private void CloseComputerUseOverlay(bool restoreMainWindow)
    {
        if (_computerUseOverlayWindow is null)
        {
            if (restoreMainWindow)
            {
                _viewModel.EndComputerUseOverlay();
                RestoreMainWindowAfterComputerUse();
            }
            return;
        }

        var window = _computerUseOverlayWindow;
        _computerUseOverlayRegistry.Unregister(window);
        window.PromptSubmitted -= OnComputerUsePromptSubmitted;
        window.Closed -= OnComputerUseOverlayClosed;
        _computerUseOverlayWindow = null;
        window.Close();
        if (restoreMainWindow)
        {
            _viewModel.EndComputerUseOverlay();
            RestoreMainWindowAfterComputerUse();
        }
        else
        {
            _preComputerUseWindowState = null;
        }
    }

    private void RestoreMainWindowAfterComputerUse()
    {
        if (_shutdownInProgress)
        {
            _preComputerUseWindowState = null;
            return;
        }

        var restore = _preComputerUseWindowState ?? WindowState.Normal;
        _preComputerUseWindowState = null;
        // Never leave the shell minimized after CU; treat Minimized as Normal.
        WindowState = restore == WindowState.Minimized ? WindowState.Normal : restore;
        Activate();
    }

    private void HelpAboutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var about = new Windows.AboutWindow
        {
            Owner = this
        };
        about.ShowDialog();
    }
}
