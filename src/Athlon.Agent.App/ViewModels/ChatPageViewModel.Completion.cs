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

/// <summary>@-mention and slash completion in the composer.</summary>
public sealed partial class ChatPageViewModel
{
    [ObservableProperty]
    private bool isAtCompletionOpen;

    [ObservableProperty]
    private int selectedAtCompletionIndex = -1;

    public ObservableCollection<AtCompletionItemViewModel> AtCompletionItems { get; } = new();

    public void UpdateComposerCompletion(string composerText, int caretIndex) =>
        UpdateAtCompletion(composerText, caretIndex);

    public void UpdateAtCompletion(string composerText, int caretIndex) =>
        _composer.UpdateAtCompletion(
            composerText,
            caretIndex,
            _getSession!().ActiveWorkspace,
            _getIgnorePatterns!(),
            AtCompletionItems,
            open => IsAtCompletionOpen = open,
            index => SelectedAtCompletionIndex = index,
            SelectedAtCompletionIndex);

    public void MoveAtCompletionSelection(int delta) =>
        _composer.MoveSelection(
            delta,
            IsAtCompletionOpen,
            AtCompletionItems.Count,
            SelectedAtCompletionIndex,
            index => SelectedAtCompletionIndex = index);

    public bool TryAcceptAtCompletion(int caretIndex, out int newCaretIndex) =>
        _composer.TryAcceptAtCompletion(
            ComposerText,
            caretIndex,
            IsAtCompletionOpen,
            SelectedAtCompletionIndex,
            AtCompletionItems,
            text => ComposerText = text,
            CloseAtCompletion,
            _createSlashCommandContext?.Invoke(),
            out newCaretIndex);

    public async Task<bool> TryExecuteSlashCommandAsync()
    {
        var context = _createSlashCommandContext?.Invoke();
        if (context is null || string.IsNullOrWhiteSpace(ComposerText))
        {
            return false;
        }

        return await _composer.TryExecuteSlashCommandAsync(
            ComposerText,
            context,
            text => ComposerText = text).ConfigureAwait(true);
    }

    public void CloseAtCompletion() =>
        _composer.CloseAtCompletion(
            AtCompletionItems,
            _ => IsAtCompletionOpen = false,
            index => SelectedAtCompletionIndex = index);
}
