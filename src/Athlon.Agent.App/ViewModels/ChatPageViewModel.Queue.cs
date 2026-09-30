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

/// <summary>Queued turns waiting to be sent.</summary>
public sealed partial class ChatPageViewModel
{
    [RelayCommand]
    private void RemoveQueuedTurn(QueuedTurnViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _sessionTurns.QueuedTurnPresenter.Remove(_getDisplayedSessionId!(), item.QueueId);
    }

    [RelayCommand]
    private void BeginEditQueuedTurn(QueuedTurnViewModel? item)
    {
        if (item is null || item.IsEditing)
        {
            return;
        }

        item.BeginEdit();
    }

    [RelayCommand]
    private void SaveQueuedTurn(QueuedTurnViewModel? item)
    {
        if (item is null || !item.IsEditing)
        {
            return;
        }

        var sessionId = _getDisplayedSessionId!();
        var trimmed = item.DraftText?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 && item.DraftImages.Count == 0)
        {
            _showShellToast?.Invoke(_loc["Nav_QueuedEmptyText"], ShellToastKind.Error);
            return;
        }

        var imageAttachments = _composer.PersistPendingImages(sessionId, item.DraftImages);
        if (!_sessionTurns.QueuedTurnPresenter.Update(sessionId, item.QueueId, trimmed, imageAttachments))
        {
            _showShellToast?.Invoke(_loc["Nav_QueuedEmptyText"], ShellToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task AddImagesToQueuedTurnAsync(QueuedTurnViewModel? item)
    {
        if (item is null || !item.IsEditing)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("Chat_SelectImages"),
            Multiselect = true,
            Filter = Strings.Get("Chat_SelectFilesFilter"),
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var imagePaths = dialog.FileNames
            .Where(path => !string.IsNullOrWhiteSpace(path)
                && File.Exists(path)
                && _documentExtractor.IsImageFile(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (imagePaths.Length == 0)
        {
            return;
        }

        var images = await _imageAttachmentReader.ReadImagesAsync(imagePaths).ConfigureAwait(true);
        item.AddDraftImages(images);
    }

    [RelayCommand]
    private void RemoveQueuedTurnImage(PendingImageAttachmentViewModel? image)
    {
        if (image is null)
        {
            return;
        }

        var sessionId = _getDisplayedSessionId!();
        var turn = _sessionTurns.QueuedTurnPresenter
            .GetForSession(sessionId)
            .FirstOrDefault(item => item.IsEditing && item.DraftImages.Contains(image));
        turn?.RemoveDraftImage(image);
    }

    [RelayCommand]
    private void CancelEditQueuedTurn(QueuedTurnViewModel? item)
    {
        item?.CancelEdit();
    }
}
