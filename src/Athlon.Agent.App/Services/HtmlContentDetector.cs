namespace Athlon.Agent.App.Services;

public static class HtmlContentDetector
{
    private static readonly string[] HtmlMarkers =
    [
        "<!doctype",
        "<html",
        "<body",
        "<div",
        "<section",
        "<article",
        "<table",
        "<style",
        "<script"
    ];

    public static bool LooksLikeHtml(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return HtmlMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
