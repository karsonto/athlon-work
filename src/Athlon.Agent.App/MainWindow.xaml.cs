using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Navigation;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.App;

public partial class MainWindow : Window, IMainWindowLayoutHost
{
    private readonly MainShellViewModel _viewModel;
    private readonly ClipboardImageAttachmentReader _clipboardImageReader;
    private readonly AppUpdateService _updateService;
    private readonly MainWindowLayoutBinder _layoutBinder;
    private readonly MainWindowShutdownCoordinator _shutdownCoordinator;
    private readonly PageViewFactory _pageViewFactory;
    private readonly Services.ComputerUse.ComputerUseOverlayRegistry _computerUseOverlayRegistry;
    private bool _shutdownInProgress;
    private readonly PropertyChangedEventHandler _viewModelPropertyChangedHandler;
    private readonly EventHandler<ContextSidebarLayoutChangedEventArgs> _contextSidebarLayoutChangedHandler;
    private readonly RoutedEventHandler _loadedHandler;
    private readonly CancelEventHandler _closingHandler;
    private Windows.ComputerUseOverlayWindow? _computerUseOverlayWindow;
    private Windows.WorkspaceComposerOverlayWindow? _workspaceComposerOverlayWindow;
    private bool _workspaceComposerPositionPending;

    private const double WorkspaceComposerMaxWidth = 784;
    private const double WorkspaceComposerSideMargin = 12;
    private const double WorkspaceComposerBottomMargin = 8;

    public MainWindow(
        MainShellViewModel viewModel,
        ClipboardImageAttachmentReader clipboardImageReader,
        AppUpdateService updateService,
        PageViewFactory pageViewFactory,
        MainWindowShutdownCoordinator shutdownCoordinator,
        Services.ComputerUse.ComputerUseOverlayRegistry computerUseOverlayRegistry)
    {
        App.StartupTrace("MainWindow constructor entered");
        InitializeComponent();
        App.StartupTrace("MainWindow InitializeComponent completed");
        Behaviors.MaximizedWindowWorkArea.Attach(this);
        _viewModel = viewModel;
        _clipboardImageReader = clipboardImageReader;
        _updateService = updateService;
        _pageViewFactory = pageViewFactory;
        _shutdownCoordinator = shutdownCoordinator;
        _computerUseOverlayRegistry = computerUseOverlayRegistry;
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
            ContextSidebarCollapsedRail = ContextSidebarCollapsedRail
        });
        DataContext = _viewModel;
        _viewModelPropertyChangedHandler = OnViewModelPropertyChanged;
        _contextSidebarLayoutChangedHandler = (_, args) =>
            ExecuteOnUiThread(() =>
            {
                _layoutBinder.ApplyContextSidebar(args);
                ScheduleWorkspaceComposerPosition();
            });
        _loadedHandler = OnMainWindowLoaded;
        _closingHandler = OnMainWindowClosing;
        _viewModel.ContextSidebarLayoutChanged += _contextSidebarLayoutChangedHandler;
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
            _viewModel.AttachChatView(ChatWebView);
            RegisterChatScrollService(chatPage);
        }
        else
        {
            _layoutBinder.ApplyAll();
        }

        SyncWorkspaceComposerOverlay();
        App.StartupTrace("MainWindow page host ready");
    }

    private WebChatView ChatWebView =>
        _viewModel.CurrentPageView is ChatPageView chatPage
            ? ((IChatLayoutSurface)chatPage).ChatWebView
            : throw new InvalidOperationException("Chat page is not loaded.");

    private void OnChatWebViewInitializationFailed(object? sender, string message) =>
        _viewModel.Settings.SettingsStatus = message;

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
            () => _ = webChat.ScrollToBottomAsync());
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        CloseWorkspaceComposerOverlay();
        CloseComputerUseOverlay(restoreMainWindow: false);
        if (_viewModel.CurrentPageView is ChatPageView chatPage)
        {
            ((IChatLayoutSurface)chatPage).ChatWebView.InitializationFailed -= OnChatWebViewInitializationFailed;
        }
        _viewModel.ContextSidebarLayoutChanged -= _contextSidebarLayoutChangedHandler;
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
                _viewModel.AttachChatView(((IChatLayoutSurface)chatPage).ChatWebView);
                RegisterChatScrollService(chatPage);
            }
        }

        if (e.PropertyName == nameof(MainShellViewModel.HasChatMessages))
        {
            ExecuteOnUiThread(() => _layoutBinder.ApplyContextSidebarImmediate());
        }

        if (e.PropertyName == nameof(MainShellViewModel.IsNavigationSidebarVisible)
            || e.PropertyName == nameof(MainShellViewModel.NavigationSidebarWidth))
        {
            ExecuteOnUiThread(_layoutBinder.ApplyNavigationSidebar);
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

        if (e.PropertyName == nameof(MainShellViewModel.IsBusy))
        {
            if (_viewModel.IsComputerUseOverlayActive && !_viewModel.IsBusy)
            {
                ExecuteOnUiThread(() => CloseComputerUseOverlay(restoreMainWindow: true));
            }
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
        if (!_shutdownInProgress)
        {
            WindowState = WindowState.Normal;
            Activate();
        }
    }

    private void CloseComputerUseOverlay(bool restoreMainWindow)
    {
        if (_computerUseOverlayWindow is null)
        {
            if (restoreMainWindow && !_shutdownInProgress)
            {
                _viewModel.EndComputerUseOverlay();
                WindowState = WindowState.Normal;
                Activate();
            }
            return;
        }

        var window = _computerUseOverlayWindow;
        _computerUseOverlayRegistry.Unregister(window);
        window.PromptSubmitted -= OnComputerUsePromptSubmitted;
        window.Closed -= OnComputerUseOverlayClosed;
        _computerUseOverlayWindow = null;
        window.Close();
        if (restoreMainWindow && !_shutdownInProgress)
        {
            _viewModel.EndComputerUseOverlay();
            WindowState = WindowState.Normal;
            Activate();
        }
    }

    private void HelpAboutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var about = new Windows.AboutWindow(_updateService)
        {
            Owner = this
        };
        about.ShowDialog();
    }
}
