namespace Athlon.Agent.App.ViewModels;

public sealed class QueuedTurnViewModel
{
    public QueuedTurnViewModel(string queueId, string previewText, int imageCount)
    {
        QueueId = queueId;
        PreviewText = previewText;
        ImageCount = imageCount;
    }

    public string QueueId { get; }
    public string PreviewText { get; }
    public int ImageCount { get; }
    public bool HasImages => ImageCount > 0;

    public static string BuildPreview(string input, int maxLength = 80)
    {
        var trimmed = input.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return trimmed[..maxLength] + "…";
    }
}
