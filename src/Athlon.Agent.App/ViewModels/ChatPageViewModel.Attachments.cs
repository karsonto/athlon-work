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

/// <summary>Pending image and document attachments.</summary>
public sealed partial class ChatPageViewModel
{
    [ObservableProperty]
    private bool isReadingAttachments;

    public ObservableCollection<PendingImageAttachmentViewModel> PendingImageAttachments { get; } = new();
    public ObservableCollection<PendingDocumentAttachmentViewModel> PendingDocumentAttachments { get; } = new();
    public bool HasPendingImages => PendingImageAttachments.Count > 0;
    public bool HasPendingDocuments => PendingDocumentAttachments.Count > 0;
    public bool HasPendingAttachments => HasPendingImages || HasPendingDocuments;

    public void OnPendingImagesChanged()
    {
        OnPropertyChanged(nameof(HasPendingImages));
        OnPropertyChanged(nameof(HasPendingAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    public void OnPendingDocumentsChanged()
    {
        OnPropertyChanged(nameof(HasPendingDocuments));
        OnPropertyChanged(nameof(HasPendingAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SelectImagesAsync() => await SelectAttachmentsAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task SelectAttachmentsAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("Chat_SelectFiles"),
            Multiselect = true,
            Filter = Strings.Get("Chat_SelectFilesFilter")
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        await AddPendingFromFilePathsAsync(dialog.FileNames).ConfigureAwait(true);
    }

    public async Task AddPendingFromFilePathsAsync(IEnumerable<string> filePaths)
    {
        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var imagePaths = new List<string>();
        var rejected = new List<string>();

        foreach (var path in paths)
        {
            if (_documentExtractor.IsLegacyPresentation(path))
            {
                rejected.Add(Strings.Format("Chat_LegacyPptRejected", Path.GetFileName(path)));
                continue;
            }

            if (_documentExtractor.IsImageFile(path))
            {
                imagePaths.Add(path);
                continue;
            }

            if (_documentExtractor.IsSupportedDocument(path))
            {
                AddPendingDocument(path);
                continue;
            }

            rejected.Add(Strings.Format("Chat_UnsupportedAttachment", Path.GetFileName(path)));
        }

        if (imagePaths.Count > 0)
        {
            var images = await _imageAttachmentReader.ReadImagesAsync(imagePaths).ConfigureAwait(true);
            AddPendingImages(images);
        }

        if (rejected.Count > 0)
        {
            _showShellToast?.Invoke(rejected[0], ShellToastKind.Error);
        }
    }

    private void AddPendingDocument(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (PendingDocumentAttachments.Any(item =>
                string.Equals(item.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        PendingDocumentAttachments.Add(new PendingDocumentAttachmentViewModel(fullPath));
        OnPendingDocumentsChanged();
    }

    public void AddPendingImages(IEnumerable<ImageAttachment> images) =>
        _composer.AddPendingImages(images, PendingImageAttachments);

    [RelayCommand]
    private void RemovePendingImage(PendingImageAttachmentViewModel? image)
    {
        if (image is null)
        {
            return;
        }

        PendingImageAttachments.Remove(image);
    }

    [RelayCommand]
    private void RemovePendingDocument(PendingDocumentAttachmentViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        PendingDocumentAttachments.Remove(document);
        OnPendingDocumentsChanged();
    }

    partial void OnIsReadingAttachmentsChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
}

public sealed class PendingDocumentAttachmentViewModel(string filePath)
{
    public string FilePath { get; } = filePath;
    public string FileName { get; } = Path.GetFileName(filePath);
    public long FileSizeBytes { get; } = new FileInfo(filePath).Exists ? new FileInfo(filePath).Length : 0;
    public string SizeLabel => $"{FileSizeBytes / 1024.0:0.00} KB";
}
