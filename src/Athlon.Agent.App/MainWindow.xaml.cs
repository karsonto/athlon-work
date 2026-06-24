using System.ComponentModel;
using System.Windows;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.App;

public partial class MainWindow : Window, IMainWindowLayoutHost
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ClipboardImageAttachmentReader _clipboardImageReader;
    private readonly AppUpdateService _updateService;
    private readonly MainWindowLayoutBinder _layoutBinder;
    private readonly MainWindowShutdownCoordinator _shutdownCoordinator;
    private bool _shutdownInProgress;
    private readonly PropertyChangedEventHandler _viewModelPropertyChangedHandler;
    private readonly EventHandler _contextSidebarLayoutChangedHandler;
    private readonly RoutedEventHandler _loadedHandler;
    private readonly CancelEventHandler _closingHandler;

    public MainWindow(
        MainWindowViewModel viewModel,
        ClipboardImageAttachmentReader clipboardImageReader,
        AppUpdateService updateService)
    {
        App.StartupTrace("MainWindow constructor entered");
        InitializeComponent();
        App.StartupTrace("MainWindow InitializeComponent completed");
        _viewModel = viewModel;
        _clipboardImageReader = clipboardImageReader;
        _updateService = updateService;
        _shutdownCoordinator = new MainWindowShutdownCoordinator();
        _layoutBinder = new MainWindowLayoutBinder(_viewModel, new MainWindowLayoutElements
        {
            NavigationSidebarColumn = NavigationSidebarColumn,
            EditorPaneColumn = EditorPaneColumn,
            ContextSidebarColumn = ContextSidebarColumn,
            ComposerRow = ComposerRow,
            EditorPaneHost = EditorPaneHost,
            EditorChatSplitter = EditorChatSplitter,
            ContextSidebarPanel = ContextSidebarPanel,
            ContextSidebarSplitter = ContextSidebarSplitter,
            ContextSidebarCollapsedRail = ContextSidebarCollapsedRail
        });
        DataContext = _viewModel;
        _viewModelPropertyChangedHandler = OnViewModelPropertyChanged;
        _contextSidebarLayoutChangedHandler = (_, _) =>
            ExecuteOnUiThread(_layoutBinder.ApplyContextSidebar);
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
        _layoutBinder.ApplyAll();
        ComposerInput.ClipboardImageReader = _clipboardImageReader;
        _viewModel.AttachChatView(ChatWebView);
        RegisterChatScrollService();
    }

    private void RegisterChatScrollService()
    {
        if (Application.Current is not App { Services: { } services })
        {
            return;
        }

        var chatScrollService = services.GetService<IChatScrollService>();
        chatScrollService?.Register(
            () => _ = ChatWebView.ScrollToBottomAsync(),
            () => _ = ChatWebView.ScrollToBottomAsync());
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
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
        if (e.PropertyName == nameof(MainWindowViewModel.HasChatMessages))
        {
            ExecuteOnUiThread(_layoutBinder.ApplyContextSidebar);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.HasOpenEditorTabs))
        {
            ExecuteOnUiThread(_layoutBinder.ApplyEditorPane);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsBusy))
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

    private void WorkspaceTreeItem_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TreeViewItem { DataContext: WorkspaceTreeNodeViewModel node })
        {
            return;
        }

        _viewModel.Sidebar.ExpandWorkspaceTreeNode(node);
    }
}
