using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.Services.Speech;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

/// <summary>Speech-to-text capture into the composer.</summary>
public sealed partial class ChatPageViewModel
{
    public bool IsSpeechInputAvailable => _speechToText.IsAvailable;

    [ObservableProperty]
    private bool isSpeechListening;

    public string SendButtonToolTip => IsSpeechListening
        ? _loc["Chat_SpeechListeningTooltip"]
        : IsSpeechInputAvailable
            ? _loc["Chat_SendTooltipWithSpeech"]
            : _loc["Chat_SendTooltip"];

    public async Task StartSpeechInputAsync()
    {
        if (!IsSpeechListening)
        {
            IsSpeechListening = true;
            _speechDraftBase = ComposerText;
        }

        // Immediate UI feedback so long-press never feels like a no-op.
        _setComposerStatus?.Invoke(_loc["Chat_SpeechListeningTooltip"]);

        if (_speechToText.IsListening)
        {
            return;
        }

        await _speechToText.StartListeningAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsSpeechInputAvailable));
        OnPropertyChanged(nameof(SendButtonToolTip));
        SendCommand.NotifyCanExecuteChanged();
    }

    public async Task StopSpeechInputAsync()
    {
        if (!IsSpeechListening && !_speechToText.IsListening)
        {
            return;
        }

        try
        {
            await _speechToText.StopListeningAsync().ConfigureAwait(true);
        }
        finally
        {
            IsSpeechListening = false;
            _speechDraftBase = null;
            _setComposerStatus?.Invoke(null);
        }
    }

    private async Task InitializeSpeechAsync()
    {
        try
        {
            await _speechToText.ProbeAvailabilityAsync().ConfigureAwait(true);
        }
        catch
        {
            // Probe failures stay silent; long-press speech stays disabled.
        }

        OnPropertyChanged(nameof(IsSpeechInputAvailable));
        OnPropertyChanged(nameof(SendButtonToolTip));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void OnSpeechAvailabilityChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            OnPropertyChanged(nameof(IsSpeechInputAvailable));
            OnPropertyChanged(nameof(SendButtonToolTip));
            SendCommand.NotifyCanExecuteChanged();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        dispatcher.Invoke(Apply);
    }

    private void OnSpeechPartialText(object? sender, string text)
    {
        void Apply()
        {
            var baseline = _speechDraftBase ?? ComposerText;
            ComposerText = ComposerSpeechText.AppendTranscript(baseline, text);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        dispatcher.Invoke(Apply);
    }

    private void OnSpeechFinalText(object? sender, string text)
    {
        void Apply()
        {
            var baseline = _speechDraftBase ?? ComposerText;
            ComposerText = ComposerSpeechText.AppendTranscript(baseline, text);
            _speechDraftBase = null;
            _setComposerStatus?.Invoke(null);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        dispatcher.Invoke(Apply);
    }

    private void OnSpeechFailed(object? sender, string message)
    {
        void Apply()
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _showShellToast?.Invoke(message, ShellToastKind.Error);
                _setComposerStatus?.Invoke(null);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        dispatcher.Invoke(Apply);
    }

    private void OnCultureChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(SendButtonToolTip));

    partial void OnIsSpeechListeningChanged(bool value) =>
        OnPropertyChanged(nameof(SendButtonToolTip));
}
