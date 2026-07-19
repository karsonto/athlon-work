using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Views;

public partial class ChatPageView : UserControl, IChatLayoutSurface
{
    private static readonly TimeSpan LongPressThreshold = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _longPressTimer;
    private bool _sendPressActive;
    private bool _speechGestureActive;
    private bool _speechStopInFlight;

    public ChatPageView()
    {
        InitializeComponent();
        _longPressTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = LongPressThreshold
        };
        _longPressTimer.Tick += LongPressTimer_OnTick;
    }

    WebChatView IChatLayoutSurface.ChatWebView => ChatWebView;

    ComposerInputControl IChatLayoutSurface.ComposerInput => ComposerInput;

    public MainWindowLayoutElements ChatLayoutElements => new()
    {
        EditorPaneColumn = EditorPaneColumn,
        EditorPaneHost = EditorPaneHost,
        EditorChatSplitter = EditorChatSplitter,
        ComposerRow = ComposerRow
    };

    private void RunOnButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainShellViewModel shell)
        {
            return;
        }

        var menu = shell.BuildRunOnMenu();
        menu.PlacementTarget = RunOnButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void SendButton_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainShellViewModel)
        {
            return;
        }

        e.Handled = true;
        _sendPressActive = true;
        _speechGestureActive = false;
        _longPressTimer.Stop();
        _longPressTimer.Start();
    }

    private async void SendButton_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_sendPressActive && !_speechGestureActive)
        {
            return;
        }

        e.Handled = true;
        await CompleteSendPressAsync(invokeSendIfShortPress: true).ConfigureAwait(true);
    }

    private async void SendButton_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        // Capture is only taken after long-press begins.
        if (_speechStopInFlight || !_speechGestureActive)
        {
            return;
        }

        await CompleteSendPressAsync(invokeSendIfShortPress: false).ConfigureAwait(true);
    }

    private async void LongPressTimer_OnTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (!_sendPressActive || DataContext is not MainShellViewModel shell)
        {
            return;
        }

        _speechGestureActive = true;
        if (!SendButton.IsMouseCaptured)
        {
            SendButton.CaptureMouse();
        }

        await shell.StartSpeechInputAsync().ConfigureAwait(true);
    }

    private async Task CompleteSendPressAsync(bool invokeSendIfShortPress)
    {
        if (_speechStopInFlight)
        {
            return;
        }

        _speechStopInFlight = true;
        try
        {
            _longPressTimer.Stop();
            var wasSpeech = _speechGestureActive;
            var wasPress = _sendPressActive;
            _sendPressActive = false;
            _speechGestureActive = false;

            if (SendButton.IsMouseCaptured)
            {
                SendButton.ReleaseMouseCapture();
            }

            if (DataContext is not MainShellViewModel shell)
            {
                return;
            }

            if (wasSpeech || shell.IsSpeechListening)
            {
                await shell.StopSpeechInputAsync().ConfigureAwait(true);
                return;
            }

            if (invokeSendIfShortPress && wasPress && shell.SendCommand.CanExecute(null))
            {
                await shell.SendCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
        }
        finally
        {
            _speechStopInFlight = false;
        }
    }

    private void ComposerInputWrapper_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ComposerInputWrapper_OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainShellViewModel shell
            || e.Data.GetData(DataFormats.FileDrop) is not string[] files
            || files.Length == 0)
        {
            return;
        }

        e.Handled = true;
        await shell.AddPendingFromFilePathsAsync(files).ConfigureAwait(true);
    }
}
