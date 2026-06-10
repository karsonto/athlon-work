using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.ViewModels;

public sealed class QueuedTurnViewModel
{
    public QueuedTurnViewModel(
        string queueId,
        string previewText,
        string textContent,
        IReadOnlyList<ImageAttachment> images)
    {
        QueueId = queueId;
        PreviewText = previewText;
        TextContent = textContent;
        Images = images;
        ImageCount = images.Count;
        ImageItems = images.Select(image => new QueuedTurnImageViewModel(image)).ToArray();
    }

    public string QueueId { get; }
    public string PreviewText { get; }
    public string TextContent { get; }
    public IReadOnlyList<ImageAttachment> Images { get; }
    public IReadOnlyList<QueuedTurnImageViewModel> ImageItems { get; }
    public int ImageCount { get; }
    public bool HasImages => ImageCount > 0;
    public bool HasText => !string.IsNullOrWhiteSpace(TextContent);

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
}

public sealed class QueuedTurnImageViewModel
{
    public QueuedTurnImageViewModel(ImageAttachment attachment)
    {
        FileName = attachment.FileName;
        Thumbnail = ImageAttachmentUi.TryCreateThumbnail(attachment);
    }

    public string FileName { get; }
    public System.Windows.Media.ImageSource? Thumbnail { get; }
}
