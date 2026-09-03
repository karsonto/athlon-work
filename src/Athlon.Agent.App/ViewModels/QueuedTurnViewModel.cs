using System.Collections.ObjectModel;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class QueuedTurnViewModel : ObservableObject
{
    private IReadOnlyList<ImageAttachment> _images = Array.Empty<ImageAttachment>();
    private QueuedTurnImageViewModel[] _imageItems = Array.Empty<QueuedTurnImageViewModel>();
    private int _imageCount;

    public QueuedTurnViewModel(
        string queueId,
        string previewText,
        string textContent,
        IReadOnlyList<ImageAttachment> images)
    {
        QueueId = queueId;
        PreviewText = previewText;
        TextContent = textContent;
        DraftText = textContent;
        SetImages(images);
    }

    public string QueueId { get; }

    [ObservableProperty]
    private string previewText;

    [ObservableProperty]
    private string textContent;

    [ObservableProperty]
    private string draftText;

    [ObservableProperty]
    private bool isEditing;

    public ObservableCollection<PendingImageAttachmentViewModel> DraftImages { get; } = new();

    public IReadOnlyList<ImageAttachment> Images => _images;

    public IReadOnlyList<QueuedTurnImageViewModel> ImageItems => _imageItems;

    public int ImageCount => _imageCount;

    public bool HasImages => ImageCount > 0;

    public bool HasDraftImages => DraftImages.Count > 0;

    public bool HasText => !string.IsNullOrWhiteSpace(TextContent);

    public void BeginEdit()
    {
        DraftText = TextContent;
        DraftImages.Clear();
        foreach (var image in Images)
        {
            DraftImages.Add(new PendingImageAttachmentViewModel(image));
        }

        IsEditing = true;
    }

    public void CancelEdit()
    {
        DraftText = TextContent;
        DraftImages.Clear();
        IsEditing = false;
    }

    public void ApplySaved(string text, IReadOnlyList<ImageAttachment> images)
    {
        TextContent = text;
        SetImages(images);
        PreviewText = BuildPreview(text, images.Count);
        DraftText = text;
        DraftImages.Clear();
        IsEditing = false;
    }

    public void AddDraftImages(IEnumerable<ImageAttachment> images)
    {
        foreach (var image in images)
        {
            if (DraftImages.Any(existing => ImageAttachmentsMatch(existing.Attachment, image)))
            {
                continue;
            }

            DraftImages.Add(new PendingImageAttachmentViewModel(image));
        }

        OnPropertyChanged(nameof(HasDraftImages));
    }

    public void RemoveDraftImage(PendingImageAttachmentViewModel? image)
    {
        if (image is null)
        {
            return;
        }

        DraftImages.Remove(image);
        OnPropertyChanged(nameof(HasDraftImages));
    }

    public static QueuedTurnViewModel Create(
        string queueId,
        string userInput,
        IReadOnlyList<ImageAttachment> images) =>
        new(
            queueId,
            BuildPreview(userInput, images.Count),
            userInput,
            images);

    public static string BuildPreview(string input, int imageCount, int maxLength = 80)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return imageCount > 0 ? $"（{imageCount} 张图片）" : string.Empty;
        }

        var suffix = imageCount > 0 ? $" · {imageCount} 张图片" : string.Empty;
        var combined = trimmed + suffix;
        if (combined.Length <= maxLength)
        {
            return combined;
        }

        var budget = maxLength - suffix.Length - 1;
        if (budget < 8)
        {
            return trimmed[..Math.Min(trimmed.Length, maxLength)] + "…";
        }

        return trimmed[..budget] + "…" + suffix;
    }

    private void SetImages(IReadOnlyList<ImageAttachment> images)
    {
        _images = images;
        _imageCount = images.Count;
        _imageItems = images.Select(image => new QueuedTurnImageViewModel(image)).ToArray();
        OnPropertyChanged(nameof(Images));
        OnPropertyChanged(nameof(ImageItems));
        OnPropertyChanged(nameof(ImageCount));
        OnPropertyChanged(nameof(HasImages));
    }

    private static bool ImageAttachmentsMatch(ImageAttachment left, ImageAttachment right) =>
        (!string.IsNullOrWhiteSpace(left.LocalPath)
            && string.Equals(left.LocalPath, right.LocalPath, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(left.DataUrl)
            && string.Equals(left.DataUrl, right.DataUrl, StringComparison.Ordinal));
}

public sealed class QueuedTurnImageViewModel
{
    public QueuedTurnImageViewModel(ImageAttachment attachment)
    {
        Attachment = attachment;
        FileName = attachment.FileName;
        Thumbnail = ImageAttachmentUi.TryCreateThumbnail(attachment);
    }

    public ImageAttachment Attachment { get; }
    public string FileName { get; }
    public System.Windows.Media.ImageSource? Thumbnail { get; }
}
