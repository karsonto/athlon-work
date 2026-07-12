using System.ComponentModel;
using System.Windows;
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
    private bool _shutdownInProgress;
    private readonly PropertyChangedEventHandler _viewModelPropertyChangedHandler;
    private readonly EventHandler<ContextSidebarLayoutChangedEventArgs> _contextSidebarLayoutChangedHandler;
    private readonly RoutedEventHandler _loadedHandler;
    private readonly CancelEventHandler _closingHandler;

    public MainWindow(
        MainShellViewModel viewModel,
        ClipboardImageAttachmentReader clipboardImageReader,
        AppUpdateService updateService,
        PageViewFactory pageViewFactory,
        MainWindowShutdownCoordinator shutdownCoordinator)
    {
        App.StartupTrace("MainWindow constructor entered");
        InitializeComponent();
        App.StartupTrace("MainWindow InitializeComponent completed");
        _viewModel = viewModel;
        _clipboardImageReader = clipboardImageReader;
        _updateService = updateService;
        _pageViewFactory = pageViewFactory;
        _shutdownCoordinator = shutdownCoordinator;
        _layoutBinder = new MainWindowLayoutBinder(_viewModel, new MainWindowLayoutElements
        {
            NavigationSidebarColumn = NavigationSidebarColumn,
            ContextSidebarColumn = ContextSidebarColumn,
            ContextSidebarPanel = ContextSidebarPanel,
            ContextSidebarSplitter = ContextSidebarSplitter,
            ContextSidebarCollapsedRail = ContextSidebarCollapsedRail
        });
        DataContext = _viewModel;
        _viewModelPropertyChangedHandler = OnViewModelPropertyChanged;
        _contextSidebarLayoutChangedHandler = (_, args) =>
            ExecuteOnUiThread(() => _layoutBinder.ApplyContextSidebar(args));
        _loadedHandler = OnMainWindowLoaded;
        _closingHandler = OnMainWindowClosing;
        _viewModel.ContextSidebarLayoutChanged += _contextSidebarLayoutChangedHandler;
        _viewModel.PropertyChanged += _viewModelPropertyChangedHandler;
        Loaded += _loadedHandler;
        Closing += _closingHandler;
        Closed += OnMainWindowClosed;
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
        if (_viewModel.CurrentPageView is ChatPageView chatPage)
        {
            ((IChatLayoutSurface)chatPage).ChatWebView.InitializationFailed -= OnChatWebViewInitializationFailed;
        }
        _viewModel.ContextSidebarLayoutChanged -= _contextSidebarLayoutChangedHandler;
        _viewModel.PropertyChanged -= _viewModelPropertyChangedHandler;
        Loaded -= _loadedHandler;
        Closing -= _closingHandler;
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

        if (e.PropertyName == nameof(MainShellViewModel.HasOpenEditorTabs))
        {
            ExecuteOnUiThread(_layoutBinder.ApplyEditorPane);
        }

        if (e.PropertyName == nameof(MainShellViewModel.IsBusy))
        {
            // WebView2 内部处理流式状态变化，无需额外操作
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
