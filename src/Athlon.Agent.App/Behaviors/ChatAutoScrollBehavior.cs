using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Athlon.Agent.App;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xaml.Behaviors;

namespace Athlon.Agent.App.Behaviors;

public sealed class ChatAutoScrollBehavior : Behavior<ListBox>
{
    private ChatAutoScrollController? _controller;
    private RoutedEventHandler? _contentInteractionHandler;

    protected override void OnAttached()
    {
        base.OnAttached();
        _controller = new ChatAutoScrollController(AssociatedObject.Dispatcher, GetIsBusy);
        _contentInteractionHandler = (_, _) => _controller.OnContentInteractionChanged();
        AssociatedObject.AddHandler(
            MarkdownMessageView.ContentInteractionChangedEvent,
            _contentInteractionHandler);
        AssociatedObject.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        AssociatedObject.Loaded += OnListBoxLoaded;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.Loaded -= OnListBoxLoaded;
        AssociatedObject.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        if (_contentInteractionHandler is not null)
        {
            AssociatedObject.RemoveHandler(
                MarkdownMessageView.ContentInteractionChangedEvent,
                _contentInteractionHandler);
        }

        _controller?.Dispose();
        _controller = null;
        base.OnDetaching();
    }

    private void OnListBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        _controller.Attach(AssociatedObject);
        if (FindViewModel() is { } viewModel)
        {
            _controller.OnHasChatMessagesChanged(viewModel.HasChatMessages);
        }

        RegisterChatScrollService();
        _controller.ScrollToEnd(immediate: true);
    }

    private void RegisterChatScrollService()
    {
        if (_controller is null
            || Application.Current is not App { Services: { } services })
        {
            return;
        }

        var chatScrollService = services.GetService<IChatScrollService>();
        chatScrollService?.Register(
            () => _controller.ScrollToEnd(immediate: false),
            () => _controller.ScrollToEnd(immediate: true));
    }

    private bool GetIsBusy() => FindViewModel()?.IsBusy ?? false;

    private MainShellViewModel? FindViewModel()
    {
        if (AssociatedObject.DataContext is MainShellViewModel direct)
        {
            return direct;
        }

        return Window.GetWindow(AssociatedObject)?.DataContext as MainShellViewModel;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _controller?.HandlePreviewMouseLeftButtonDown();

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _controller?.HandlePreviewMouseLeftButtonUp();

    public void OnHasChatMessagesChanged(bool hasMessages) =>
        _controller?.OnHasChatMessagesChanged(hasMessages);

    public void OnStreamingStateChanged(bool isBusy) =>
        _controller?.OnStreamingStateChanged(isBusy);
}
