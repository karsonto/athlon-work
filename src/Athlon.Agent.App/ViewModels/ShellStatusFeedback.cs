using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

/// <summary>
/// Ephemeral shell toast + sticky composer status used by chat-visible feedback.
/// </summary>
public sealed partial class ShellStatusFeedback : ObservableObject
{
    private CancellationTokenSource? _toastCts;

    /// <summary>Test seam for auto-hide timing.</summary>
    internal Func<int, CancellationToken, Task> DelayAsync { get; set; } =
        static (delayMs, cancellationToken) => Task.Delay(delayMs, cancellationToken);

    [ObservableProperty]
    private string toastMessage = string.Empty;

    [ObservableProperty]
    private bool isToastVisible;

    [ObservableProperty]
    private ShellToastKind toastKind = ShellToastKind.Info;

    [ObservableProperty]
    private string composerStatusText = string.Empty;

    [ObservableProperty]
    private bool isComposerStatusVisible;

    public static int GetToastHideDelayMs(ShellToastKind kind) =>
        kind == ShellToastKind.Error ? 4000 : 2400;

    public void ShowToast(string message, ShellToastKind kind = ShellToastKind.Info)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            IsToastVisible = false;
            return;
        }

        ToastMessage = message.Trim();
        ToastKind = kind;
        IsToastVisible = true;
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;
        _ = HideToastAsync(GetToastHideDelayMs(kind), token);
    }

    public void SetComposerStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            ComposerStatusText = string.Empty;
            IsComposerStatusVisible = false;
            return;
        }

        ComposerStatusText = message.Trim();
        IsComposerStatusVisible = true;
    }

    public void CancelPendingHide()
    {
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = null;
    }

    private async Task HideToastAsync(int delayMs, CancellationToken cancellationToken)
    {
        try
        {
            await DelayAsync(delayMs, cancellationToken);
            IsToastVisible = false;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer notice.
        }
    }
}
